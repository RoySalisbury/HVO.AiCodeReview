using System.Diagnostics;
using System.Text;
using AiCodeReview.Models;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Services;

public class CodeReviewOrchestrator : ICodeReviewOrchestrator
{
    private readonly IDevOpsService _devOpsService;
    private readonly ICodeReviewServiceResolver _passResolver;
    private readonly VectorStoreReviewService _vectorService;
    private readonly ModelAdapterResolver _modelAdapterResolver;
    private readonly AzureDevOpsSettings _devOpsSettings;
    private readonly AiProviderSettings _aiProviderSettings;
    private readonly AssistantsSettings _assistantsSettings;
    private readonly SizeGuardrailsSettings _sizeGuardrails;
    private readonly IReviewRateLimiter _rateLimiter;
    private readonly IGlobalRateLimitSignal _globalRateLimitSignal;
    private readonly ILogger<CodeReviewOrchestrator> _logger;

    public CodeReviewOrchestrator(
        IDevOpsService devOpsService,
        ICodeReviewServiceResolver passResolver,
        VectorStoreReviewService vectorService,
        ModelAdapterResolver modelAdapterResolver,
        IOptions<AzureDevOpsSettings> devOpsSettings,
        IOptions<AiProviderSettings> aiProviderSettings,
        IOptions<AssistantsSettings> assistantsSettings,
        IOptions<SizeGuardrailsSettings> sizeGuardrails,
        IReviewRateLimiter rateLimiter,
        IGlobalRateLimitSignal globalRateLimitSignal,
        ILogger<CodeReviewOrchestrator> logger)
    {
        _devOpsService = devOpsService;
        _passResolver = passResolver;
        _vectorService = vectorService;
        _modelAdapterResolver = modelAdapterResolver;
        _devOpsSettings = devOpsSettings.Value;
        _aiProviderSettings = aiProviderSettings.Value;
        _assistantsSettings = assistantsSettings.Value;
        _sizeGuardrails = sizeGuardrails.Value;
        _rateLimiter = rateLimiter;
        _globalRateLimitSignal = globalRateLimitSignal;
        _logger = logger;
    }

    /// <summary>
    /// Resolves the AI review service for a specific review pass and depth.
    /// Resolution order: PassRouting → DepthModels → ActiveProvider.
    /// </summary>
    private ICodeReviewService GetServiceForPass(ReviewPass pass, ReviewDepth depth)
        => _passResolver.GetService(pass, depth);

    /// <summary>
    /// Calculates the estimated cost in USD for the given model and token usage.
    /// Returns null if the model has no pricing configured.
    /// </summary>
    private decimal? CalculateEstimatedCost(string? modelName, int? promptTokens, int? completionTokens)
    {
        if (string.IsNullOrWhiteSpace(modelName) || promptTokens is null || completionTokens is null)
            return null;

        var adapter = _modelAdapterResolver.Resolve(modelName);
        var cost = adapter.CalculateCost(promptTokens.Value, completionTokens.Value);

        if (cost.HasValue)
            _logger.LogInformation("Estimated cost for model '{Model}': ${Cost:F6} ({PromptTokens} prompt + {CompletionTokens} completion tokens)",
                modelName, cost.Value, promptTokens.Value, completionTokens.Value);

        return cost;
    }

    public async Task<ReviewResponse> ExecuteReviewAsync(
        string project,
        string repository,
        int pullRequestId,
        IProgress<ReviewStatusUpdate>? progress = null,
        bool forceReview = false,
        bool simulationOnly = false,
        ReviewDepth reviewDepth = ReviewDepth.Standard,
        ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (simulationOnly)
                _logger.LogInformation("SIMULATION MODE — review will NOT post anything to PR #{PrId}", pullRequestId);

            // ── Step 0: Rate-limit check (in-memory, no API calls) ──────
            var (allowed, secondsRemaining, lastReviewedUtc) = _rateLimiter.Check(
                _devOpsSettings.Organization, project, repository, pullRequestId,
                _devOpsSettings.MinReviewIntervalMinutes);

            if (!allowed && !forceReview && !simulationOnly)
            {
                var nextAllowed = lastReviewedUtc!.Value.AddMinutes(_devOpsSettings.MinReviewIntervalMinutes);
                ReportProgress(progress, ReviewStep.Complete,
                    $"Rate limited — next review allowed in {secondsRemaining}s.", 100);

                return new ReviewResponse
                {
                    Status = "RateLimited",
                    Summary = $"This PR was reviewed too recently. " +
                              $"Please wait {secondsRemaining} seconds before requesting another review. " +
                              $"(Cooldown: {_devOpsSettings.MinReviewIntervalMinutes} min, " +
                              $"last reviewed: {lastReviewedUtc.Value:yyyy-MM-dd HH:mm:ss} UTC, " +
                              $"next allowed: {nextAllowed:yyyy-MM-dd HH:mm:ss} UTC)",
                };
            }

            // ── Step 1: Gather PR state and review history ──────────────────
            ReportProgress(progress, ReviewStep.CheckingReviewStatus,
                "Checking PR state and review history...", 5);

            var prInfo = await _devOpsService.GetPullRequestAsync(project, repository, pullRequestId);
            var metadata = await _devOpsService.GetReviewMetadataAsync(project, repository, pullRequestId);
            var currentIteration = await _devOpsService.GetIterationCountAsync(project, repository, pullRequestId);

            _logger.LogInformation(
                "PR #{PrId}: '{Title}' by {Author} | Draft={IsDraft} | SourceCommit={Commit} | Iteration={Iter}",
                prInfo.PullRequestId, prInfo.Title, prInfo.CreatedBy,
                prInfo.IsDraft, prInfo.LastMergeSourceCommit, currentIteration);

            // ── Step 2: Decide what action to take ──────────────────────────
            var action = (forceReview || simulationOnly)
                ? ReviewAction.ReReview
                : DetermineAction(prInfo, metadata, currentIteration);

            if (forceReview)
                _logger.LogInformation("Force-review requested for PR #{PrId} — bypassing skip/dedup logic.", pullRequestId);
            else if (simulationOnly)
                _logger.LogInformation("Simulation mode for PR #{PrId} — bypassing skip/dedup logic (nothing will be posted).", pullRequestId);

            _logger.LogInformation("Review action for PR #{PrId}: {Action}", pullRequestId, action);

            switch (action)
            {
                case ReviewAction.Skip:
                    return await HandleSkipAsync(
                        project, repository, pullRequestId, prInfo, metadata, currentIteration, progress);

                case ReviewAction.VoteOnly:
                    return await HandleVoteOnlyAsync(project, repository, pullRequestId, prInfo, metadata, progress);

                case ReviewAction.FullReview:
                case ReviewAction.ReReview:
                    return await HandleReviewAsync(
                        project, repository, pullRequestId, prInfo, metadata,
                        currentIteration, action == ReviewAction.ReReview, progress,
                        simulationOnly, reviewDepth, reviewStrategy, cancellationToken);

                default:
                    throw new InvalidOperationException($"Unexpected review action: {action}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error reviewing PR #{PrId} in {Project}/{Repo}",
                pullRequestId, project, repository);

            return new ReviewResponse
            {
                Status = "Error",
                ErrorMessage = ex.Message,
            };
        }
    }

    // ── Action Determination ────────────────────────────────────────────────

    private enum ReviewAction { FullReview, ReReview, VoteOnly, Skip }

    private ReviewAction DetermineAction(PullRequestInfo prInfo, ReviewMetadata metadata, int currentIteration)
    {
        // Never reviewed before → full review
        if (!metadata.HasPreviousReview)
        {
            _logger.LogInformation("No previous review found — full review needed.");
            return ReviewAction.FullReview;
        }

        // Code changed since last review → re-review
        if (metadata.HasCodeChanged(prInfo.LastMergeSourceCommit))
        {
            _logger.LogInformation(
                "Source commit changed: {Old} → {New} — re-review needed.",
                metadata.LastReviewedSourceCommit, prInfo.LastMergeSourceCommit);
            return ReviewAction.ReReview;
        }

        // Same code, draft → active transition, vote not yet submitted → vote only
        if (metadata.IsDraftToActiveTransition(prInfo.IsDraft, prInfo.LastMergeSourceCommit)
            && !metadata.VoteSubmitted
            && _devOpsSettings.AddReviewerVote)
        {
            _logger.LogInformation("Draft-to-active transition with no code changes — vote only.");
            return ReviewAction.VoteOnly;
        }

        // Same code, already reviewed, already voted (or voting disabled/draft) → skip
        _logger.LogInformation("No new changes since last review — skipping.");
        return ReviewAction.Skip;
    }

    // ── Skip Flow (no new changes) ──────────────────────────────────────────

    private async Task<ReviewResponse> HandleSkipAsync(
        string project, string repository, int pullRequestId,
        PullRequestInfo prInfo, ReviewMetadata metadata, int currentIteration,
        IProgress<ReviewStatusUpdate>? progress)
    {
        ReportProgress(progress, ReviewStep.Complete,
            "PR has already been fully reviewed. No new changes detected. Recording skip.", 90);

        // Derive review number from history (includes all prior events)
        var existingHistory = await _devOpsService.GetReviewHistoryAsync(project, repository, pullRequestId);
        var nextEventNumber = existingHistory.Count + 1;

        var skipEntry = new ReviewHistoryEntry
        {
            ReviewNumber = nextEventNumber,
            ReviewedAtUtc = DateTime.UtcNow,
            Action = "Skipped",
            Verdict = "No Changes",
            SourceCommit = prInfo.LastMergeSourceCommit,
            Iteration = currentIteration,
            IsDraft = prInfo.IsDraft,
            InlineComments = 0,
            FilesChanged = 0,
            Vote = null,
        };

        // Store in PR properties (canonical source of truth)
        await _devOpsService.AppendReviewHistoryAsync(project, repository, pullRequestId, skipEntry);

        // Also append to PR description (visual convenience)
        await AppendReviewHistoryToDescriptionAsync(project, repository, pullRequestId, prInfo, skipEntry);

        // Record in rate limiter so back-to-back calls are blocked
        _rateLimiter.Record(_devOpsSettings.Organization, project, repository, pullRequestId);

        ReportProgress(progress, ReviewStep.Complete,
            "PR has already been fully reviewed. No new changes detected.", 100);

        return new ReviewResponse
        {
            Status = "Skipped",
            Summary = "This PR has already been reviewed. No new changes detected since the last review.",
        };
    }

    // ── Vote-Only Flow (draft → active transition) ──────────────────────────

    private async Task<ReviewResponse> HandleVoteOnlyAsync(
        string project, string repository, int pullRequestId,
        PullRequestInfo prInfo, ReviewMetadata metadata,
        IProgress<ReviewStatusUpdate>? progress)
    {
        ReportProgress(progress, ReviewStep.SubmittingVote,
            "Draft-to-active transition — submitting reviewer vote...", 80);

        bool voteFailed = false;
        // Use the last review's recommended vote. Default to 5 (approved with suggestions) if unknown.
        int vote = 5;

        try
        {
            await _devOpsService.AddReviewerAsync(project, repository, pullRequestId, vote);
            _logger.LogInformation("Added reviewer vote {Vote} for PR #{PrId} (draft-to-active transition)",
                vote, pullRequestId);
        }
        catch (HttpRequestException ex)
        {
            voteFailed = true;
            _logger.LogWarning(ex, "Failed to submit vote for PR #{PrId} during draft-to-active transition.", pullRequestId);
        }

        // Update metadata to reflect vote was submitted
        if (!voteFailed)
        {
            metadata.VoteSubmitted = true;
            metadata.WasDraft = false;

            // Derive review number from history (resilient to metadata resets)
            var existingHistory = await _devOpsService.GetReviewHistoryAsync(project, repository, pullRequestId);
            var nextReviewNumber = existingHistory.Count + 1;

            metadata.ReviewCount = nextReviewNumber;
            metadata.ReviewedAtUtc = DateTime.UtcNow;
            await _devOpsService.SetReviewMetadataAsync(project, repository, pullRequestId, metadata);

            // Append to PR description history
            var voteHistory = new ReviewHistoryEntry
            {
                ReviewNumber = nextReviewNumber,
                ReviewedAtUtc = metadata.ReviewedAtUtc,
                Action = "Vote Only",
                Verdict = "Approved w/ Suggestions (vote)",
                Vote = vote,
                SourceCommit = prInfo.LastMergeSourceCommit,
                Iteration = await _devOpsService.GetIterationCountAsync(project, repository, pullRequestId),
                IsDraft = false,
                InlineComments = 0,
                FilesChanged = 0,
            };
            // Store in PR properties (canonical source of truth)
            await _devOpsService.AppendReviewHistoryAsync(project, repository, pullRequestId, voteHistory);
            // Also append to PR description (visual convenience)
            await AppendReviewHistoryToDescriptionAsync(project, repository, pullRequestId, prInfo, voteHistory);
        }

        // Record in rate limiter
        _rateLimiter.Record(_devOpsSettings.Organization, project, repository, pullRequestId);

        ReportProgress(progress, ReviewStep.Complete,
            voteFailed ? "Vote submission failed." : "Vote submitted for previously-reviewed PR.", 100);

        return new ReviewResponse
        {
            Status = "Reviewed",
            Recommendation = "ApprovedWithSuggestions",
            Summary = "Draft-to-active transition: reviewer vote added for previously-reviewed PR. No re-review needed — code has not changed.",
            Vote = voteFailed ? null : vote,
            ErrorMessage = voteFailed ? "Failed to submit reviewer vote. Check server logs." : null,
        };
    }

    // ── Full / Re-Review Flow ───────────────────────────────────────────────

    private async Task<ReviewResponse> HandleReviewAsync(
        string project, string repository, int pullRequestId,
        PullRequestInfo prInfo, ReviewMetadata metadata,
        int currentIteration, bool isReReview,
        IProgress<ReviewStatusUpdate>? progress,
        bool simulationOnly = false,
        ReviewDepth reviewDepth = ReviewDepth.Standard,
        ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var totalSw = Stopwatch.StartNew();
        var reviewLabel = isReReview ? "Re-review" : "Review";

        // Derive next review number from existing summary comments (survives metadata clears)
        var existingSummaryCount = await _devOpsService.CountReviewSummaryCommentsAsync(project, repository, pullRequestId);
        var nextReviewNumber = existingSummaryCount + 1;

        // ── Fetch file changes ──────────────────────────────────────────
        ReportProgress(progress, ReviewStep.RetrievingChanges,
            "Retrieving file changes...", 20);

        var fileChanges = await _devOpsService.GetPullRequestChangesAsync(
            project, repository, pullRequestId, prInfo);

        _logger.LogInformation("{Label}: Retrieved {Count} file changes for PR #{PrId}",
            reviewLabel, fileChanges.Count, pullRequestId);

        // ── Pre-filter: classify and separate non-reviewable files ───────
        var allFileCount = fileChanges.Count;
        var skippedFiles = new List<FileChange>();
        var reviewableFiles = new List<FileChange>();

        foreach (var fc in fileChanges)
        {
            var skipReason = ClassifyNonReviewableFile(fc);
            if (skipReason != null)
            {
                fc.SkipReason = skipReason;
                skippedFiles.Add(fc);
            }
            else
            {
                reviewableFiles.Add(fc);
            }
        }

        if (skippedFiles.Count > 0)
        {
            // Group by reason for concise logging
            var grouped = skippedFiles.GroupBy(f => f.SkipReason).OrderByDescending(g => g.Count());
            var groupSummary = string.Join(", ", grouped.Select(g => $"{g.Count()} {g.Key}"));
            _logger.LogInformation("{Label}: Skipped {SkipCount}/{TotalCount} non-reviewable files ({Groups})",
                reviewLabel, skippedFiles.Count, allFileCount, groupSummary);
        }

        // Replace fileChanges with only reviewable files for AI analysis
        fileChanges = reviewableFiles;

        // ── Size guardrails: warn if PR is large, optionally trim ────────
        fileChanges = PrioritizeFiles(fileChanges);

        var sizeWarning = EvaluateSizeGuardrails(fileChanges, _sizeGuardrails);
        if (sizeWarning != null)
        {
            _logger.LogWarning("{Label}: {Warning}", reviewLabel, sizeWarning);
        }

        var deferredFiles = new List<FileChange>();
        if (_sizeGuardrails.FocusModeEnabled && fileChanges.Count > _sizeGuardrails.FocusModeMaxFiles)
        {
            deferredFiles = fileChanges.Skip(_sizeGuardrails.FocusModeMaxFiles).ToList();
            fileChanges = fileChanges.Take(_sizeGuardrails.FocusModeMaxFiles).ToList();

            foreach (var df in deferredFiles)
                df.SkipReason = "deferred (focus mode)";
            skippedFiles.AddRange(deferredFiles);

            _logger.LogInformation("{Label}: Focus mode active — reviewing top {Kept} files, deferred {Deferred} lower-priority files",
                reviewLabel, fileChanges.Count, deferredFiles.Count);

            // Update the warning to mention focus-mode trimming
            sizeWarning = (sizeWarning ?? "Large PR detected.") +
                $" Focus mode is active — reviewing the top {fileChanges.Count} highest-priority files; " +
                $"{deferredFiles.Count} lower-priority file(s) were deferred.";
        }

        if (fileChanges.Count == 0)
        {
            _logger.LogWarning("No reviewable file changes found for PR #{PrId}", pullRequestId);

            if (!simulationOnly)
            {
                var skipDetail = skippedFiles.Count > 0
                    ? $"\n\n> :file_folder: **{skippedFiles.Count} file(s) excluded from review:**\n\n" +
                      string.Join("\n", skippedFiles.Select(f => $"> - `{f.FilePath}` — {f.SkipReason}"))
                    : "";

                await _devOpsService.PostCommentThreadAsync(project, repository, pullRequestId,
                    "## Code Review -- PR " + pullRequestId + "\n\nNo reviewable file changes found in this PR." + skipDetail,
                    "closed");

                var noFilesHistory = new ReviewHistoryEntry
                {
                    Action = isReReview ? "Re-Review" : "Full Review",
                    Verdict = "Approved (auto — no files)",
                    SourceCommit = prInfo.LastMergeSourceCommit,
                    Iteration = currentIteration,
                    IsDraft = prInfo.IsDraft,
                    InlineComments = 0,
                    FilesChanged = 0,
                };
                await UpdateMetadataAndTag(project, repository, pullRequestId, prInfo, currentIteration, false, noFilesHistory);
            }

            ReportProgress(progress, ReviewStep.Complete,
                "No reviewable files found. Auto-approved.", 100);

            return new ReviewResponse
            {
                Status = simulationOnly ? "Simulated" : "Reviewed",
                Recommendation = "Approved",
                ReviewDepth = reviewDepth.ToString(),
                Summary = "No reviewable file changes found. Auto-approved.",
                Vote = 10,
                SkippedFiles = simulationOnly && skippedFiles.Count > 0
                    ? skippedFiles.Select(f => new SkippedFileDto { FilePath = f.FilePath, SkipReason = f.SkipReason! }).ToList()
                    : null,
            };
        }

        // ── Fetch linked work items (AC/DoD context) ───────────────────
        ReportProgress(progress, ReviewStep.AnalyzingCode,
            "Retrieving linked work items for AC/DoD context...", 30);

        List<WorkItemInfo>? workItems = null;
        try
        {
            var workItemIds = await _devOpsService.GetLinkedWorkItemIdsAsync(project, repository, pullRequestId);
            if (workItemIds.Count > 0)
            {
                workItems = new List<WorkItemInfo>();
                foreach (var wiId in workItemIds)
                {
                    var wi = await _devOpsService.GetWorkItemAsync(project, wiId);
                    if (wi != null)
                    {
                        // Fetch discussion comments for AC modification context
                        var comments = await _devOpsService.GetWorkItemCommentsAsync(project, wiId);
                        wi.Comments = comments;
                        workItems.Add(wi);
                    }
                }
                _logger.LogInformation("Retrieved {Count} work item(s) with AC/DoD context for PR #{PrId}",
                    workItems.Count, pullRequestId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to retrieve work items for PR #{PrId} — continuing without AC context", pullRequestId);
        }

        // ── Resolve per-pass AI services ─────────────────────────────
        var pass1Service = GetServiceForPass(ReviewPass.PrSummary, reviewDepth);

        // ── Pass 1: PR-level summary (cross-file context) ────────────
        var prSummary = await BuildPass1SummaryAsync(
            pass1Service, prInfo, fileChanges, workItems, pullRequestId, progress);

        // Track per-pass model names for metrics
        var passModels = new Dictionary<string, string>
        {
            [ReviewPass.PrSummary.ToString()] = pass1Service.ModelName,
        };

        // ── Quick mode: Pass 1 only — skip per-file reviews ────────────
        CodeReviewResult reviewResult;
        DeepAnalysisResult? deepAnalysis = null;

        if (reviewDepth == ReviewDepth.Quick)
        {
            _logger.LogInformation("[Quick] Skipping Pass 2 (per-file reviews) for PR #{PrId} — Quick mode uses Pass 1 summary only", pullRequestId);

            reviewResult = BuildQuickModeResult(prSummary, fileChanges, skippedFiles);

            // Add Pass 1 token usage
            if (prSummary != null)
            {
                reviewResult.PromptTokens = prSummary.PromptTokens;
                reviewResult.CompletionTokens = prSummary.CompletionTokens;
                reviewResult.TotalTokens = prSummary.TotalTokens;
                reviewResult.AiDurationMs = prSummary.AiDurationMs;
                reviewResult.ModelName = prSummary.ModelName;
            }

            _logger.LogInformation("[Quick] PR #{PrId}: {Verdict} (no inline comments — Quick mode)", pullRequestId, reviewResult.Summary.Verdict);
        }
        else
        {
            // Resolve Pass 2 and (optionally) Pass 3 services
            var pass2Service = GetServiceForPass(ReviewPass.PerFileReview, reviewDepth);
            passModels[ReviewPass.PerFileReview.ToString()] = pass2Service.ModelName;

            ICodeReviewService? pass3Service = null;
            if (reviewDepth == ReviewDepth.Deep)
            {
                pass3Service = GetServiceForPass(ReviewPass.DeepReview, reviewDepth);
                passModels[ReviewPass.DeepReview.ToString()] = pass3Service.ModelName;
            }

            // Standard or Deep — run Pass 2 (and optionally Pass 3)
            (reviewResult, deepAnalysis) = await HandleStandardOrDeepPassesAsync(
                prInfo, pullRequestId, fileChanges, prSummary, workItems,
                reviewDepth, reviewStrategy, reviewLabel, pass2Service, pass3Service, progress, cancellationToken);
        }

        // Store aggregated pass model information
        reviewResult.PassModels = passModels;

        // ── Validate & sanitize inline comments from AI ─────────────────
        var validatedComments = ValidateInlineComments(reviewResult.InlineComments, fileChanges);

        // ── Demote generic L1-1 comments ─────────────────────────────
        // AI sometimes produces file-level observations with startLine=1,endLine=1.
        // These aren't real line-specific findings — just drop them.
        var lineSpecificComments = new List<InlineComment>();
        foreach (var c in validatedComments)
        {
            if (c.StartLine == 1 && c.EndLine == 1)
            {
                _logger.LogInformation("Dropped L1-1 generic inline comment: {File} — {LeadIn}", c.FilePath, c.LeadIn);
            }
            else
            {
                lineSpecificComments.Add(c);
            }
        }

        if (validatedComments.Count > lineSpecificComments.Count)
        {
            _logger.LogInformation("Dropped {Demoted}/{Total} generic L1-1 inline comments; {Kept} line-specific comments remain",
                validatedComments.Count - lineSpecificComments.Count, validatedComments.Count, lineSpecificComments.Count);
        }

        // ── Build attribution tag suffix ────────────────────────────────
        var attributionTag = _devOpsSettings.CommentAttributionTag;
        var attributionSuffix = !string.IsNullOrEmpty(attributionTag)
            ? $"\n\n_[{attributionTag}]_"
            : "";

        if (!simulationOnly)
        {
            // ── Post inline comments (with thread resolution + dedup) ────
            var threadService = GetServiceForPass(ReviewPass.ThreadVerification, reviewDepth);
            passModels[ReviewPass.ThreadVerification.ToString()] = threadService.ModelName;
            var (_, postedComments, _, _) =
                await PostInlineCommentsAsync(
                    project, repository, pullRequestId, fileChanges,
                    lineSpecificComments, reviewResult,
                    attributionSuffix, attributionTag, isReReview,
                    threadService, progress, cancellationToken);

            // ── Post summary comment ────────────────────────────────────
            var summaryMarkdown = await PostSummaryThreadAsync(
                project, repository, pullRequestId, reviewResult,
                isReReview, nextReviewNumber, metadata, workItems,
                skippedFiles, prSummary, reviewDepth, deepAnalysis, progress,
                sizeWarning);

            // ── Cast reviewer vote ──────────────────────────────────────
            var vote = reviewResult.RecommendedVote;
            var (voteFailed, voteSkipped) = await CastReviewVoteAsync(
                project, repository, pullRequestId, prInfo,
                vote, progress, cancellationToken);

            // ── Record history and metadata ─────────────────────────────
            totalSw.Stop();
            var estimatedCost = CalculateEstimatedCost(
                reviewResult.ModelName, reviewResult.PromptTokens,
                reviewResult.CompletionTokens);

            await RecordReviewHistoryAsync(
                project, repository, pullRequestId, prInfo, currentIteration,
                isReReview, reviewResult, postedComments, fileChanges,
                voteFailed, voteSkipped, vote, reviewDepth, estimatedCost,
                totalSw);

            // ── Build and return response ───────────────────────────────
            var completionMsg = BuildCompletionMessage(
                reviewLabel, reviewResult.Summary.Verdict, voteFailed, voteSkipped);
            ReportProgress(progress, ReviewStep.Complete, completionMsg, 100);

            return BuildReviewResponse(reviewResult, reviewDepth, summaryMarkdown,
                voteFailed, voteSkipped, vote, estimatedCost);
        }

        // ── Simulation path: build summary but don't post anything ──────
        return BuildSimulationResponse(
            pullRequestId, reviewResult, lineSpecificComments,
            fileChanges, skippedFiles, isReReview, nextReviewNumber, metadata,
            workItems, prSummary, reviewDepth, deepAnalysis, totalSw, progress,
            sizeWarning);
    }

    // ── Extracted helpers from HandleReviewAsync ────────────────────────────

    /// <summary>
    /// Pass 1: Generates a cross-file PR summary and attaches it to
    /// <see cref="PullRequestInfo.CrossFileSummary"/>.
    /// Returns <c>null</c> if summary generation fails or produces no result.
    /// </summary>
    private async Task<PrSummaryResult?> BuildPass1SummaryAsync(
        ICodeReviewService activeService,
        PullRequestInfo prInfo,
        List<FileChange> fileChanges,
        List<WorkItemInfo>? workItems,
        int pullRequestId,
        IProgress<ReviewStatusUpdate>? progress)
    {
        try
        {
            ReportProgress(progress, ReviewStep.AnalyzingCode,
                "Pass 1: Generating cross-file PR summary...", 30);

            var prSummary = await activeService.GeneratePrSummaryAsync(prInfo, fileChanges, workItems);

            if (prSummary != null)
            {
                _logger.LogInformation("[Pass 1] PR summary generated for PR #{PrId}: {Intent} ({Relationships} relationships, {Risks} risks)",
                    pullRequestId,
                    prSummary.Intent.Length > 80 ? prSummary.Intent[..80] + "…" : prSummary.Intent,
                    prSummary.CrossFileRelationships.Count,
                    prSummary.RiskAreas.Count);

                // Attach the summary to the PR info so per-file prompts can reference it
                prInfo.CrossFileSummary = prSummary;
            }
            else
            {
                _logger.LogInformation("[Pass 1] No PR summary generated for PR #{PrId} — proceeding without cross-file context", pullRequestId);
            }

            return prSummary;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Pass 1] PR summary failed for PR #{PrId} — per-file reviews will proceed without cross-file context", pullRequestId);
            return null;
        }
    }

    /// <summary>
    /// Resolves prior AI threads that were fixed, and posts new inline comments
    /// with exact + semantic deduplication. Returns counters for logging.
    /// </summary>
    private async Task<(int resolvedThreads, int postedComments, int skippedDuplicates, int repliedInThread)> PostInlineCommentsAsync(
        string project, string repository, int pullRequestId,
        List<FileChange> fileChanges,
        List<InlineComment> lineSpecificComments,
        CodeReviewResult reviewResult,
        string attributionSuffix, string? attributionTag,
        bool isReReview,
        ICodeReviewService activeService,
        IProgress<ReviewStatusUpdate>? progress,
        CancellationToken cancellationToken)
    {
        List<ExistingCommentThread> existingThreads = new();
        int resolvedThreads = 0;
        int postedComments = 0;
        int skippedDuplicates = 0;
        int repliedInThread = 0;

        if (isReReview)
        {
            existingThreads = await _devOpsService.GetExistingReviewThreadsAsync(
                project, repository, pullRequestId, attributionTag);

            // Resolve AI-generated active threads whose file is no longer in the changed set,
            // and verify via AI whether threads on modified lines were actually fixed.
            if (_devOpsSettings.ResolveFixedThreadsOnReReview)
            {
                ReportProgress(progress, ReviewStep.PostingInlineComments,
                    "Checking prior AI comments for resolved issues...", 62);

                var changedFilePaths = new HashSet<string>(
                    fileChanges.Select(f => f.FilePath), StringComparer.OrdinalIgnoreCase);

                // Collect candidates for AI verification (lines modified but need to confirm fix)
                var verificationCandidates = new List<ThreadVerificationCandidate>();

                foreach (var thread in existingThreads.Where(t => t.IsAiGenerated && t.Status == 1 /* Active */))
                {
                    if (!changedFilePaths.Contains(thread.FilePath ?? ""))
                    {
                        // File is no longer in the diff → the issue was addressed or the file was removed
                        try
                        {
                            await _devOpsService.UpdateThreadStatusAsync(
                                project, repository, pullRequestId, thread.ThreadId, "fixed");
                            resolvedThreads++;
                            var fileName = thread.FilePath?.Contains('/') == true
                                ? thread.FilePath[(thread.FilePath.LastIndexOf('/') + 1)..] : thread.FilePath;
                            _logger.LogInformation(
                                "Resolved AI thread {ThreadId} on {File} L{Start}-{End} (file no longer in diff)",
                                thread.ThreadId, fileName, thread.StartLine, thread.EndLine);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to resolve thread {ThreadId}", thread.ThreadId);
                        }
                    }
                    else
                    {
                        // File is still changed — check if the specific lines are in a modified range
                        var fc = fileChanges.FirstOrDefault(f =>
                            string.Equals(f.FilePath, thread.FilePath, StringComparison.OrdinalIgnoreCase));
                        if (fc != null && fc.ChangedLineRanges.Count > 0)
                        {
                            bool linesWereModified = fc.ChangedLineRanges.Any(r =>
                                thread.StartLine >= r.Start && thread.StartLine <= r.End);

                            if (linesWereModified)
                            {
                                // Lines were modified — build a code context window for AI verification
                                var currentCode = ExtractCodeContext(fc.ModifiedContent, thread.StartLine, thread.EndLine, contextLines: 10);
                                verificationCandidates.Add(new ThreadVerificationCandidate
                                {
                                    ThreadId = thread.ThreadId,
                                    FilePath = thread.FilePath ?? "",
                                    StartLine = thread.StartLine,
                                    EndLine = thread.EndLine,
                                    OriginalComment = thread.Content,
                                    CurrentCode = currentCode,
                                    AuthorReplies = thread.Replies,
                                });
                            }
                            // else: lines unchanged — leave thread active, nothing to verify
                        }
                    }
                }

                // AI-verify candidates whose lines were modified
                if (verificationCandidates.Count > 0)
                {
                    ReportProgress(progress, ReviewStep.PostingInlineComments,
                        $"AI-verifying {verificationCandidates.Count} prior comment(s) for resolution...", 63);

                    var verificationResults = await activeService.VerifyThreadResolutionsAsync(verificationCandidates);

                    foreach (var result in verificationResults.Where(r => r.IsFixed))
                    {
                        try
                        {
                            await _devOpsService.UpdateThreadStatusAsync(
                                project, repository, pullRequestId, result.ThreadId, "fixed");
                            resolvedThreads++;
                            var candidate = verificationCandidates.First(c => c.ThreadId == result.ThreadId);
                            var fileName = candidate.FilePath.Contains('/')
                                ? candidate.FilePath[(candidate.FilePath.LastIndexOf('/') + 1)..] : candidate.FilePath;
                            _logger.LogInformation(
                                "Resolved AI thread {ThreadId} on {File} L{Start}-{End} (AI verified: {Reason})",
                                result.ThreadId, fileName, candidate.StartLine, candidate.EndLine, result.Reasoning);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Failed to resolve verified thread {ThreadId}", result.ThreadId);
                        }
                    }

                    // Log threads that were NOT verified as fixed
                    foreach (var result in verificationResults.Where(r => !r.IsFixed))
                    {
                        var candidate = verificationCandidates.FirstOrDefault(c => c.ThreadId == result.ThreadId);
                        var fileName = candidate?.FilePath?.Contains('/') == true
                            ? candidate.FilePath[(candidate.FilePath.LastIndexOf('/') + 1)..] : candidate?.FilePath;
                        _logger.LogInformation(
                            "Kept AI thread {ThreadId} on {File} L{Start}-{End} active (AI verified NOT fixed: {Reason})",
                            result.ThreadId, fileName, candidate?.StartLine, candidate?.EndLine, result.Reasoning);
                    }
                }

                if (resolvedThreads > 0)
                {
                    _logger.LogInformation("Resolved {Count} prior AI comment threads as Fixed", resolvedThreads);
                }
            }
        }

        // ── Post inline comments (with semantic deduplication) ─────────
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(progress, ReviewStep.PostingInlineComments,
            $"Posting inline comments (deduplicating against existing threads)...", 65);

        foreach (var comment in lineSpecificComments)
        {
            var commentContent = $"**{comment.LeadIn}.** {comment.Comment}{attributionSuffix}";

            // ── Exact dedup: skip if same file + same line range + same core content ──
            var coreContent = $"**{comment.LeadIn}.** {comment.Comment}";
            if (existingThreads.Any(t =>
                    string.Equals(t.FilePath, comment.FilePath, StringComparison.OrdinalIgnoreCase)
                    && t.StartLine == comment.StartLine
                    && t.EndLine == comment.EndLine
                    && (string.Equals(t.Content, commentContent, StringComparison.Ordinal)
                        || string.Equals(t.Content, coreContent, StringComparison.Ordinal))))
            {
                skippedDuplicates++;
                continue;
            }

            // ── Semantic dedup: same file + overlapping line range + same severity ──
            // Instead of creating a new thread, reply in the existing one with updated feedback
            var semanticMatch = existingThreads.FirstOrDefault(t =>
                t.IsAiGenerated
                && string.Equals(t.FilePath, comment.FilePath, StringComparison.OrdinalIgnoreCase)
                && string.Equals(t.LeadIn, comment.LeadIn, StringComparison.OrdinalIgnoreCase)
                && LinesOverlap(t.StartLine, t.EndLine, comment.StartLine, comment.EndLine));

            if (semanticMatch != null)
            {
                // Build a contextual reply acknowledging the conversation
                var replyText = BuildThreadReply(comment, semanticMatch, attributionSuffix);
                try
                {
                    await _devOpsService.ReplyToThreadAsync(
                        project, repository, pullRequestId, semanticMatch.ThreadId, replyText);

                    // If the thread was resolved/closed, reactivate it since the issue persists
                    if (semanticMatch.Status != 1 /* Active */)
                    {
                        await _devOpsService.UpdateThreadStatusAsync(
                            project, repository, pullRequestId, semanticMatch.ThreadId, "active");
                    }

                    repliedInThread++;
                    _logger.LogInformation(
                        "Replied in existing thread {ThreadId} on {File} L{Start}-{End} (semantic match: same {LeadIn} on overlapping lines)",
                        semanticMatch.ThreadId, comment.FilePath, comment.StartLine, comment.EndLine, comment.LeadIn);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to reply in thread {ThreadId}, falling back to new thread", semanticMatch.ThreadId);
                    // Fall back to posting a new thread
                    try
                    {
                        await _devOpsService.PostInlineCommentThreadAsync(
                            project, repository, pullRequestId,
                            comment.FilePath, comment.StartLine, comment.EndLine,
                            commentContent, comment.Status);
                        postedComments++;
                    }
                    catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "Failed to post inline comment on {File}:{Line}", comment.FilePath, comment.StartLine);
                    }
                }
                continue;
            }

            try
            {
                await _devOpsService.PostInlineCommentThreadAsync(
                    project, repository, pullRequestId,
                    comment.FilePath, comment.StartLine, comment.EndLine,
                    commentContent, comment.Status);
                postedComments++;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to post inline comment on {File}:{Line}",
                    comment.FilePath, comment.StartLine);
            }
        }

        _logger.LogInformation("Posted {Count}/{Total} inline comments, skipped {Dupes} duplicates, replied in {Replied} existing threads, resolved {Resolved} prior threads",
            postedComments, reviewResult.InlineComments.Count, skippedDuplicates, repliedInThread, resolvedThreads);

        return (resolvedThreads, postedComments, skippedDuplicates, repliedInThread);
    }

    /// <summary>
    /// Builds and posts the review summary markdown as a closed comment thread.
    /// Returns the summary markdown for inclusion in the response.
    /// </summary>
    private async Task<string> PostSummaryThreadAsync(
        string project, string repository, int pullRequestId,
        CodeReviewResult reviewResult, bool isReReview,
        int nextReviewNumber, ReviewMetadata metadata,
        List<WorkItemInfo>? workItems, List<FileChange> skippedFiles,
        PrSummaryResult? prSummary, ReviewDepth reviewDepth,
        DeepAnalysisResult? deepAnalysis,
        IProgress<ReviewStatusUpdate>? progress,
        string? sizeWarning = null)
    {
        ReportProgress(progress, ReviewStep.PostingSummary,
            "Posting review summary...", 80);

        var summaryMarkdown = BuildSummaryMarkdown(pullRequestId, reviewResult, isReReview,
            nextReviewNumber, isReReview ? metadata : null, workItems, skippedFiles, prSummary,
            reviewDepth, deepAnalysis, sizeWarning);
        await _devOpsService.PostCommentThreadAsync(
            project, repository, pullRequestId, summaryMarkdown, "closed");

        return summaryMarkdown;
    }

    /// <summary>
    /// Submits the reviewer vote unless the PR is a draft or voting is disabled.
    /// Returns flags indicating whether the vote was skipped or failed.
    /// </summary>
    private async Task<(bool voteFailed, bool voteSkipped)> CastReviewVoteAsync(
        string project, string repository, int pullRequestId,
        PullRequestInfo prInfo, int vote,
        IProgress<ReviewStatusUpdate>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReportProgress(progress, ReviewStep.SubmittingVote,
            "Updating review metadata...", 85);

        bool voteFailed = false;
        bool voteSkipped = false;
        var verdictLabel = VoteToLabel(vote);

        if (prInfo.IsDraft)
        {
            voteSkipped = true;
            _logger.LogInformation("PR #{PrId} is a draft — skipping vote.", pullRequestId);
        }
        else if (!_devOpsSettings.AddReviewerVote)
        {
            voteSkipped = true;
            _logger.LogInformation("AddReviewerVote is disabled — skipping vote for PR #{PrId}.", pullRequestId);
        }
        else
        {
            ReportProgress(progress, ReviewStep.SubmittingVote,
                $"Submitting reviewer vote: {verdictLabel}...", 90);
            try
            {
                await _devOpsService.AddReviewerAsync(project, repository, pullRequestId, vote);
                _logger.LogInformation("Submitted vote {Vote} ({Label}) for PR #{PrId}",
                    vote, verdictLabel, pullRequestId);
            }
            catch (HttpRequestException ex)
            {
                voteFailed = true;
                _logger.LogWarning(ex, "Failed to submit vote for PR #{PrId}.", pullRequestId);
            }
        }

        return (voteFailed, voteSkipped);
    }

    /// <summary>
    /// Records the review in metadata/tags and the rate limiter.
    /// </summary>
    private async Task RecordReviewHistoryAsync(
        string project, string repository, int pullRequestId,
        PullRequestInfo prInfo, int currentIteration,
        bool isReReview, CodeReviewResult reviewResult,
        int postedComments, List<FileChange> fileChanges,
        bool voteFailed, bool voteSkipped, int vote,
        ReviewDepth reviewDepth, decimal? estimatedCost,
        Stopwatch totalSw)
    {
        var historyEntry = new ReviewHistoryEntry
        {
            Action = isReReview ? "Re-Review" : "Full Review",
            Verdict = reviewResult.Summary.Verdict,
            SourceCommit = prInfo.LastMergeSourceCommit,
            Iteration = currentIteration,
            IsDraft = prInfo.IsDraft,
            InlineComments = postedComments,
            FilesChanged = fileChanges.Count,
            Vote = (voteFailed || voteSkipped) ? null : vote,
            // AI Metrics
            ModelName = reviewResult.ModelName,
            PromptTokens = reviewResult.PromptTokens,
            CompletionTokens = reviewResult.CompletionTokens,
            TotalTokens = reviewResult.TotalTokens,
            AiDurationMs = reviewResult.AiDurationMs,
            TotalDurationMs = totalSw.ElapsedMilliseconds,
            EstimatedCost = estimatedCost,
            ReviewDepth = reviewDepth.ToString(),
            PassModels = reviewResult.PassModels,
        };
        await UpdateMetadataAndTag(project, repository, pullRequestId, prInfo, currentIteration,
            !voteFailed && !voteSkipped, historyEntry);

        // Record in rate limiter
        _rateLimiter.Record(_devOpsSettings.Organization, project, repository, pullRequestId);
    }

    /// <summary>
    /// Constructs a human-readable completion message for progress reporting.
    /// </summary>
    private static string BuildCompletionMessage(
        string reviewLabel, string verdict, bool voteFailed, bool voteSkipped)
    {
        if (voteFailed) return $"{reviewLabel} complete: {verdict} (vote failed)";
        if (voteSkipped) return $"{reviewLabel} complete: {verdict} (vote skipped)";
        return $"{reviewLabel} complete: {verdict}";
    }

    /// <summary>
    /// Constructs the final <see cref="ReviewResponse"/> for a completed (non-simulation) review.
    /// </summary>
    private static ReviewResponse BuildReviewResponse(
        CodeReviewResult reviewResult, ReviewDepth reviewDepth,
        string summaryMarkdown, bool voteFailed, bool voteSkipped,
        int vote, decimal? estimatedCost)
    {
        int errors = reviewResult.InlineComments.Count(c => c.LeadIn is "Bug" or "Security");
        int warnings = reviewResult.InlineComments.Count(c => c.LeadIn is "Concern" or "Performance");
        int info = reviewResult.InlineComments.Count(c => c.LeadIn is "Suggestion" or "LGTM" or "Good catch" or "Important");

        return new ReviewResponse
        {
            Status = "Reviewed",
            ReviewDepth = reviewDepth.ToString(),
            Recommendation = MapVerdictToRecommendation(reviewResult.Summary.Verdict),
            Summary = summaryMarkdown,
            IssueCount = reviewResult.InlineComments.Count,
            ErrorCount = errors,
            WarningCount = warnings,
            InfoCount = info,
            Vote = (voteFailed || voteSkipped) ? null : vote,
            ErrorMessage = voteFailed
                ? "Review posted but vote submission failed. Check server logs."
                : null,
            PromptTokens = reviewResult.PromptTokens,
            CompletionTokens = reviewResult.CompletionTokens,
            TotalTokens = reviewResult.TotalTokens,
            AiDurationMs = reviewResult.AiDurationMs,
            EstimatedCost = estimatedCost,
        };
    }

    /// <summary>
    /// Builds the simulation response — same as a real review but nothing is posted to Azure DevOps.
    /// </summary>
    private ReviewResponse BuildSimulationResponse(
        int pullRequestId, CodeReviewResult reviewResult,
        List<InlineComment> lineSpecificComments,
        List<FileChange> fileChanges, List<FileChange> skippedFiles,
        bool isReReview, int nextReviewNumber, ReviewMetadata metadata,
        List<WorkItemInfo>? workItems, PrSummaryResult? prSummary,
        ReviewDepth reviewDepth, DeepAnalysisResult? deepAnalysis,
        Stopwatch totalSw, IProgress<ReviewStatusUpdate>? progress,
        string? sizeWarning = null)
    {
        var simSummaryMarkdown = BuildSummaryMarkdown(pullRequestId, reviewResult, isReReview,
            nextReviewNumber, isReReview ? metadata : null, workItems, skippedFiles, prSummary,
            reviewDepth, deepAnalysis, sizeWarning);

        totalSw.Stop();

        var simVote = reviewResult.RecommendedVote;
        var simEstimatedCost = CalculateEstimatedCost(
            reviewResult.ModelName, reviewResult.PromptTokens, reviewResult.CompletionTokens);

        var completionMsg = $"Simulation complete: {reviewResult.Summary.Verdict} (nothing posted)";
        ReportProgress(progress, ReviewStep.Complete, completionMsg, 100);

        int simErrors = lineSpecificComments.Count(c => c.LeadIn is "Bug" or "Security");
        int simWarnings = lineSpecificComments.Count(c => c.LeadIn is "Concern" or "Performance");
        int simInfo = lineSpecificComments.Count(c => c.LeadIn is "Suggestion" or "LGTM" or "Good catch" or "Important");

        var simResponse = new ReviewResponse
        {
            Status = "Simulated",
            ReviewDepth = reviewDepth.ToString(),
            Recommendation = MapVerdictToRecommendation(reviewResult.Summary.Verdict),
            Summary = simSummaryMarkdown,
            IssueCount = lineSpecificComments.Count,
            ErrorCount = simErrors,
            WarningCount = simWarnings,
            InfoCount = simInfo,
            Vote = simVote,
            Verdict = reviewResult.Summary.Verdict,
            VerdictJustification = reviewResult.Summary.VerdictJustification,
            PromptTokens = reviewResult.PromptTokens,
            CompletionTokens = reviewResult.CompletionTokens,
            TotalTokens = reviewResult.TotalTokens,
            AiDurationMs = reviewResult.AiDurationMs,
            EstimatedCost = simEstimatedCost,
            InlineComments = lineSpecificComments.Select(c => new InlineCommentDto
            {
                FilePath = c.FilePath,
                StartLine = c.StartLine,
                EndLine = c.EndLine,
                Severity = c.LeadIn,
                Comment = c.Comment,
                Status = c.Status,
            }).ToList(),
            FileReviews = reviewResult.FileReviews.Select(f => new FileReviewDto
            {
                FilePath = f.FilePath,
                Verdict = f.Verdict,
                ReviewText = f.ReviewText,
            }).ToList(),
            SkippedFiles = skippedFiles.Count > 0
                ? skippedFiles.Select(f => new SkippedFileDto
                {
                    FilePath = f.FilePath,
                    SkipReason = f.SkipReason!,
                }).ToList()
                : null,
        };

        _logger.LogInformation("SIMULATION complete for PR #{PrId}: {Verdict} — {IssueCount} issues, {FileCount} files reviewed, {SkipCount} files skipped (nothing posted)",
            pullRequestId, reviewResult.Summary.Verdict, lineSpecificComments.Count, fileChanges.Count, skippedFiles.Count);

        return simResponse;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs Pass 2 (per-file parallel review or Vector Store review) and optionally Pass 3 (Deep holistic analysis).
    /// Extracted from HandleReviewAsync to keep the depth-branching logic clean.
    /// </summary>
    private async Task<(CodeReviewResult reviewResult, DeepAnalysisResult? deepAnalysis)> HandleStandardOrDeepPassesAsync(
        PullRequestInfo prInfo, int pullRequestId,
        List<FileChange> fileChanges, PrSummaryResult? prSummary,
        List<WorkItemInfo>? workItems,
        ReviewDepth reviewDepth, ReviewStrategy reviewStrategy, string reviewLabel,
        ICodeReviewService pass2Service,
        ICodeReviewService? pass3Service,
        IProgress<ReviewStatusUpdate>? progress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // ── Resolve effective strategy (Auto mode) ──────────────────────
        var effectiveStrategy = reviewStrategy;
        if (reviewStrategy == ReviewStrategy.Auto)
        {
            effectiveStrategy = fileChanges.Count <= _assistantsSettings.AutoThreshold
                ? ReviewStrategy.FileByFile
                : ReviewStrategy.Vector;

            _logger.LogInformation(
                "[Auto] {FileCount} files vs threshold {Threshold} → using {Strategy} strategy",
                fileChanges.Count, _assistantsSettings.AutoThreshold, effectiveStrategy);
        }

        // ── RPM capacity warning ─────────────────────────────────────
        // Estimate total API calls: 1 (Pass 1 summary, already done) + N files (Pass 2)
        // + 1 (Pass 3 deep analysis, if Deep mode). Warn if exceeding 80% of model RPM.
        if (effectiveStrategy == ReviewStrategy.FileByFile)
        {
            var adapter = _modelAdapterResolver.Resolve(pass2Service.ModelName);
            if (adapter.RequestsPerMinute is > 0 and var rpm)
            {
                int estimatedCalls = 1 + fileChanges.Count + (reviewDepth == ReviewDepth.Deep ? 1 : 0);
                double capacityPct = (double)estimatedCalls / rpm * 100;

                if (capacityPct >= 80)
                {
                    _logger.LogWarning(
                        "[RPM] PR #{PrId} will make ~{EstimatedCalls} API calls against model '{Model}' ({Rpm} RPM) — " +
                        "{CapacityPct:F0}% of per-minute capacity. Review may be throttled to avoid rate-limit errors.",
                        pullRequestId, estimatedCalls, pass2Service.ModelName, rpm, capacityPct);
                }
                else
                {
                    _logger.LogDebug(
                        "[RPM] PR #{PrId}: ~{EstimatedCalls} calls vs {Rpm} RPM ({CapacityPct:F0}% capacity) — within limits",
                        pullRequestId, estimatedCalls, rpm, capacityPct);
                }
            }
        }

        // ── Pass 2: Branch on strategy ──────────────────────────────────
        CodeReviewResult reviewResult;

        if (effectiveStrategy == ReviewStrategy.Vector)
        {
            reviewResult = await HandleVectorPassAsync(
                prInfo, pullRequestId, fileChanges, prSummary, workItems,
                reviewStrategy, reviewLabel, pass2Service, progress, cancellationToken);
        }
        else
        {
            reviewResult = await HandleFileByFilePassAsync(
                prInfo, pullRequestId, fileChanges, prSummary, workItems,
                reviewLabel, pass2Service, progress, cancellationToken);
        }

        // ── Pass 3: Deep holistic re-evaluation (Deep mode only) ────────
        DeepAnalysisResult? deepAnalysis = null;

        if (reviewDepth == ReviewDepth.Deep && pass3Service != null)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                ReportProgress(progress, ReviewStep.AnalyzingCode,
                    "Pass 3: Deep holistic re-evaluation...", 62);

                deepAnalysis = await pass3Service.GenerateDeepAnalysisAsync(
                    prInfo, prSummary, reviewResult, fileChanges);

                if (deepAnalysis != null)
                {
                    _logger.LogInformation(
                        "[Pass 3] Deep analysis complete for PR #{PrId}: {Risk} risk, {IssueCount} cross-file issues, verdict consistent={Consistent}",
                        pullRequestId,
                        deepAnalysis.OverallRiskLevel,
                        deepAnalysis.CrossFileIssues.Count,
                        deepAnalysis.VerdictConsistency.IsConsistent);

                    // Apply verdict override if deep analysis disagrees
                    if (!deepAnalysis.VerdictConsistency.IsConsistent
                        && !string.IsNullOrWhiteSpace(deepAnalysis.VerdictConsistency.RecommendedVerdict))
                    {
                        _logger.LogInformation(
                            "[Pass 3] Verdict override: {OldVerdict} → {NewVerdict} (reason: {Reason})",
                            reviewResult.Summary.Verdict,
                            deepAnalysis.VerdictConsistency.RecommendedVerdict,
                            deepAnalysis.VerdictConsistency.Explanation);

                        reviewResult.Summary.Verdict = deepAnalysis.VerdictConsistency.RecommendedVerdict;
                        reviewResult.Summary.VerdictJustification = deepAnalysis.VerdictConsistency.Explanation;

                        if (deepAnalysis.VerdictConsistency.RecommendedVote.HasValue)
                            reviewResult.RecommendedVote = deepAnalysis.VerdictConsistency.RecommendedVote.Value;
                    }

                    // Add Pass 3 token usage to the total
                    reviewResult.PromptTokens = (reviewResult.PromptTokens ?? 0) + (deepAnalysis.PromptTokens ?? 0);
                    reviewResult.CompletionTokens = (reviewResult.CompletionTokens ?? 0) + (deepAnalysis.CompletionTokens ?? 0);
                    reviewResult.TotalTokens = (reviewResult.TotalTokens ?? 0) + (deepAnalysis.TotalTokens ?? 0);
                    reviewResult.AiDurationMs = (reviewResult.AiDurationMs ?? 0) + (deepAnalysis.AiDurationMs ?? 0);
                }
                else
                {
                    _logger.LogInformation("[Pass 3] No deep analysis generated for PR #{PrId} — proceeding with Standard results", pullRequestId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Pass 3] Deep analysis failed for PR #{PrId} — proceeding with Standard results", pullRequestId);
            }
        }

        return (reviewResult, deepAnalysis);
    }

    /// <summary>
    /// Pass 2 — FileByFile: per-file parallel Chat Completions review (original behavior).
    /// </summary>
    private async Task<CodeReviewResult> HandleFileByFilePassAsync(
        PullRequestInfo prInfo, int pullRequestId,
        List<FileChange> fileChanges, PrSummaryResult? prSummary,
        List<WorkItemInfo>? workItems,
        string reviewLabel,
        ICodeReviewService activeService,
        IProgress<ReviewStatusUpdate>? progress,
        CancellationToken cancellationToken)
    {
        var maxParallel = Math.Max(1, _aiProviderSettings.MaxParallelReviews);

        // ── RPM-aware throttling ────────────────────────────────────────
        // Resolve the model adapter to get the deployment's RPM limit.
        // Each task claims a time slot so calls are spaced at least (60s / RPM) apart.
        var adapter = _modelAdapterResolver.Resolve(activeService.ModelName);
        int effectiveRpm = adapter.RequestsPerMinute ?? 0;
        int rpmDelayMs = effectiveRpm > 0
            ? (int)Math.Ceiling(60_000.0 / effectiveRpm)
            : 0;

        // Clamp concurrency when RPM is very low (avoid wasting semaphore slots waiting on delay)
        if (rpmDelayMs > 0)
        {
            int rpmDerivedParallel = Math.Max(1, effectiveRpm / 10); // heuristic: allow ~6s of pipelined calls
            if (rpmDerivedParallel < maxParallel)
            {
                _logger.LogInformation(
                    "[RPM] Reducing concurrency from {Configured} → {Effective} for model '{Model}' ({Rpm} RPM, {DelayMs}ms between calls)",
                    maxParallel, rpmDerivedParallel, activeService.ModelName, effectiveRpm, rpmDelayMs);
                maxParallel = rpmDerivedParallel;
            }
            else
            {
                _logger.LogInformation(
                    "[RPM] Throttling model '{Model}' at {DelayMs}ms between calls ({Rpm} RPM, {MaxParallel} concurrent)",
                    activeService.ModelName, rpmDelayMs, effectiveRpm, maxParallel);
            }
        }

        long nextCallTick = 0; // shared ticket for RPM spacing

        ReportProgress(progress, ReviewStep.AnalyzingCode,
            $"Pass 2 (FileByFile): Analyzing {fileChanges.Count} files (max {maxParallel} concurrent, {reviewLabel.ToLower()})...", 35);

        _logger.LogInformation("{Label}: Reviewing {FileCount} files in parallel (max {MaxParallel} concurrent)",
            reviewLabel, fileChanges.Count, maxParallel);

        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        int completedFiles = 0;
        var perFileResults = new CodeReviewResult[fileChanges.Count];

        try
        {
            var tasks = fileChanges.Select(async (file, index) =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    // RPM rate limiting: claim a time slot and wait if needed
                    if (rpmDelayMs > 0)
                    {
                        long mySlot;
                        while (true)
                        {
                            long current = Interlocked.Read(ref nextCallTick);
                            long now = Environment.TickCount64;
                            long proposed = Math.Max(current, now) + rpmDelayMs;
                            if (Interlocked.CompareExchange(ref nextCallTick, proposed, current) == current)
                            {
                                mySlot = proposed - rpmDelayMs;
                                break;
                            }
                        }
                        long delay = mySlot - Environment.TickCount64;
                        if (delay > 0)
                            await Task.Delay((int)delay, cancellationToken);
                    }

                    // Wait for any global rate-limit cooldown before calling the AI
                    await _globalRateLimitSignal.WaitIfCoolingDownAsync(cancellationToken);

                    var result = await activeService.ReviewFileAsync(prInfo, file, fileChanges.Count, workItems);
                    perFileResults[index] = result;

                    var done = Interlocked.Increment(ref completedFiles);
                    var pct = 35 + (int)(25.0 * done / fileChanges.Count);
                    ReportProgress(progress, ReviewStep.AnalyzingCode,
                        $"Analyzed {done}/{fileChanges.Count} files...", pct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "AI review failed for {FilePath}", file.FilePath);
                    perFileResults[index] = new CodeReviewResult
                    {
                        Summary = new ReviewSummary
                        {
                            FilesChanged = 1,
                            Verdict = "APPROVED",
                            VerdictJustification = $"AI review failed for {file.FilePath}: {ex.Message}",
                        },
                        FileReviews = new List<FileReview>
                        {
                            new FileReview { FilePath = file.FilePath, Verdict = "CONCERN", ReviewText = $"AI review failed: {ex.Message}" }
                        },
                    };
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }
        finally
        {
            semaphore.Dispose();
        }

        // Merge per-file results
        var reviewResult = MergeBatchResults(perFileResults.ToList(), fileChanges.Count);

        // Add Pass 1 token usage
        if (prSummary != null)
        {
            reviewResult.PromptTokens = (reviewResult.PromptTokens ?? 0) + (prSummary.PromptTokens ?? 0);
            reviewResult.CompletionTokens = (reviewResult.CompletionTokens ?? 0) + (prSummary.CompletionTokens ?? 0);
            reviewResult.TotalTokens = (reviewResult.TotalTokens ?? 0) + (prSummary.TotalTokens ?? 0);
            reviewResult.AiDurationMs = (reviewResult.AiDurationMs ?? 0) + (prSummary.AiDurationMs ?? 0);

            if (!string.IsNullOrWhiteSpace(prSummary.Intent)
                && string.IsNullOrWhiteSpace(reviewResult.Summary?.Description))
            {
                reviewResult.Summary ??= new ReviewSummary();
                reviewResult.Summary.Description = prSummary.Intent;
            }
        }

        _logger.LogInformation("{Label} complete ({FileCount} files, FileByFile): {Verdict} with {InlineCount} inline comments",
            reviewLabel, fileChanges.Count,
            reviewResult.Summary.Verdict, reviewResult.InlineComments.Count);

        return reviewResult;
    }

    /// <summary>
    /// Pass 2 — Vector: upload all files to a Vector Store and review in a single Assistants API run.
    /// Falls back to FileByFile if Auto mode was used and Vector fails.
    /// </summary>
    private async Task<CodeReviewResult> HandleVectorPassAsync(
        PullRequestInfo prInfo, int pullRequestId,
        List<FileChange> fileChanges, PrSummaryResult? prSummary,
        List<WorkItemInfo>? workItems,
        ReviewStrategy originalStrategy,
        string reviewLabel,
        ICodeReviewService activeService,
        IProgress<ReviewStatusUpdate>? progress,
        CancellationToken cancellationToken)
    {
        ReportProgress(progress, ReviewStep.AnalyzingCode,
            $"Pass 2 (Vector): Uploading {fileChanges.Count} files to Vector Store for cross-file review...", 35);

        try
        {
            var reviewResult = await _vectorService.ReviewAllFilesAsync(
                prInfo, fileChanges, prSummary, workItems, cancellationToken);

            // Add Pass 1 token usage
            if (prSummary != null)
            {
                reviewResult.PromptTokens = (reviewResult.PromptTokens ?? 0) + (prSummary.PromptTokens ?? 0);
                reviewResult.CompletionTokens = (reviewResult.CompletionTokens ?? 0) + (prSummary.CompletionTokens ?? 0);
                reviewResult.TotalTokens = (reviewResult.TotalTokens ?? 0) + (prSummary.TotalTokens ?? 0);
                reviewResult.AiDurationMs = (reviewResult.AiDurationMs ?? 0) + (prSummary.AiDurationMs ?? 0);

                if (!string.IsNullOrWhiteSpace(prSummary.Intent)
                    && string.IsNullOrWhiteSpace(reviewResult.Summary?.Description))
                {
                    reviewResult.Summary ??= new ReviewSummary();
                    reviewResult.Summary.Description = prSummary.Intent;
                }
            }

            _logger.LogInformation("{Label} complete ({FileCount} files, Vector): {Verdict} with {InlineCount} inline comments",
                reviewLabel, fileChanges.Count,
                reviewResult.Summary.Verdict, reviewResult.InlineComments.Count);

            return reviewResult;
        }
        catch (Exception ex) when (originalStrategy == ReviewStrategy.Auto)
        {
            // Auto mode: fall back to FileByFile on Vector failure
            _logger.LogWarning(ex,
                "[Vector] Vector Store review failed for PR #{PrId} — falling back to FileByFile (Auto mode)",
                pullRequestId);

            ReportProgress(progress, ReviewStep.AnalyzingCode,
                "Vector Store review failed — falling back to FileByFile...", 36);

            return await HandleFileByFilePassAsync(
                prInfo, pullRequestId, fileChanges, prSummary, workItems,
                reviewLabel, activeService, progress, cancellationToken);
        }
        // If Vector mode was explicitly requested and fails, let the exception propagate
    }

    /// <summary>
    /// Build a CodeReviewResult from Pass 1 (PR summary) only, for Quick mode.
    /// No per-file reviews or inline comments — just a PR-level verdict.
    /// Computes change-type counts from fileChanges and includes skipped file info.
    /// </summary>
    internal static CodeReviewResult BuildQuickModeResult(
        PrSummaryResult? prSummary, List<FileChange> fileChanges, List<FileChange> skippedFiles)
    {
        // Compute change-type counts from fileChanges (same as MergeBatchResults)
        int edits = 0, adds = 0, deletes = 0;
        foreach (var fc in fileChanges)
        {
            switch (fc.ChangeType)
            {
                case "edit": edits++; break;
                case "add": adds++; break;
                case "delete": deletes++; break;
                default: edits++; break; // treat unknown as edit
            }
        }

        var description = prSummary?.Intent ?? "Quick mode review — PR-level summary only.";
        if (skippedFiles.Count > 0)
            description += $" ({skippedFiles.Count} non-reviewable file(s) excluded.)";

        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = fileChanges.Count,
                EditsCount = edits,
                AddsCount = adds,
                DeletesCount = deletes,
                Description = description,
                Verdict = DeriveQuickVerdict(prSummary),
                VerdictJustification = DeriveQuickJustification(prSummary),
            },
            // No inline comments in Quick mode
            InlineComments = new List<InlineComment>(),
            // No detailed file reviews in Quick mode
            FileReviews = fileChanges.Select(f => new FileReview
            {
                FilePath = f.FilePath,
                Verdict = "SKIPPED",
                ReviewText = "Per-file review skipped (Quick mode).",
            }).ToList(),
            RecommendedVote = DeriveQuickVote(prSummary),
        };

        return result;
    }

    internal static string DeriveQuickVerdict(PrSummaryResult? prSummary)
    {
        if (prSummary == null)
            return "APPROVED WITH SUGGESTIONS"; // limited analysis — can't confirm approval

        // Use risk areas to determine verdict
        var highRiskCount = prSummary.RiskAreas.Count;
        return highRiskCount switch
        {
            0 => "APPROVED",
            1 or 2 => "APPROVED WITH SUGGESTIONS",
            _ => "NEEDS WORK",
        };
    }

    internal static string DeriveQuickJustification(PrSummaryResult? prSummary)
    {
        if (prSummary == null)
            return "Quick review — limited analysis without per-file review. Pass 1 summary unavailable.";

        if (prSummary.RiskAreas.Count == 0)
            return $"Quick review — no significant risks identified. {prSummary.Intent}";

        var risks = string.Join("; ", prSummary.RiskAreas.Select(r => $"{r.Area}: {r.Reason}"));
        return $"Quick review — {prSummary.RiskAreas.Count} risk area(s) identified: {risks}";
    }

    internal static int DeriveQuickVote(PrSummaryResult? prSummary)
    {
        if (prSummary == null)
            return 5; // approve with suggestions — limited analysis, consistent with verdict

        return prSummary.RiskAreas.Count switch
        {
            0 => 10,  // approve
            1 or 2 => 5,  // approve with suggestions
            _ => -5,  // wait for author
        };
    }

    private async Task UpdateMetadataAndTag(
        string project, string repository, int pullRequestId,
        PullRequestInfo prInfo, int currentIteration, bool voteSubmitted,
        ReviewHistoryEntry? historyEntry = null)
    {
        // Derive review number from history (resilient to metadata resets)
        var currentMeta = await _devOpsService.GetReviewMetadataAsync(project, repository, pullRequestId);
        var existingHistory = await _devOpsService.GetReviewHistoryAsync(project, repository, pullRequestId);
        var newCount = existingHistory.Count + 1;

        var newMetadata = new ReviewMetadata
        {
            LastReviewedSourceCommit = prInfo.LastMergeSourceCommit,
            LastReviewedTargetCommit = prInfo.LastMergeTargetCommit,
            LastReviewedIteration = currentIteration,
            WasDraft = prInfo.IsDraft,
            ReviewedAtUtc = DateTime.UtcNow,
            VoteSubmitted = voteSubmitted,
            ReviewCount = newCount,
        };
        await _devOpsService.SetReviewMetadataAsync(project, repository, pullRequestId, newMetadata);

        // Ensure tag is present (decorative — for PR list filtering; not used for decisions)
        if (!await _devOpsService.HasReviewTagAsync(project, repository, pullRequestId))
        {
            await _devOpsService.AddReviewTagAsync(project, repository, pullRequestId);
        }

        // Append review history to PR description
        if (historyEntry != null)
        {
            historyEntry.ReviewNumber = newCount;
            historyEntry.ReviewedAtUtc = newMetadata.ReviewedAtUtc;

            // Store in PR properties (canonical source of truth)
            await _devOpsService.AppendReviewHistoryAsync(project, repository, pullRequestId, historyEntry);

            // Also append to PR description (visual convenience)
            await AppendReviewHistoryToDescriptionAsync(project, repository, pullRequestId, prInfo, historyEntry);
        }
    }

    private const string HistoryMarkerStart = "<!-- AI-REVIEW-HISTORY-START -->";
    private const string HistoryMarkerEnd = "<!-- AI-REVIEW-HISTORY-END -->";

    private async Task AppendReviewHistoryToDescriptionAsync(
        string project, string repository, int pullRequestId,
        PullRequestInfo prInfo, ReviewHistoryEntry entry)
    {
        try
        {
            // Re-fetch the PR to get the latest description (may have been updated by a prior review)
            var currentPr = await _devOpsService.GetPullRequestAsync(project, repository, pullRequestId);
            var description = currentPr.Description ?? string.Empty;

            // Extract existing history section if present
            var startIdx = description.IndexOf(HistoryMarkerStart, StringComparison.Ordinal);
            var endIdx = description.IndexOf(HistoryMarkerEnd, StringComparison.Ordinal);

            string existingRows = string.Empty;
            string originalDescription;

            if (startIdx >= 0 && endIdx >= 0)
            {
                // Pull out existing table rows (between markers)
                var historyContent = description.Substring(
                    startIdx + HistoryMarkerStart.Length,
                    endIdx - startIdx - HistoryMarkerStart.Length);

                // Extract just the data rows (lines starting with |, skip header + separator)
                var lines = historyContent.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                var dataRows = lines.Where(l => l.TrimStart().StartsWith("|") && !l.Contains("---") && !l.Contains("Review #")).ToList();
                existingRows = string.Join("\n", dataRows);

                originalDescription = description[..startIdx].TrimEnd();
            }
            else
            {
                originalDescription = description.TrimEnd();
            }

            // Build the new history row
            var shortCommit = (entry.SourceCommit?.Length > 7 ? entry.SourceCommit[..7] : entry.SourceCommit) ?? "—";
            var draftBadge = entry.IsDraft ? " 📝 Draft" : "";
            // Count existing rows in the description table for a monotonic display number
            int existingRowCount = string.IsNullOrWhiteSpace(existingRows)
                ? 0
                : existingRows.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
            var displayNumber = existingRowCount + 1;

            var dateStr = entry.ReviewedAtUtc.ToString("yyyy-MM-dd HH:mm:ss UTC");

            var newRow = $"| {displayNumber} | {dateStr} | {entry.Action} | {entry.Verdict}{draftBadge} | `{shortCommit}` | Iter {entry.Iteration} | {entry.FilesChanged} files, {entry.InlineComments} comments |";

            // Build complete history section
            var sb = new StringBuilder();
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(HistoryMarkerStart);
            sb.AppendLine();
            sb.AppendLine("---");
            sb.AppendLine();
            sb.AppendLine("### 🤖 AI Code Review History");
            sb.AppendLine();
            sb.AppendLine("| Review # | Date (UTC) | Action | Verdict | Commit | Iteration | Scope |");
            sb.AppendLine("|----------|-----------|--------|---------|--------|-----------|-------|");
            if (!string.IsNullOrWhiteSpace(existingRows))
            {
                sb.AppendLine(existingRows);
            }
            sb.AppendLine(newRow);
            sb.AppendLine();
            sb.AppendLine(HistoryMarkerEnd);

            var newDescription = originalDescription + sb.ToString();

            await _devOpsService.UpdatePrDescriptionAsync(project, repository, pullRequestId, newDescription);
            _logger.LogInformation("Appended review history entry #{Num} to PR #{PrId} description", entry.ReviewNumber, pullRequestId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append review history to PR #{PrId} description — non-fatal", pullRequestId);
        }
    }

    /// <summary>
    /// Extract a window of code around the specified line range from file content.
    /// Provides context for AI to verify whether a prior review comment was addressed.
    /// </summary>
    private static string ExtractCodeContext(string? fileContent, int startLine, int endLine, int contextLines = 10)
    {
        if (string.IsNullOrEmpty(fileContent))
            return "(file content not available)";

        var lines = fileContent.Split('\n');
        var from = Math.Max(0, startLine - 1 - contextLines);  // 0-indexed
        var to = Math.Min(lines.Length - 1, endLine - 1 + contextLines); // 0-indexed

        var sb = new StringBuilder();
        for (int i = from; i <= to; i++)
        {
            sb.Append((i + 1).ToString().PadLeft(4));
            sb.Append(" | ");
            sb.AppendLine(lines[i].TrimEnd('\r'));
        }
        return sb.ToString().TrimEnd();
    }

    // ── Known lock / generated file names ────────────────────────────────
    private static readonly HashSet<string> LockFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "package-lock.json", "yarn.lock", "pnpm-lock.yaml",
        "composer.lock", "Gemfile.lock", "poetry.lock",
        "Pipfile.lock", "packages.lock.json", "shrinkwrap.yaml",
    };

    private static readonly HashSet<string> GeneratedFileSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        ".designer.cs", ".g.cs", ".generated.cs", ".g.i.cs",
        ".min.js", ".min.css",
    };

    /// <summary>
    /// SHA pattern: 40-hex-char git object hash, optionally followed by whitespace.
    /// Matches submodule pointers, .gitmodules entries, etc.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ShaPattern =
        new(@"^\s*[0-9a-fA-F]{40}\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Classifies a file as non-reviewable if it matches known patterns (submodule ref, lock file, etc.).
    /// Returns a human-readable reason string, or null if the file should be reviewed normally.
    /// </summary>
    internal static string? ClassifyNonReviewableFile(FileChange file)
    {
        var fileName = Path.GetFileName(file.FilePath);

        // 1. Lock files (auto-generated, huge, no human-written code)
        if (LockFileNames.Contains(fileName))
            return "lock file";

        // 2. Generated code files
        if (GeneratedFileSuffixes.Any(suffix => file.FilePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)))
            return "generated file";

        // 3. Known non-code marker files
        if (fileName.Equals(".gitkeep", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".gitattributes", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals(".editorconfig", StringComparison.OrdinalIgnoreCase))
            return "config marker";

        // 4. Content-based checks (only for files we have content for)
        var content = file.ModifiedContent ?? file.OriginalContent;
        if (content != null)
        {
            var trimmed = content.Trim();

            // Empty / whitespace-only files
            if (trimmed.Length == 0)
                return "empty file";

            // Single-line files that are just a SHA hash (git submodule pointers)
            if (!trimmed.Contains('\n') && ShaPattern.IsMatch(trimmed))
                return "submodule reference";

            // Very short single-line content that looks like a hash/guid/token (not real code)
            if (!trimmed.Contains('\n') && trimmed.Length <= 128 && !trimmed.Contains(' ')
                && System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[0-9a-fA-F\-]{20,}$"))
                return "hash/identifier reference";
        }

        return null; // Reviewable
    }

    /// <summary>
    /// Check if two line ranges overlap or are adjacent (within 5 lines).
    /// Used for semantic deduplication — a re-phrased comment on nearby lines is likely the same concern.
    /// </summary>
    internal static bool LinesOverlap(int start1, int end1, int start2, int end2, int tolerance = 5)
    {
        return start1 <= end2 + tolerance && start2 <= end1 + tolerance;
    }

    /// <summary>
    /// Build a contextual reply for an existing thread, acknowledging the conversation history.
    /// </summary>
    private static string BuildThreadReply(InlineComment newComment, ExistingCommentThread existingThread, string attributionSuffix)
    {
        var sb = new StringBuilder();

        // Acknowledge human replies if present
        if (existingThread.Replies.Count > 0)
        {
            sb.AppendLine("**Re-review update** — I've reviewed the changes and the conversation on this thread.");
            sb.AppendLine();
            sb.AppendLine($"**{newComment.LeadIn}.** {newComment.Comment}");
        }
        else
        {
            sb.AppendLine("**Re-review update** — This issue was flagged again during re-review.");
            sb.AppendLine();
            sb.AppendLine($"**{newComment.LeadIn}.** {newComment.Comment}");
        }

        sb.Append(attributionSuffix);
        return sb.ToString();
    }

    /// <summary>
    /// Counts the total number of changed lines across a set of files.
    /// Uses <see cref="FileChange.ChangedLineRanges"/> for edits, and line-counts
    /// of <see cref="FileChange.ModifiedContent"/> for adds (or <see cref="FileChange.OriginalContent"/> for deletes).
    /// </summary>
    internal static int CountChangedLines(IEnumerable<FileChange> files)
    {
        int total = 0;
        foreach (var f in files)
        {
            if (f.ChangedLineRanges.Count > 0)
            {
                total += f.ChangedLineRanges.Sum(r => r.End - r.Start + 1);
            }
            else
            {
                // For adds/deletes with no explicit ranges, count content lines
                var content = f.ModifiedContent ?? f.OriginalContent;
                if (content != null)
                    total += content.Split('\n').Length;
            }
        }
        return total;
    }

    /// <summary>
    /// Sorts files by review priority: new files first, then heavily-modified edits,
    /// then minor edits, then deletes, then renames/moves. Within each category,
    /// files are ordered by descending number of changed lines.
    /// </summary>
    internal static List<FileChange> PrioritizeFiles(List<FileChange> files)
    {
        return files
            .OrderBy(f => ChangeTypePriority(f.ChangeType))
            .ThenByDescending(f => f.ChangedLineRanges.Sum(r => r.End - r.Start + 1))
            .ToList();

        static int ChangeTypePriority(string changeType) => changeType.ToLowerInvariant() switch
        {
            "add" => 0,
            "edit" => 1,
            "delete" => 2,
            "rename" => 3,
            _ => 4,
        };
    }

    /// <summary>
    /// Evaluates size guardrails against the current set of reviewable files.
    /// Returns a human-readable warning string if thresholds are exceeded, or <c>null</c> if within limits.
    /// </summary>
    internal static string? EvaluateSizeGuardrails(List<FileChange> reviewableFiles, SizeGuardrailsSettings settings)
    {
        var warnings = new List<string>();

        if (settings.WarnFileCount > 0 && reviewableFiles.Count > settings.WarnFileCount)
            warnings.Add($"{reviewableFiles.Count} reviewable files (threshold: {settings.WarnFileCount})");

        var changedLines = CountChangedLines(reviewableFiles);
        if (settings.WarnChangedLines > 0 && changedLines > settings.WarnChangedLines)
            warnings.Add($"{changedLines:N0} changed lines (threshold: {settings.WarnChangedLines:N0})");

        return warnings.Count > 0
            ? $"Large PR detected — {string.Join("; ", warnings)}. Consider splitting into smaller PRs for more effective reviews."
            : null;
    }

    internal static string BuildSummaryMarkdown(int pullRequestId, CodeReviewResult result, bool isReReview = false,
        int reviewNumber = 0, ReviewMetadata? priorMetadata = null, List<WorkItemInfo>? workItems = null,
        List<FileChange>? skippedFiles = null, PrSummaryResult? prSummary = null,
        ReviewDepth reviewDepth = ReviewDepth.Standard, DeepAnalysisResult? deepAnalysis = null,
        string? sizeWarning = null)
    {
        var sb = new StringBuilder();
        var s = result.Summary;
        var reviewLabel = reviewNumber > 0 ? $" (Review {reviewNumber})" : "";
        var depthBadge = reviewDepth switch
        {
            ReviewDepth.Quick => " :zap: Quick",
            ReviewDepth.Deep => " :mag: Deep",
            _ => "",
        };

        if (isReReview)
        {
            sb.AppendLine($"## Re-Review{reviewLabel}{depthBadge} -- PR {pullRequestId}");
            sb.AppendLine();

            // Include prior review data in the blockquote
            if (priorMetadata != null && priorMetadata.HasPreviousReview)
            {
                var priorDate = priorMetadata.ReviewedAtUtc.ToString("yyyy-MM-dd HH:mm:ss UTC");
                var priorCommit = priorMetadata.LastReviewedSourceCommit?.Length > 7
                    ? priorMetadata.LastReviewedSourceCommit[..7]
                    : priorMetadata.LastReviewedSourceCommit ?? "unknown";
                var priorIter = priorMetadata.LastReviewedIteration;
                var priorVote = priorMetadata.VoteSubmitted ? "vote submitted" : "no vote";
                var priorDraft = priorMetadata.WasDraft ? " (draft)" : "";

                sb.AppendLine($"> _Re-review triggered by new changes since the last review._");
                sb.AppendLine($"> ");
                sb.AppendLine($"> **Prior review** (Review #{priorMetadata.ReviewCount}): {priorDate} | Commit `{priorCommit}` | Iteration {priorIter} | {priorVote}{priorDraft}");
            }
            else
            {
                sb.AppendLine("> _This is a re-review triggered by new changes since the last review._");
            }
        }
        else
        {
            sb.AppendLine($"## Code Review{reviewLabel}{depthBadge} -- PR {pullRequestId}");
        }
        sb.AppendLine();
        sb.AppendLine("### Summary");
        sb.AppendLine($"{s.FilesChanged} files changed ({s.EditsCount} edits, {s.AddsCount} adds, {s.DeletesCount} deletes). {s.Description}");
        sb.AppendLine();

        // Size guardrail warning
        if (!string.IsNullOrWhiteSpace(sizeWarning))
        {
            sb.AppendLine($"> :warning: **PR Size Warning**: {sizeWarning}");
            sb.AppendLine();
        }

        // Note about skipped non-reviewable files
        if (skippedFiles != null && skippedFiles.Count > 0)
        {
            var grouped = skippedFiles.GroupBy(f => f.SkipReason ?? "unknown").OrderByDescending(g => g.Count());
            var parts = grouped.Select(g => $"{g.Count()} {g.Key}{(g.Count() != 1 ? "s" : "")}");
            sb.AppendLine($"> :file_folder: **{skippedFiles.Count} file(s) excluded** from detailed review: {string.Join(", ", parts)}.");
            sb.AppendLine();
        }

        // Cross-file analysis from Pass 1 (if available)
        if (prSummary != null)
        {
            sb.AppendLine("### Cross-File Analysis");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(prSummary.ArchitecturalImpact) &&
                !prSummary.ArchitecturalImpact.Equals("None", StringComparison.OrdinalIgnoreCase))
            {
                sb.AppendLine($"**Architectural Impact**: {prSummary.ArchitecturalImpact}");
                sb.AppendLine();
            }

            if (prSummary.CrossFileRelationships.Count > 0)
            {
                sb.AppendLine("**Cross-File Relationships**:");
                foreach (var rel in prSummary.CrossFileRelationships)
                    sb.AppendLine($"- {rel}");
                sb.AppendLine();
            }

            if (prSummary.RiskAreas.Count > 0)
            {
                sb.AppendLine("**Risk Areas**:");
                foreach (var risk in prSummary.RiskAreas)
                    sb.AppendLine($"- **{risk.Area}**: {risk.Reason}");
                sb.AppendLine();
            }

            if (prSummary.FileGroupings.Count > 0)
            {
                sb.AppendLine("**File Groupings**:");
                foreach (var group in prSummary.FileGroupings)
                {
                    sb.AppendLine($"- **{group.GroupName}**: {group.Description}");
                    sb.AppendLine($"  Files: {string.Join(", ", group.Files.Select(f => $"`{f}`"))}");
                }
                sb.AppendLine();
            }
        }

        // Deep Analysis section from Pass 3 (Deep mode only)
        if (deepAnalysis != null)
        {
            sb.AppendLine("### Deep Analysis (Pass 3 — Holistic Re-evaluation)");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(deepAnalysis.ExecutiveSummary))
            {
                sb.AppendLine($"**Executive Summary**: {deepAnalysis.ExecutiveSummary}");
                sb.AppendLine();
            }

            sb.AppendLine($"**Overall Risk Level**: {deepAnalysis.OverallRiskLevel}");
            sb.AppendLine();

            if (deepAnalysis.CrossFileIssues.Count > 0)
            {
                sb.AppendLine("**Cross-File Issues** (not visible in per-file reviews):");
                foreach (var issue in deepAnalysis.CrossFileIssues)
                {
                    var files = string.Join(", ", issue.Files.Select(f => $"`{f}`"));
                    sb.AppendLine($"- **[{issue.Severity}]** {issue.Description} ({files})");
                }
                sb.AppendLine();
            }

            if (!deepAnalysis.VerdictConsistency.IsConsistent)
            {
                sb.AppendLine($"> :warning: **Verdict Override**: {deepAnalysis.VerdictConsistency.Explanation}");
                sb.AppendLine();
            }

            if (deepAnalysis.Recommendations.Count > 0)
            {
                sb.AppendLine("**Recommendations**:");
                foreach (var rec in deepAnalysis.Recommendations)
                    sb.AppendLine($"- {rec}");
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();

        // Code Changes Review (only CONCERN/NEEDS WORK/REJECTED or AI-failure entries)
        var filesWithIssues = result.FileReviews
            .Where(fr => fr.Verdict.Equals("CONCERN", StringComparison.OrdinalIgnoreCase)
                         || fr.Verdict.Equals("NEEDS WORK", StringComparison.OrdinalIgnoreCase)
                         || fr.Verdict.Equals("REJECTED", StringComparison.OrdinalIgnoreCase)
                         || fr.ReviewText.Contains("AI review failed", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filesWithIssues.Count > 0)
        {
            sb.AppendLine("### Code Changes Review");
            sb.AppendLine();
            for (int i = 0; i < filesWithIssues.Count; i++)
            {
                var fr = filesWithIssues[i];
                sb.AppendLine($"**{i + 1}. `{fr.FilePath}` -- {fr.Verdict}**");
                sb.AppendLine(fr.ReviewText);
                sb.AppendLine();
            }
            sb.AppendLine("---");
            sb.AppendLine();
        }

        // Verdict
        var isNonApproval = s.Verdict.Contains("REJECTED", StringComparison.OrdinalIgnoreCase)
            || s.Verdict.Contains("NEEDS WORK", StringComparison.OrdinalIgnoreCase);

        sb.AppendLine($"### Verdict: **{s.Verdict}**");
        sb.AppendLine(s.VerdictJustification);

        // For rejections/needs-work, add a prominent "Blocking Issues" section
        // that consolidates all the reasons the PR was not approved.
        if (isNonApproval)
        {
            // Prefer fileReviews for blocking issues; fall back to inline comments
            // with active status (Concern/Bug/Security) when fileReviews is empty.
            var blockingItems = new List<(string FilePath, string Description)>();

            if (filesWithIssues.Count > 0)
            {
                blockingItems.AddRange(filesWithIssues.Select(fr => (fr.FilePath, fr.ReviewText)));
            }
            else if (result.InlineComments.Count > 0)
            {
                // Extract blocking issues from inline comments that are "active"
                var activeComments = result.InlineComments
                    .Where(c => c.Status.Equals("active", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (activeComments.Count == 0)
                    activeComments = result.InlineComments; // fallback to all

                blockingItems.AddRange(activeComments.Select(c =>
                    (c.FilePath, $"[Line {c.StartLine}] **{c.LeadIn}**: {c.Comment}")));
            }

            if (blockingItems.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("---");
                sb.AppendLine();
                sb.AppendLine("### :x: Blocking Issues");
                sb.AppendLine();
                sb.AppendLine("The following issues must be resolved before this PR can be approved:");
                sb.AppendLine();
                for (int i = 0; i < blockingItems.Count; i++)
                {
                    var (filePath, desc) = blockingItems[i];
                    sb.AppendLine($"{i + 1}. **`{filePath}`**: {desc}");
                }
                sb.AppendLine();
            }
        }

        // ── Acceptance Criteria / Definition of Done Analysis ────────────
        AppendAcceptanceCriteriaSection(sb, result, workItems);

        return sb.ToString();
    }

    /// <summary>
    /// Append the AC/DoD compliance analysis section to the summary markdown.
    /// Shows linked work item requirements and per-criterion status from the AI analysis.
    /// </summary>
    private static void AppendAcceptanceCriteriaSection(
        StringBuilder sb, CodeReviewResult result, List<WorkItemInfo>? workItems)
    {
        // Only show if we have work items with AC or the AI produced AC analysis
        var hasAcItems = result.AcceptanceCriteriaAnalysis?.Items?.Count > 0;
        var hasWorkItemsWithAc = workItems?.Any(wi => !string.IsNullOrWhiteSpace(wi.AcceptanceCriteria)) == true;

        if (!hasAcItems && !hasWorkItemsWithAc)
            return;

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("### :clipboard: Acceptance Criteria / DoD Compliance");
        sb.AppendLine();

        // Show linked work item context
        if (hasWorkItemsWithAc)
        {
            foreach (var wi in workItems!.Where(w => !string.IsNullOrWhiteSpace(w.AcceptanceCriteria)))
            {
                sb.AppendLine($"**{wi.WorkItemType} #{wi.Id}**: {wi.Title}");
                sb.AppendLine();
            }
        }

        // Show per-criterion analysis from AI
        if (hasAcItems)
        {
            var analysis = result.AcceptanceCriteriaAnalysis!;
            if (!string.IsNullOrWhiteSpace(analysis.Summary))
            {
                sb.AppendLine(analysis.Summary);
                sb.AppendLine();
            }

            sb.AppendLine("| Status | Criterion | Evidence |");
            sb.AppendLine("|--------|-----------|----------|");

            foreach (var item in analysis.Items)
            {
                var icon = item.Status switch
                {
                    "Addressed" => ":white_check_mark:",
                    "Partially Addressed" => ":large_orange_diamond:",
                    "Not Addressed" => ":x:",
                    "Cannot Determine" => ":grey_question:",
                    _ => ":grey_question:",
                };
                // Escape pipes in text for table formatting
                var criterion = item.Criterion.Replace("|", "\\|");
                var evidence = item.Evidence.Replace("|", "\\|");
                sb.AppendLine($"| {icon} {item.Status} | {criterion} | {evidence} |");
            }
            sb.AppendLine();
        }
        else if (hasWorkItemsWithAc)
        {
            // We had AC but the AI didn't produce analysis (shouldn't normally happen)
            sb.AppendLine("_Acceptance criteria were found on linked work items but the AI did not produce a per-criterion analysis._");
            sb.AppendLine();
        }
    }

    private static string MapVerdictToRecommendation(string verdict) => verdict.ToUpperInvariant() switch
    {
        "APPROVED" => "Approved",
        "APPROVED WITH SUGGESTIONS" => "ApprovedWithSuggestions",
        "NEEDS WORK" => "NeedsWork",
        "REJECTED" => "Rejected",
        _ => "Approved"
    };

    private static string VoteToLabel(int vote) => vote switch
    {
        10 => "Approved",
        5 => "Approved with suggestions",
        -5 => "Waiting for author",
        -10 => "Rejected",
        _ => $"Vote: {vote}"
    };

    private static void ReportProgress(IProgress<ReviewStatusUpdate>? progress, ReviewStep step, string message, int percent)
    {
        progress?.Report(new ReviewStatusUpdate
        {
            Step = step,
            Message = message,
            PercentComplete = percent,
        });
    }

    /// <summary>
    /// Validate and sanitize inline comments returned by the AI.
    /// - Drops comments whose filePath doesn't match any changed file.
    /// - Clamps line numbers to the valid range for the file.
    /// </summary>
    private List<InlineComment> ValidateInlineComments(
        List<InlineComment> comments, List<FileChange> fileChanges)
    {
        // Build lookups: filePath → line count AND filePath → content lines AND filePath → changed ranges
        var fileLineCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var fileContentLines = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var fileChangedRanges = new Dictionary<string, List<(int Start, int End)>>(StringComparer.OrdinalIgnoreCase);
        foreach (var fc in fileChanges)
        {
            var content = fc.ModifiedContent ?? fc.OriginalContent;
            if (!string.IsNullOrEmpty(content))
            {
                var lines = content.Split('\n');
                fileLineCount[fc.FilePath] = lines.Length;
                fileContentLines[fc.FilePath] = lines;
            }
            if (fc.ChangedLineRanges.Count > 0)
                fileChangedRanges[fc.FilePath] = fc.ChangedLineRanges;
        }

        var validated = new List<InlineComment>();
        int snippetResolved = 0;
        int filteredUnchangedCount = 0;
        foreach (var c in comments)
        {
            // Check file path exists in the changed files
            if (!fileLineCount.TryGetValue(c.FilePath, out var maxLines))
            {
                _logger.LogWarning(
                    "AI referenced file '{FilePath}' which is not in the changed files. Dropping comment: {Comment}",
                    c.FilePath, c.Comment.Length > 100 ? c.Comment[..100] + "..." : c.Comment);
                continue;
            }

            // ── Resolve line numbers from codeSnippet if available ──
            if (!string.IsNullOrWhiteSpace(c.CodeSnippet) && fileContentLines.TryGetValue(c.FilePath, out var lines))
            {
                var resolved = ResolveLineFromSnippet(c.CodeSnippet, lines);
                if (resolved.HasValue)
                {
                    var (resolvedStart, resolvedEnd) = resolved.Value;
                    if (c.StartLine != resolvedStart || c.EndLine != resolvedEnd)
                    {
                        _logger.LogInformation(
                            "Snippet match: {File} AI said L{AiStart}-{AiEnd}, resolved to L{ResStart}-{ResEnd} via snippet \"{Snippet}\"",
                            c.FilePath.Split('/')[^1], c.StartLine, c.EndLine, resolvedStart, resolvedEnd,
                            c.CodeSnippet.Length > 60 ? c.CodeSnippet[..60] + "..." : c.CodeSnippet);
                        c.StartLine = resolvedStart;
                        c.EndLine = resolvedEnd;
                        snippetResolved++;
                    }
                }
                else
                {
                    _logger.LogDebug("Snippet not found in {File}: \"{Snippet}\"",
                        c.FilePath.Split('/')[^1],
                        c.CodeSnippet.Length > 80 ? c.CodeSnippet[..80] + "..." : c.CodeSnippet);
                }
            }

            // Clamp line numbers to valid range
            var origStart = c.StartLine;
            var origEnd = c.EndLine;

            c.StartLine = Math.Max(1, Math.Min(c.StartLine, maxLines));
            c.EndLine = Math.Max(c.StartLine, Math.Min(c.EndLine, maxLines));

            if (origStart != c.StartLine || origEnd != c.EndLine)
            {
                _logger.LogWarning(
                    "AI line range {OrigStart}-{OrigEnd} clamped to {NewStart}-{NewEnd} for {FilePath} (max {MaxLines} lines)",
                    origStart, origEnd, c.StartLine, c.EndLine, c.FilePath, maxLines);
            }

            // ── Filter: only keep comments that target changed lines ──
            // Two policies:
            //   1. Proximity: comment is within 5 lines of a changed range (catches single-line/small changes)
            //   2. Density: >40% of lines in a ±25-line window around the comment were changed
            //      (catches method-level rewrites where scattered edits add up)
            // If neither applies, the comment is on truly unchanged code and gets dropped.
            const int proximityWindow = 5;
            const int densityWindow = 25; // ±25 lines ≈ 50-line method scope
            const double densityThreshold = 0.40;
            if (fileChangedRanges.TryGetValue(c.FilePath, out var changedRanges) && changedRanges.Count > 0)
            {
                // Policy 1: proximity check
                bool nearChangedCode = changedRanges.Any(r =>
                    c.StartLine <= r.End + proximityWindow &&
                    c.EndLine >= r.Start - proximityWindow);

                if (!nearChangedCode)
                {
                    // Policy 2: density check — use a ±25-line window around the comment
                    // so that scattered edits across a method are seen as a whole.
                    int regionStart = Math.Max(1, c.StartLine - densityWindow);
                    int fileLines = fileLineCount.TryGetValue(c.FilePath, out var fl) ? fl : c.EndLine;
                    int regionEnd = Math.Min(fileLines, c.EndLine + densityWindow);
                    int regionSpan = regionEnd - regionStart + 1;
                    int changedLinesInRegion = 0;
                    for (int line = regionStart; line <= regionEnd; line++)
                    {
                        if (changedRanges.Any(r => line >= r.Start && line <= r.End))
                            changedLinesInRegion++;
                    }
                    double density = (double)changedLinesInRegion / regionSpan;

                    if (density >= densityThreshold)
                    {
                        var fileName = c.FilePath.Contains('/') ? c.FilePath[(c.FilePath.LastIndexOf('/') + 1)..] : c.FilePath;
                        _logger.LogInformation(
                            "Allowed method-level comment via density ({Density:P0}, {Changed}/{Span} lines in L{RegionStart}-{RegionEnd}): {File} L{Start}-{End} — {LeadIn}",
                            density, changedLinesInRegion, regionSpan, regionStart, regionEnd,
                            fileName, c.StartLine, c.EndLine, c.LeadIn);
                    }
                    else
                    {
                        var fileName = c.FilePath.Contains('/') ? c.FilePath[(c.FilePath.LastIndexOf('/') + 1)..] : c.FilePath;
                        _logger.LogInformation(
                            "Filtered out comment on unchanged code ({Density:P0}, {Changed}/{Span} lines in L{RegionStart}-{RegionEnd}): {File} L{Start}-{End} — {LeadIn}: {Comment}",
                            density, changedLinesInRegion, regionSpan, regionStart, regionEnd,
                            fileName, c.StartLine, c.EndLine, c.LeadIn,
                            c.Comment.Length > 80 ? c.Comment[..80] + "..." : c.Comment);
                        filteredUnchangedCount++;
                        continue;
                    }
                }
            }

            validated.Add(c);
        }

        // ── Server-side false positive filter: "not defined" claims ──
        // If a comment says something is "not defined/missing/not implemented" but the
        // symbol actually exists in the file content, it's a false positive.
        var falsePositivePatterns = new[] { "not defined", "is not defined", "not found", "not implemented", "missing definition", "missing implementation", "ensure it is implemented" };
        int falsePositivesRemoved = 0;
        validated = validated.Where(c =>
        {
            var commentLower = c.Comment.ToLowerInvariant();
            bool claimsMissing = falsePositivePatterns.Any(p => commentLower.Contains(p));
            if (!claimsMissing)
                return true; // Not a "missing reference" claim — keep it

            // Try to extract symbol names from the comment and check if they exist in the file
            if (fileContentLines.TryGetValue(c.FilePath, out var contentLines))
            {
                var fullContent = string.Join("\n", contentLines);
                // Look for identifiers mentioned in backticks or after "The/the" + pattern
                var symbolMatches = System.Text.RegularExpressions.Regex.Matches(
                    c.Comment, @"`(\w+)`|(?:method|class|function|property|variable|interface)\s+['""]?(\w+)['""]?",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

                foreach (System.Text.RegularExpressions.Match m in symbolMatches)
                {
                    var symbol = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
                    if (!string.IsNullOrEmpty(symbol) && fullContent.Contains(symbol, StringComparison.Ordinal))
                    {
                        var fileName = c.FilePath.Contains('/') ? c.FilePath[(c.FilePath.LastIndexOf('/') + 1)..] : c.FilePath;
                        _logger.LogInformation(
                            "Filtered false positive: '{Symbol}' IS defined in {File} but AI claimed it was missing — {LeadIn}: {Comment}",
                            symbol, fileName, c.LeadIn,
                            c.Comment.Length > 100 ? c.Comment[..100] + "..." : c.Comment);
                        falsePositivesRemoved++;
                        return false; // Remove this false positive
                    }
                }
            }

            return true; // Could not verify — keep the comment
        }).ToList();

        if (snippetResolved > 0)
        {
            _logger.LogInformation("Snippet-based line resolution: {Resolved}/{Total} comments resolved via codeSnippet",
                snippetResolved, comments.Count);
        }

        if (validated.Count < comments.Count)
        {
            _logger.LogInformation(
                "Inline comment validation: {Kept}/{Total} kept, {DroppedPath} dropped (invalid path), {DroppedUnchanged} dropped (unchanged code), {DroppedFalsePositive} dropped (false positives)",
                validated.Count, comments.Count,
                comments.Count - validated.Count - filteredUnchangedCount - falsePositivesRemoved,
                filteredUnchangedCount, falsePositivesRemoved);
        }

        return validated;
    }

    /// <summary>
    /// Search for a code snippet in the file's content lines and return the matching line range.
    /// Returns null if the snippet isn't found.
    /// </summary>
    private static (int start, int end)? ResolveLineFromSnippet(string snippet, string[] lines)
    {
        // Normalize the snippet: trim, collapse whitespace
        var snippetTrimmed = snippet.Trim();
        if (string.IsNullOrEmpty(snippetTrimmed)) return null;

        // Try exact substring match on each line (first line of snippet)
        var snippetFirstLine = snippetTrimmed.Split('\n')[0].Trim();
        if (string.IsNullOrEmpty(snippetFirstLine)) return null;

        // Search for the first line of the snippet in the file content
        int matchLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].TrimEnd('\r').Trim().Contains(snippetFirstLine, StringComparison.Ordinal))
            {
                matchLine = i;
                break;
            }
        }

        if (matchLine < 0)
        {
            // Try case-insensitive
            for (int i = 0; i < lines.Length; i++)
            {
                if (lines[i].TrimEnd('\r').Trim().Contains(snippetFirstLine, StringComparison.OrdinalIgnoreCase))
                {
                    matchLine = i;
                    break;
                }
            }
        }

        if (matchLine < 0) return null;

        // Count how many lines in the snippet to determine endLine
        var snippetLines = snippetTrimmed.Split('\n');
        int startLine = matchLine + 1; // 1-based
        int endLine = Math.Min(startLine + snippetLines.Length - 1, lines.Length);

        return (startLine, endLine);
    }

    /// <summary>
    /// Split a list of files into batches of at most <paramref name="batchSize"/> files each.
    /// </summary>
    private static List<List<FileChange>> ChunkFiles(List<FileChange> files, int batchSize)
    {
        var batches = new List<List<FileChange>>();
        for (int i = 0; i < files.Count; i += batchSize)
        {
            batches.Add(files.GetRange(i, Math.Min(batchSize, files.Count - i)));
        }
        return batches;
    }

    /// <summary>
    /// Merge multiple batch <see cref="CodeReviewResult"/>s into a single unified result.
    /// Inline comments and file reviews are concatenated. Observations are deduplicated.
    /// The overall verdict is the most severe across all batches.
    /// AI metrics are summed.
    /// </summary>
    private static CodeReviewResult MergeBatchResults(List<CodeReviewResult> batchResults, int totalFilesChanged)
    {
        if (batchResults.Count == 1)
            return batchResults[0]; // No merging needed

        var merged = new CodeReviewResult();

        // ── Aggregate inline comments ───────────────────────────────────
        foreach (var batch in batchResults)
            merged.InlineComments.AddRange(batch.InlineComments);

        // ── Aggregate file reviews ──────────────────────────────────────
        foreach (var batch in batchResults)
            merged.FileReviews.AddRange(batch.FileReviews);

        // ── Deduplicate observations ────────────────────────────────────
        var seenObs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in batchResults)
        {
            foreach (var obs in batch.Observations)
            {
                if (seenObs.Add(obs))
                    merged.Observations.Add(obs);
            }
        }

        // ── Determine overall verdict (most severe wins) ────────────────
        // Severity order: REJECTED > NEEDS WORK > APPROVED WITH SUGGESTIONS > APPROVED
        var verdictPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["APPROVED"] = 0,
            ["APPROVED WITH SUGGESTIONS"] = 1,
            ["NEEDS WORK"] = 2,
            ["REJECTED"] = 3,
        };

        string worstVerdict = "APPROVED";
        int worstVote = 10;
        string worstJustification = "";

        foreach (var batch in batchResults)
        {
            var batchVerdict = batch.Summary.Verdict.ToUpperInvariant().Trim();
            var batchPriority = verdictPriority.GetValueOrDefault(batchVerdict, 1);
            var currentPriority = verdictPriority.GetValueOrDefault(worstVerdict, 0);

            if (batchPriority > currentPriority)
            {
                worstVerdict = batch.Summary.Verdict;
                worstJustification = batch.Summary.VerdictJustification;
            }

            if (batch.RecommendedVote < worstVote)
                worstVote = batch.RecommendedVote;
        }

        merged.RecommendedVote = worstVote;

        // ── Build merged summary ────────────────────────────────────────
        int totalEdits = 0, totalAdds = 0, totalDeletes = 0;

        foreach (var batch in batchResults)
        {
            totalEdits += batch.Summary.EditsCount;
            totalAdds += batch.Summary.AddsCount;
            totalDeletes += batch.Summary.DeletesCount;
        }

        merged.Summary = new ReviewSummary
        {
            FilesChanged = totalFilesChanged,
            EditsCount = totalEdits,
            AddsCount = totalAdds,
            DeletesCount = totalDeletes,
            Description = "", // Per-file descriptions are not useful in merged context
            Verdict = worstVerdict,
            VerdictJustification = worstJustification,
        };

        // ── Aggregate AI metrics ────────────────────────────────────────
        int? totalPrompt = null, totalCompletion = null, totalTokens = null;
        long? totalAiMs = null;

        foreach (var batch in batchResults)
        {
            if (batch.PromptTokens.HasValue)
                totalPrompt = (totalPrompt ?? 0) + batch.PromptTokens.Value;
            if (batch.CompletionTokens.HasValue)
                totalCompletion = (totalCompletion ?? 0) + batch.CompletionTokens.Value;
            if (batch.TotalTokens.HasValue)
                totalTokens = (totalTokens ?? 0) + batch.TotalTokens.Value;
            if (batch.AiDurationMs.HasValue)
                totalAiMs = (totalAiMs ?? 0) + batch.AiDurationMs.Value;
        }

        merged.ModelName = batchResults[0].ModelName;
        merged.PromptTokens = totalPrompt;
        merged.CompletionTokens = totalCompletion;
        merged.TotalTokens = totalTokens;
        merged.AiDurationMs = totalAiMs;

        // ── Merge acceptance criteria analysis from per-file results ────
        var allAcItems = new List<AcceptanceCriteriaItem>();
        foreach (var batch in batchResults)
        {
            if (batch.AcceptanceCriteriaAnalysis?.Items != null)
                allAcItems.AddRange(batch.AcceptanceCriteriaAnalysis.Items);
        }

        if (allAcItems.Count > 0)
        {
            // Deduplicate by criterion text using a conservative merge strategy:
            // if some files say "Addressed" but others disagree, downgrade to "Partially Addressed"
            // to avoid misleading optimistic results when only one file partially covers a criterion.
            var statusPriority = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
            {
                ["Addressed"] = 3,
                ["Partially Addressed"] = 2,
                ["Cannot Determine"] = 1,
                ["Not Addressed"] = 0,
            };

            var grouped = allAcItems
                .GroupBy(a => a.Criterion, StringComparer.OrdinalIgnoreCase)
                .Select(g =>
                {
                    var items = g.ToList();
                    // Combine evidence from all files
                    var allEvidence = items.Select(a => a.Evidence).Where(e => !string.IsNullOrWhiteSpace(e)).Distinct();

                    string mergedStatus;
                    if (items.Count == 1)
                    {
                        mergedStatus = items[0].Status;
                    }
                    else
                    {
                        var statuses = items.Select(a => a.Status).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                        bool hasAddressed = statuses.Any(s => string.Equals(s, "Addressed", StringComparison.OrdinalIgnoreCase));
                        bool hasConflicting = statuses.Any(s =>
                            string.Equals(s, "Not Addressed", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(s, "Cannot Determine", StringComparison.OrdinalIgnoreCase));

                        // Conservative: downgrade to "Partially Addressed" when files disagree
                        if (hasAddressed && hasConflicting)
                            mergedStatus = "Partially Addressed";
                        else
                            mergedStatus = items.OrderByDescending(a => statusPriority.GetValueOrDefault(a.Status, 0)).First().Status;
                    }

                    return new AcceptanceCriteriaItem
                    {
                        Criterion = g.Key,
                        Status = mergedStatus,
                        Evidence = string.Join(" | ", allEvidence),
                    };
                })
                .ToList();

            merged.AcceptanceCriteriaAnalysis = new AcceptanceCriteriaAnalysis
            {
                Summary = $"AC analysis merged from {batchResults.Count} file reviews.",
                Items = grouped,
            };
        }

        return merged;
    }
}
