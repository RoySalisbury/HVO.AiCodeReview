using System.Diagnostics;
using System.Text;
using AiCodeReview.Models;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Services;

public class CodeReviewOrchestrator : ICodeReviewOrchestrator
{
    private readonly IAzureDevOpsService _devOpsService;
    private readonly ICodeReviewService _reviewService;
    private readonly AzureDevOpsSettings _devOpsSettings;
    private readonly AiProviderSettings _aiProviderSettings;
    private readonly IReviewRateLimiter _rateLimiter;
    private readonly ILogger<CodeReviewOrchestrator> _logger;

    public CodeReviewOrchestrator(
        IAzureDevOpsService devOpsService,
        ICodeReviewService reviewService,
        IOptions<AzureDevOpsSettings> devOpsSettings,
        IOptions<AiProviderSettings> aiProviderSettings,
        IReviewRateLimiter rateLimiter,
        ILogger<CodeReviewOrchestrator> logger)
    {
        _devOpsService = devOpsService;
        _reviewService = reviewService;
        _devOpsSettings = devOpsSettings.Value;
        _aiProviderSettings = aiProviderSettings.Value;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task<ReviewResponse> ExecuteReviewAsync(
        string project,
        string repository,
        int pullRequestId,
        IProgress<ReviewStatusUpdate>? progress = null)
    {
        try
        {
            // ── Step 0: Rate-limit check (in-memory, no API calls) ──────
            var (allowed, secondsRemaining, lastReviewedUtc) = _rateLimiter.Check(
                _devOpsSettings.Organization, project, repository, pullRequestId,
                _devOpsSettings.MinReviewIntervalMinutes);

            if (!allowed)
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
            var action = DetermineAction(prInfo, metadata, currentIteration);

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
                        currentIteration, action == ReviewAction.ReReview, progress);

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
        IProgress<ReviewStatusUpdate>? progress)
    {
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

        if (fileChanges.Count == 0)
        {
            _logger.LogWarning("No reviewable file changes found for PR #{PrId}", pullRequestId);

            await _devOpsService.PostCommentThreadAsync(project, repository, pullRequestId,
                "## Code Review -- PR " + pullRequestId + "\n\nNo reviewable file changes found in this PR.",
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

            ReportProgress(progress, ReviewStep.Complete,
                "No reviewable files found. Auto-approved.", 100);

            return new ReviewResponse
            {
                Status = "Reviewed",
                Recommendation = "Approved",
                Summary = "No reviewable file changes found. Auto-approved.",
                Vote = 10,
            };
        }

        // ── AI analysis (parallel per-file for accuracy) ──────────────
        var maxParallel = Math.Max(1, _aiProviderSettings.MaxParallelReviews);

        ReportProgress(progress, ReviewStep.AnalyzingCode,
            $"Analyzing {fileChanges.Count} files with AI (parallel, max {maxParallel} concurrent, {reviewLabel.ToLower()})...", 35);

        _logger.LogInformation("{Label}: Reviewing {FileCount} files in parallel (max {MaxParallel} concurrent)",
            reviewLabel, fileChanges.Count, maxParallel);

        var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
        int completedFiles = 0;
        var perFileResults = new CodeReviewResult[fileChanges.Count];

        var tasks = fileChanges.Select((file, index) => Task.Run(async () =>
        {
            await semaphore.WaitAsync();
            try
            {
                var result = await _reviewService.ReviewFileAsync(prInfo, file, fileChanges.Count);
                perFileResults[index] = result;

                var done = Interlocked.Increment(ref completedFiles);
                var pct = 35 + (int)(25.0 * done / fileChanges.Count);
                ReportProgress(progress, ReviewStep.AnalyzingCode,
                    $"Analyzed {done}/{fileChanges.Count} files...", pct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI review failed for {FilePath}", file.FilePath);
                // Return a safe fallback result for this file
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
        })).ToArray();

        await Task.WhenAll(tasks);

        // ── Merge per-file results ──────────────────────────────────────
        var reviewResult = MergeBatchResults(perFileResults.ToList(), fileChanges.Count);

        _logger.LogInformation("{Label} complete ({FileCount} files, parallel): {Verdict} with {InlineCount} inline comments",
            reviewLabel, fileChanges.Count,
            reviewResult.Summary.Verdict, reviewResult.InlineComments.Count);

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

        // ── Resolve fixed threads from prior AI reviews ─────────────────
        List<ExistingCommentThread> existingThreads = new();
        int resolvedThreads = 0;
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

                    var verificationResults = await _reviewService.VerifyThreadResolutionsAsync(verificationCandidates);

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

        // ── Post inline comments (with deduplication) ───────────────────
        ReportProgress(progress, ReviewStep.PostingInlineComments,
            $"Posting inline comments (deduplicating against existing threads)...", 65);

        int postedComments = 0;
        int skippedDuplicates = 0;
        foreach (var comment in lineSpecificComments)
        {
            var commentContent = $"**{comment.LeadIn}.** {comment.Comment}{attributionSuffix}";

            // Dedup: skip if same file + same line range + same core content already exists
            // (strip attribution tag from comparison since old comments may not have it)
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

        _logger.LogInformation("Posted {Count}/{Total} inline comments, skipped {Dupes} duplicates, resolved {Resolved} prior threads",
            postedComments, reviewResult.InlineComments.Count, skippedDuplicates, resolvedThreads);

        // ── Post summary comment ────────────────────────────────────────
        ReportProgress(progress, ReviewStep.PostingSummary,
            "Posting review summary...", 80);

        var summaryMarkdown = BuildSummaryMarkdown(pullRequestId, reviewResult, isReReview,
            nextReviewNumber, isReReview ? metadata : null);
        await _devOpsService.PostCommentThreadAsync(
            project, repository, pullRequestId, summaryMarkdown, "closed");

        // ── Tag + metadata ──────────────────────────────────────────────
        ReportProgress(progress, ReviewStep.SubmittingVote,
            "Updating review metadata...", 85);

        bool voteFailed = false;
        bool voteSkipped = false;
        var vote = reviewResult.RecommendedVote;
        var verdictLabel = VoteToLabel(vote);

        // ── Vote (non-draft + config-enabled) ───────────────────────────
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

        totalSw.Stop();

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
        };
        await UpdateMetadataAndTag(project, repository, pullRequestId, prInfo, currentIteration,
            !voteFailed && !voteSkipped, historyEntry);

        // Record in rate limiter
        _rateLimiter.Record(_devOpsSettings.Organization, project, repository, pullRequestId);

        // ── Complete ────────────────────────────────────────────────────
        var completionMsg = voteFailed
            ? $"{reviewLabel} complete: {reviewResult.Summary.Verdict} (vote failed)"
            : voteSkipped
                ? $"{reviewLabel} complete: {reviewResult.Summary.Verdict} (vote skipped)"
                : $"{reviewLabel} complete: {reviewResult.Summary.Verdict}";
        ReportProgress(progress, ReviewStep.Complete, completionMsg, 100);

        int errors = reviewResult.InlineComments.Count(c => c.LeadIn is "Bug" or "Security");
        int warnings = reviewResult.InlineComments.Count(c => c.LeadIn is "Concern" or "Performance");
        int info = reviewResult.InlineComments.Count(c => c.LeadIn is "Suggestion" or "LGTM" or "Good catch" or "Important");

        return new ReviewResponse
        {
            Status = "Reviewed",
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
        };
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

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

    private static string BuildSummaryMarkdown(int pullRequestId, CodeReviewResult result, bool isReReview = false,
        int reviewNumber = 0, ReviewMetadata? priorMetadata = null)
    {
        var sb = new StringBuilder();
        var s = result.Summary;
        var reviewLabel = reviewNumber > 0 ? $" (Review {reviewNumber})" : "";

        if (isReReview)
        {
            sb.AppendLine($"## Re-Review{reviewLabel} -- PR {pullRequestId}");
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
            sb.AppendLine($"## Code Review{reviewLabel} -- PR {pullRequestId}");
        }
        sb.AppendLine();
        sb.AppendLine("### Summary");
        sb.AppendLine($"{s.FilesChanged} files changed ({s.EditsCount} edits, {s.AddsCount} adds, {s.DeletesCount} deletes). {s.Description}");
        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine();

        // Code Changes Review (only CONCERN/REJECTED or AI-failure entries)
        var filesWithIssues = result.FileReviews
            .Where(fr => fr.Verdict.Equals("CONCERN", StringComparison.OrdinalIgnoreCase)
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
        sb.AppendLine($"### Verdict: **{s.Verdict}**");
        sb.AppendLine(s.VerdictJustification);

        return sb.ToString();
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

        return merged;
    }
}
