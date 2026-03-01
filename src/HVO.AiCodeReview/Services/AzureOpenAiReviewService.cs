using System.ClientModel;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AiCodeReview.Models;
using Azure.AI.OpenAI;
using Azure.AI.OpenAI.Chat;
using HVO.Enterprise.Telemetry.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI.Chat;

namespace AiCodeReview.Services;

/// <summary>
/// Azure OpenAI implementation of <see cref="ICodeReviewService"/>.
/// Sends code to an Azure OpenAI deployment (e.g. gpt-4o) for review.
/// </summary>
public class AzureOpenAiReviewService : ICodeReviewService
{
    private readonly string _modelName;
    private readonly ILogger<AzureOpenAiReviewService> _logger;
    private readonly ChatClient _chatClient;
    private readonly int _maxInputLinesPerFile;
    private readonly ReviewProfile _reviewProfile;

    /// <inheritdoc />
    public string ModelName => _modelName;

    // Model adapter (per-model tuning) — null when no adapter is resolved
    private readonly ModelAdapter? _modelAdapter;

    // Prompt pipeline (layered rule catalog) — null when no catalog is loaded
    private readonly PromptAssemblyPipeline? _pipeline;
    private readonly string? _customInstructions;

    // Hardcoded fallback prompts (used when pipeline is not available)
    private readonly string _fallbackSystemPrompt;
    private readonly string _fallbackSingleFileSystemPrompt;
    private readonly string _fallbackPrSummarySystemPrompt;

    // Global rate-limit cooldown signal (shared across all service instances)
    private readonly IGlobalRateLimitSignal? _rateLimitSignal;

    // Telemetry service for operation scopes and metrics
    private readonly ITelemetryService? _telemetry;

    // Global AI call throttle — limits concurrent inference calls across all reviews
    private readonly IAiCallThrottle? _aiCallThrottle;

    // ── Legacy constructor: used by direct DI registration via IOptions ──

    [ExcludeFromCodeCoverage(Justification = "Creates AzureOpenAIClient (SDK) which cannot be mocked.")]
    public AzureOpenAiReviewService(
        IOptions<AzureOpenAISettings> settings,
        ILogger<AzureOpenAiReviewService> logger,
        IOptions<ReviewProfile>? reviewProfileOptions = null)
        : this(
            settings.Value.Endpoint,
            settings.Value.ApiKey,
            settings.Value.DeploymentName,
            settings.Value.CustomInstructionsPath,
            logger,
            maxInputLinesPerFile: 5000,
            reviewProfile: reviewProfileOptions?.Value)
    { }

    // ── Factory constructor: used by CodeReviewServiceFactory from ProviderConfig ──

    [ExcludeFromCodeCoverage(Justification = "Creates AzureOpenAIClient (SDK) which cannot be mocked.")]
    public AzureOpenAiReviewService(
        string endpoint,
        string apiKey,
        string modelName,
        string? customInstructionsPath,
        ILogger<AzureOpenAiReviewService> logger,
        int maxInputLinesPerFile = 5000,
        ReviewProfile? reviewProfile = null,
        PromptAssemblyPipeline? pipeline = null,
        ModelAdapter? modelAdapter = null,
        IGlobalRateLimitSignal? rateLimitSignal = null,
        ITelemetryService? telemetry = null,
        IAiCallThrottle? aiCallThrottle = null)
    {
        _modelName = modelName;
        _logger = logger;
        _pipeline = pipeline;
        _modelAdapter = modelAdapter;
        _rateLimitSignal = rateLimitSignal;
        _telemetry = telemetry;
        _aiCallThrottle = aiCallThrottle;

        // Apply model adapter overrides (temperature, tokens, truncation)
        var baseProfile = reviewProfile ?? new ReviewProfile();
        _reviewProfile = modelAdapter != null
            ? ModelAdapterResolver.ApplyOverrides(baseProfile, modelAdapter)
            : baseProfile;
        _maxInputLinesPerFile = modelAdapter != null
            ? ModelAdapterResolver.GetEffectiveMaxInputLines(maxInputLinesPerFile, modelAdapter)
            : maxInputLinesPerFile;

        // Load custom instructions once (used by both pipeline and fallback paths)
        _customInstructions = LoadCustomInstructions(customInstructionsPath);

        var client = new AzureOpenAIClient(
            new Uri(endpoint),
            new ApiKeyCredential(apiKey));

        _chatClient = client.GetChatClient(modelName);

        // Build hardcoded fallback prompts (used when pipeline has no catalog)
        _fallbackSystemPrompt = BuildSystemPrompt(_customInstructions);
        _fallbackSingleFileSystemPrompt = BuildSingleFileSystemPrompt(_customInstructions);
        _fallbackPrSummarySystemPrompt = BuildPrSummarySystemPrompt();

        var batchPrompt = GetSystemPrompt();
        var singlePrompt = GetSingleFileSystemPrompt();
        var prSummaryPrompt = GetPrSummarySystemPrompt();
        var source = _pipeline?.HasCatalog == true ? "catalog" : "hardcoded";
        var adapterName = _modelAdapter?.Name ?? "none";
        _logger.LogInformation("[{Provider}] System prompts assembled from {Source}, adapter: {Adapter} (multi-file: {MultiLen} chars, single-file: {SingleLen} chars, pr-summary: {SumLen} chars, max input lines/file: {MaxLines}, temperature: {Temp}, batch tokens: {BatchTok}, single-file tokens: {SFTok}, verification tokens: {VerTok}, pr-summary tokens: {PrTok})",
            modelName, source, adapterName, batchPrompt.Length, singlePrompt.Length, prSummaryPrompt.Length,
            _maxInputLinesPerFile, _reviewProfile.Temperature, _reviewProfile.MaxOutputTokensBatch, _reviewProfile.MaxOutputTokensSingleFile, _reviewProfile.MaxOutputTokensVerification, _reviewProfile.MaxOutputTokensPrSummary);
    }

    // ─── Helper: build options with reasoning-model awareness ────────────

    /// <summary>
    /// Builds <see cref="ChatCompletionOptions"/> honoring the model adapter's
    /// <see cref="ModelAdapter.IsReasoningModel"/> flag.
    /// Reasoning models (o1, o3, …) do not support <c>Temperature</c> or
    /// <c>ResponseFormat = JSON</c> at the API level, and require the
    /// <c>SetNewMaxCompletionTokensPropertyEnabled</c> opt-in for the
    /// <c>max_completion_tokens</c> wire property.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Uses Azure SDK ChatCompletionOptions / SetNewMaxCompletionTokensPropertyEnabled.")]
    private ChatCompletionOptions BuildChatOptions(int maxOutputTokens)
    {
        var isReasoning = _modelAdapter?.IsReasoningModel == true;

        var options = new ChatCompletionOptions
        {
            MaxOutputTokenCount = maxOutputTokens,
        };

        // All newer Azure OpenAI models require max_completion_tokens
        // instead of max_tokens on the wire.
#pragma warning disable AOAI001
        options.SetNewMaxCompletionTokensPropertyEnabled(true);
#pragma warning restore AOAI001

        if (!isReasoning)
        {
            options.Temperature = _reviewProfile.Temperature;
            options.ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat();
        }

        return options;
    }

    // ─── Prompt accessors (pipeline → fallback) ─────────────────────────

    /// <summary>
    /// Returns the system prompt for batch (multi-file) reviews.
    /// Uses the pipeline catalog if available, otherwise falls back to hardcoded.
    /// </summary>
    internal string GetSystemPrompt()
        => _pipeline?.AssemblePrompt("batch", _customInstructions, _modelAdapter?.Preamble) ?? _fallbackSystemPrompt;

    /// <summary>
    /// Returns the system prompt for single-file reviews.
    /// </summary>
    internal string GetSingleFileSystemPrompt()
        => _pipeline?.AssemblePrompt("single-file", _customInstructions, _modelAdapter?.Preamble) ?? _fallbackSingleFileSystemPrompt;

    /// <summary>
    /// Returns the system prompt for PR-level summary (Pass 1).
    /// </summary>
    internal string GetPrSummarySystemPrompt()
        => _pipeline?.AssemblePrompt("pass-1", modelPreamble: _modelAdapter?.Preamble) ?? _fallbackPrSummarySystemPrompt;

    /// <summary>
    /// Returns the system prompt for thread verification.
    /// </summary>
    internal string GetThreadVerificationSystemPrompt()
        => _pipeline?.AssemblePrompt("thread-verification", modelPreamble: _modelAdapter?.Preamble) ?? ThreadVerificationSystemPrompt;

    /// <summary>
    /// Wraps <see cref="ChatClient.CompleteChatAsync"/> with the global AI call throttle
    /// (when registered). Acquires a permit before calling, releases after — even on failure.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Thin wrapper around Azure OpenAI SDK call; tested via LiveAI integration tests.")]
    private async Task<ClientResult<ChatCompletion>> ThrottledCompleteChatAsync(
        IList<ChatMessage> messages, ChatCompletionOptions options)
    {
        if (_aiCallThrottle == null)
            return await _chatClient.CompleteChatAsync(messages, options);

        await _aiCallThrottle.AcquireAsync();
        try
        {
            return await _chatClient.CompleteChatAsync(messages, options);
        }
        finally
        {
            _aiCallThrottle.Release();
        }
    }

    [ExcludeFromCodeCoverage(Justification = "Calls _chatClient.CompleteChatAsync (Azure OpenAI SDK).")]
    public async Task<CodeReviewResult> ReviewAsync(PullRequestInfo pullRequest, List<FileChange> fileChanges, List<WorkItemInfo>? workItems = null)
    {
        var systemPrompt = GetSystemPrompt();
        var userPrompt = BuildUserPrompt(pullRequest, fileChanges, workItems);

        _logger.LogInformation("Sending {FileCount} files to AI for review of PR #{PrId}",
            fileChanges.Count, pullRequest.PullRequestId);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt),
        };

        var options = BuildChatOptions(_reviewProfile.MaxOutputTokensBatch);

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Retry with Retry-After-aware backoff for rate limiting (HTTP 429)
        var totalRetryTime = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_rateLimitSignal != null)
                    await _rateLimitSignal.WaitIfCoolingDownAsync();

                response = await ThrottledCompleteChatAsync(messages, options);
                break;
            }
            catch (ClientResultException cex) when (cex.Status == 429 && attempt < RateLimitHelper.MaxRateLimitRetries)
            {
                var delay = RateLimitHelper.ComputeRetryDelay(cex);
                totalRetryTime += delay;

                if (totalRetryTime > RateLimitHelper.MaxTotalRetryDuration)
                {
                    _logger.LogError(
                        "Rate limit retries exhausted for batch review of PR #{PrId}: cumulative wait {Total:F0}s exceeds {Max}s cap",
                        pullRequest.PullRequestId, totalRetryTime.TotalSeconds, RateLimitHelper.MaxTotalRetryDuration.TotalSeconds);
                    throw;
                }

                _logger.LogWarning(
                    "Rate limited (429) during batch review of PR #{PrId}, retry {Attempt}/{Max} after {Delay}s (cumulative: {Total:F0}s)",
                    pullRequest.PullRequestId, attempt + 1, RateLimitHelper.MaxRateLimitRetries, delay.TotalSeconds, totalRetryTime.TotalSeconds);

                _rateLimitSignal?.SignalCooldown(delay);

                await Task.Delay(delay);
            }
            catch (ClientResultException cex) when (cex.Status == 429)
            {
                _logger.LogError(cex,
                    "Rate limit retries exhausted for batch review of PR #{PrId} after {Attempts} attempts. Status: {Status}",
                    pullRequest.PullRequestId, attempt + 1, cex.Status);
                throw;
            }
            catch (ClientResultException cex)
            {
                _logger.LogError(cex, "Azure OpenAI API error. Status: {Status}. Message: {Message}",
                    cex.Status, cex.Message);
                throw;
            }
        }
        sw.Stop();
        var content = response.Value.Content[0].Text;

        // Capture token usage from the response
        var usage = response.Value.Usage;
        int? promptTokens = usage != null ? usage.InputTokenCount : null;
        int? completionTokens = usage != null ? usage.OutputTokenCount : null;
        int? totalTokens = usage != null ? usage.TotalTokenCount : null;
        long aiDurationMs = sw.ElapsedMilliseconds;

        _logger.LogInformation("AI response: {Length} chars | {PromptTokens} prompt + {CompletionTokens} completion = {TotalTokens} tokens | {DurationMs}ms | Model: {Model}",
            content.Length, promptTokens, completionTokens, totalTokens, aiDurationMs, _modelName);

        // Log raw inline comment details for debugging line-number accuracy
        _logger.LogInformation("AI inline line-ranges: {Excerpt}",
            ExtractInlineExcerpt(content));

        try
        {
            var result = JsonSerializer.Deserialize<CodeReviewResult>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (result == null) result = new CodeReviewResult();

            // Attach AI metrics (not from AI JSON, set by us)
            result.ModelName = _modelName;
            result.PromptTokens = promptTokens;
            result.CompletionTokens = completionTokens;
            result.TotalTokens = totalTokens;
            result.AiDurationMs = aiDurationMs;

            // Record telemetry metrics
            if (promptTokens.HasValue) _telemetry?.RecordMetric("ai.prompt_tokens", promptTokens.Value);
            if (completionTokens.HasValue) _telemetry?.RecordMetric("ai.completion_tokens", completionTokens.Value);
            _telemetry?.RecordMetric("ai.duration_ms", aiDurationMs);

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse AI response as JSON. Raw response: {Response}", content);
            // Return a minimal result with the raw response as the summary
            return new CodeReviewResult
            {
                Summary = new ReviewSummary
                {
                    Description = "AI returned a non-structured response. See server logs for details.",
                    Verdict = "APPROVED",
                    VerdictJustification = "Unable to parse AI response; defaulting to approved."
                }
            };
        }
    }

    /// <summary>
    /// Review a single file in its own dedicated AI call for maximum accuracy.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Calls _chatClient.CompleteChatAsync (Azure OpenAI SDK).")]
    public async Task<CodeReviewResult> ReviewFileAsync(PullRequestInfo pullRequest, FileChange file, int totalFilesInPr, List<WorkItemInfo>? workItems = null)
    {
        using var opScope = _telemetry?.StartOperation("AI.ReviewFile");
        opScope?.WithTag("ai.model", _modelName)
               .WithTag("file.path", file.FilePath)
               .WithTag("file.change_type", file.ChangeType)
               .WithTag("pr.id", pullRequest.PullRequestId);

        var userPrompt = BuildSingleFileUserPrompt(pullRequest, file, totalFilesInPr, workItems);

        _logger.LogInformation("Reviewing file {FilePath} ({ChangeType}) for PR #{PrId}",
            file.FilePath, file.ChangeType, pullRequest.PullRequestId);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(GetSingleFileSystemPrompt()),
            new UserChatMessage(userPrompt),
        };

        var options = BuildChatOptions(_reviewProfile.MaxOutputTokensSingleFile);

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Retry with Retry-After-aware backoff for rate limiting (HTTP 429)
        var totalRetryTime = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                // Wait for any global cooldown before making the call
                if (_rateLimitSignal != null)
                    await _rateLimitSignal.WaitIfCoolingDownAsync();

                response = await ThrottledCompleteChatAsync(messages, options);
                break; // Success
            }
            catch (ClientResultException cex) when (cex.Status == 429 && attempt < RateLimitHelper.MaxRateLimitRetries)
            {
                var delay = RateLimitHelper.ComputeRetryDelay(cex);
                totalRetryTime += delay;

                if (totalRetryTime > RateLimitHelper.MaxTotalRetryDuration)
                {
                    _logger.LogError(
                        "Rate limit retries exhausted for {FilePath}: cumulative wait {Total:F0}s exceeds {Max}s cap",
                        file.FilePath, totalRetryTime.TotalSeconds, RateLimitHelper.MaxTotalRetryDuration.TotalSeconds);
                    opScope?.Fail(cex);
                    opScope?.RecordException(cex);
                    throw;
                }

                _logger.LogWarning(
                    "Rate limited (429) reviewing {FilePath}, retry {Attempt}/{Max} after {Delay}s (cumulative: {Total:F0}s)",
                    file.FilePath, attempt + 1, RateLimitHelper.MaxRateLimitRetries, delay.TotalSeconds, totalRetryTime.TotalSeconds);

                // Signal all concurrent callers to pause
                _rateLimitSignal?.SignalCooldown(delay);

                await Task.Delay(delay);
            }
            catch (ClientResultException cex) when (cex.Status == 429)
            {
                _logger.LogError(cex,
                    "Rate limit retries exhausted for {FilePath} after {Attempts} attempts. Status: {Status}",
                    file.FilePath, attempt + 1, cex.Status);
                opScope?.Fail(cex);
                opScope?.RecordException(cex);
                throw;
            }
            catch (ClientResultException cex)
            {
                _logger.LogError(cex, "AI error reviewing {FilePath}. Status: {Status}", file.FilePath, cex.Status);
                opScope?.Fail(cex);
                opScope?.RecordException(cex);
                throw;
            }
        }
        sw.Stop();
        var content = response.Value.Content[0].Text;

        var usage = response.Value.Usage;
        int? promptTokens = usage?.InputTokenCount;
        int? completionTokens = usage?.OutputTokenCount;
        int? totalTokens = usage?.TotalTokenCount;
        long aiDurationMs = sw.ElapsedMilliseconds;

        var fileName = file.FilePath.Contains('/') ? file.FilePath[(file.FilePath.LastIndexOf('/') + 1)..] : file.FilePath;
        _logger.LogInformation("  {FileName}: {Len} chars | {Prompt}+{Completion}={Total} tokens | {Ms}ms | Lines: {Excerpt}",
            fileName, content.Length, promptTokens, completionTokens, totalTokens, aiDurationMs,
            ExtractInlineExcerpt(content));

        try
        {
            var result = JsonSerializer.Deserialize<CodeReviewResult>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            if (result == null) result = new CodeReviewResult();

            result.ModelName = _modelName;
            result.PromptTokens = promptTokens;
            result.CompletionTokens = completionTokens;
            result.TotalTokens = totalTokens;
            result.AiDurationMs = aiDurationMs;

            // Record telemetry metrics
            if (promptTokens.HasValue) _telemetry?.RecordMetric("ai.prompt_tokens", promptTokens.Value);
            if (completionTokens.HasValue) _telemetry?.RecordMetric("ai.completion_tokens", completionTokens.Value);
            _telemetry?.RecordMetric("ai.duration_ms", aiDurationMs);
            opScope?.WithTag("ai.prompt_tokens", promptTokens ?? 0)
                   .WithTag("ai.completion_tokens", completionTokens ?? 0)
                   .Succeed();

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse single-file AI response for {FilePath}", file.FilePath);
            opScope?.Fail(ex);
            opScope?.RecordException(ex);
            return new CodeReviewResult
            {
                Summary = new ReviewSummary
                {
                    Verdict = "APPROVED",
                    VerdictJustification = $"Unable to parse AI response for {file.FilePath}.",
                },
                FileReviews = new List<FileReview>
                {
                    new FileReview { FilePath = file.FilePath, Verdict = "OBSERVATION", ReviewText = "AI response parse failed." }
                }
            };
        }
    }

    /// <summary>
    /// Verify whether prior AI review comments have been addressed by examining
    /// the current code at those locations. Uses a single batched AI call for efficiency.
    /// </summary>
    [ExcludeFromCodeCoverage(Justification = "Calls _chatClient.CompleteChatAsync (Azure OpenAI SDK).")]
    public async Task<List<ThreadVerificationResult>> VerifyThreadResolutionsAsync(
        List<ThreadVerificationCandidate> candidates)
    {
        if (candidates.Count == 0)
            return new List<ThreadVerificationResult>();

        _logger.LogInformation("Verifying {Count} prior AI comment(s) for resolution...", candidates.Count);

        var userPrompt = BuildVerificationUserPrompt(candidates);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(GetThreadVerificationSystemPrompt()),
            new UserChatMessage(userPrompt),
        };

        var options = BuildChatOptions(_reviewProfile.MaxOutputTokensVerification);

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Retry with Retry-After-aware backoff for rate limiting (HTTP 429)
        var totalRetryTime = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_rateLimitSignal != null)
                    await _rateLimitSignal.WaitIfCoolingDownAsync();

                response = await ThrottledCompleteChatAsync(messages, options);
                break;
            }
            catch (ClientResultException cex) when (cex.Status == 429 && attempt < RateLimitHelper.MaxRateLimitRetries)
            {
                var delay = RateLimitHelper.ComputeRetryDelay(cex);
                totalRetryTime += delay;

                if (totalRetryTime > RateLimitHelper.MaxTotalRetryDuration)
                {
                    _logger.LogError(
                        "Rate limit retries exhausted during thread verification: cumulative wait {Total:F0}s exceeds {Max}s cap",
                        totalRetryTime.TotalSeconds, RateLimitHelper.MaxTotalRetryDuration.TotalSeconds);
                    throw;
                }

                _logger.LogWarning(
                    "Rate limited (429) during thread verification, retry {Attempt}/{Max} after {Delay}s (cumulative: {Total:F0}s)",
                    attempt + 1, RateLimitHelper.MaxRateLimitRetries, delay.TotalSeconds, totalRetryTime.TotalSeconds);

                _rateLimitSignal?.SignalCooldown(delay);

                await Task.Delay(delay);
            }
            catch (ClientResultException cex) when (cex.Status == 429)
            {
                _logger.LogError(cex,
                    "Rate limit retries exhausted during thread verification after {Attempts} attempts. Status: {Status}",
                    attempt + 1, cex.Status);
                throw;
            }
            catch (ClientResultException cex)
            {
                _logger.LogError(cex, "AI error during thread verification. Status: {Status}", cex.Status);
                throw;
            }
        }
        sw.Stop();

        var content = response.Value.Content[0].Text;
        var usage = response.Value.Usage;
        _logger.LogInformation("Thread verification: {Len} chars | {Prompt}+{Completion}={Total} tokens | {Ms}ms",
            content.Length, usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount, sw.ElapsedMilliseconds);

        try
        {
            using var doc = JsonDocument.Parse(content);
            var results = new List<ThreadVerificationResult>();

            if (doc.RootElement.TryGetProperty("verifications", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    results.Add(new ThreadVerificationResult
                    {
                        ThreadId = item.GetProperty("threadId").GetInt32(),
                        IsFixed = item.GetProperty("isFixed").GetBoolean(),
                        Reasoning = item.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "",
                    });
                }
            }

            _logger.LogInformation("Verification results: {Fixed} fixed, {NotFixed} not fixed out of {Total}",
                results.Count(r => r.IsFixed), results.Count(r => !r.IsFixed), results.Count);

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to parse thread verification response. Treating all as NOT fixed (safe default). Raw: {Response}", content);
            // Safe default: don't resolve anything we can't verify
            return candidates.Select(c => new ThreadVerificationResult
            {
                ThreadId = c.ThreadId,
                IsFixed = false,
                Reasoning = "Verification parsing failed; treating as not fixed."
            }).ToList();
        }
    }

    private static string BuildVerificationUserPrompt(List<ThreadVerificationCandidate> candidates)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("# Thread Resolution Verification");
        sb.AppendLine();
        sb.AppendLine($"Verify whether the following {candidates.Count} prior review comment(s) have been addressed in the current code.");
        sb.AppendLine();

        foreach (var c in candidates)
        {
            sb.AppendLine($"---");
            sb.AppendLine($"## Thread ID: {c.ThreadId}");
            sb.AppendLine($"**File**: `{c.FilePath}` (Lines {c.StartLine}-{c.EndLine})");
            sb.AppendLine();
            sb.AppendLine("**Original Review Comment:**");
            sb.AppendLine($"> {c.OriginalComment}");
            sb.AppendLine();

            // Include human replies for additional context
            if (c.AuthorReplies.Count > 0)
            {
                sb.AppendLine("**Author/Reviewer Replies:**");
                foreach (var reply in c.AuthorReplies)
                {
                    var dateStr = reply.CreatedDateUtc != default
                        ? reply.CreatedDateUtc.ToString("yyyy-MM-dd HH:mm UTC")
                        : "unknown date";
                    sb.AppendLine($"> **{reply.Author}** ({dateStr}):");
                    sb.AppendLine($"> {reply.Content}");
                    sb.AppendLine();
                }
            }

            sb.AppendLine("**Current Code At That Location:**");
            sb.AppendLine("```");
            sb.AppendLine(c.CurrentCode);
            sb.AppendLine("```");
            sb.AppendLine();
        }

        return sb.ToString();
    }

    private const string ThreadVerificationSystemPrompt = """
        You are verifying whether prior code review comments have been addressed in the updated code.

        For each thread, you are given:
        - The ORIGINAL review comment describing an issue
        - Any REPLIES from the PR author or other reviewers (if available)
        - The CURRENT code at the location that was commented on

        Your job is to determine whether the SPECIFIC issue described in the comment was ACTUALLY FIXED
        in the current code. Be precise:

        - If the comment flagged a bug, check if the bug was fixed.
        - If the comment suggested adding error handling, check if error handling was added.
        - If the comment pointed out a security concern, check if it was addressed.
        - If the code was changed but the original issue persists, mark as NOT fixed.
        - If the code was changed for unrelated reasons (e.g., new features, refactoring), mark as NOT fixed.
        - If the comment's concern no longer applies because the code structure has fundamentally changed
          (e.g., the method was rewritten with a different approach that inherently avoids the issue),
          mark as FIXED.
        - If the PR author replied with a valid explanation for why the code is correct as-is
          (e.g., it's intentional behavior, a known pattern, or a false positive), consider their
          reasoning carefully. If their explanation is technically sound, mark as FIXED.
        - If the PR author acknowledged the issue and the code now reflects the fix, mark as FIXED.

        IMPORTANT: The fact that lines were modified does NOT by itself mean the issue was fixed.
        Only mark as fixed if you can verify the specific concern was addressed.

        When in doubt, mark as NOT fixed — it is better to leave a resolved thread active than to
        incorrectly dismiss a valid concern.

        Respond with valid JSON matching this schema:
        {
          "verifications": [
            {
              "threadId": <int>,
              "isFixed": <bool>,
              "reasoning": "<one brief sentence explaining why fixed or not>"
            }
          ]
        }

        Rules:
        1. Include ONE entry per thread ID provided.
        2. Only output valid JSON. No markdown, no explanation text outside the JSON.
        3. Be conservative — when the evidence is ambiguous, default to isFixed: false.
        """;

    // ═══════════════════════════════════════════════════════════════════════
    //  Pass 1 — PR-Level Summary (Cross-File Context)
    // ═══════════════════════════════════════════════════════════════════════

    [ExcludeFromCodeCoverage(Justification = "Calls _chatClient.CompleteChatAsync (Azure OpenAI SDK).")]
    public async Task<PrSummaryResult?> GeneratePrSummaryAsync(
        PullRequestInfo pullRequest, List<FileChange> fileChanges, List<WorkItemInfo>? workItems = null)
    {
        if (fileChanges.Count == 0)
            return null;

        var userPrompt = BuildPrSummaryUserPrompt(pullRequest, fileChanges, workItems);

        _logger.LogInformation("[Pass 1] Generating PR summary for PR #{PrId} ({FileCount} files, prompt ~{PromptLen} chars)",
            pullRequest.PullRequestId, fileChanges.Count, userPrompt.Length);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(GetPrSummarySystemPrompt()),
            new UserChatMessage(userPrompt),
        };

        var options = BuildChatOptions(_reviewProfile.MaxOutputTokensPrSummary);

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Retry with Retry-After-aware backoff for rate limiting (HTTP 429)
        var totalRetryTime = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_rateLimitSignal != null)
                    await _rateLimitSignal.WaitIfCoolingDownAsync();

                response = await ThrottledCompleteChatAsync(messages, options);
                break;
            }
            catch (ClientResultException cex) when (cex.Status == 429 && attempt < RateLimitHelper.MaxRateLimitRetries)
            {
                var delay = RateLimitHelper.ComputeRetryDelay(cex);
                totalRetryTime += delay;

                if (totalRetryTime > RateLimitHelper.MaxTotalRetryDuration)
                {
                    _logger.LogWarning(
                        "[Pass 1] Rate limit retries exhausted for PR #{PrId} summary — proceeding without cross-file context",
                        pullRequest.PullRequestId);
                    return null;
                }

                _logger.LogWarning(
                    "[Pass 1] Rate limited (429) during PR summary, retry {Attempt}/{Max} after {Delay}s",
                    attempt + 1, RateLimitHelper.MaxRateLimitRetries, delay.TotalSeconds);

                _rateLimitSignal?.SignalCooldown(delay);
                await Task.Delay(delay);
            }
            catch (ClientResultException cex) when (cex.Status == 429)
            {
                _logger.LogWarning(
                    "[Pass 1] Rate limit retries exhausted for PR #{PrId} summary after {Attempts} attempts — proceeding without cross-file context",
                    pullRequest.PullRequestId, attempt + 1);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Pass 1] PR summary generation failed for PR #{PrId} — per-file reviews will proceed without cross-file context",
                    pullRequest.PullRequestId);
                return null;
            }
        }
        sw.Stop();

        var content = response.Value.Content[0].Text;
        var usage = response.Value.Usage;

        _logger.LogInformation("[Pass 1] PR summary received in {Duration}ms (tokens: {Prompt}/{Completion}/{Total})",
            sw.ElapsedMilliseconds,
            usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount);

        try
        {
            var result = JsonSerializer.Deserialize<PrSummaryResult>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                _logger.LogWarning("[Pass 1] AI returned null/empty PR summary for PR #{PrId}", pullRequest.PullRequestId);
                return null;
            }

            // Attach metrics
            result.ModelName = _modelName;
            result.PromptTokens = usage?.InputTokenCount;
            result.CompletionTokens = usage?.OutputTokenCount;
            result.TotalTokens = usage?.TotalTokenCount;
            result.AiDurationMs = sw.ElapsedMilliseconds;

            return result;
        }
        catch (JsonException jex)
        {
            _logger.LogWarning(jex, "[Pass 1] Failed to parse PR summary JSON for PR #{PrId}", pullRequest.PullRequestId);
            return null;
        }
    }

    private static string BuildPrSummarySystemPrompt()
    {
        return """
        You are an expert code reviewer performing the FIRST PASS of a two-pass review process.
        Your job is to analyze the ENTIRE pull request at a high level and identify:

        1. **Intent**: What is this PR trying to accomplish? Summarize in one paragraph.
        2. **Architectural Impact**: How do these changes affect the system architecture?
        3. **Cross-File Relationships**: Which files depend on each other in this PR?
           Example: "ServiceA.cs adds a new method → ControllerB.cs calls it → ModelC.cs provides the DTO"
        4. **Risk Areas**: Which files or changes are highest risk and need careful review?
        5. **File Groupings**: Group related files into logical change sets.

        You are NOT producing inline comments or per-file verdicts — that happens in Pass 2.
        Focus entirely on the big picture: relationships, patterns, risks.

        Respond with valid JSON matching this schema:
        {
          "intent": "<one paragraph describing what the PR accomplishes>",
          "architecturalImpact": "<how changes affect architecture — 'None' if trivial>",
          "crossFileRelationships": [
            "<description of a cross-file dependency or relationship>"
          ],
          "riskAreas": [
            { "area": "<file or area name>", "reason": "<why this is risky>" }
          ],
          "fileGroupings": [
            { "groupName": "<logical group>", "files": ["<path>"], "description": "<why grouped>" }
          ]
        }

        Rules:
        1. Only output valid JSON. No markdown, no explanation text outside the JSON.
        2. Be concise — each description should be 1-2 sentences.
        3. If the PR is small/trivial, it's fine to have short/empty arrays.
        4. Focus on relationships the per-file reviewer would miss in isolation.
        """;
    }

    /// <summary>
    /// Build the user prompt for Pass 1 (PR-level summary).
    /// Includes file names, change types, and truncated diffs.
    /// </summary>
    internal string BuildPrSummaryUserPrompt(PullRequestInfo pr, List<FileChange> fileChanges, List<WorkItemInfo>? workItems = null)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# PR Summary Request (Pass 1)");
        sb.AppendLine();
        sb.AppendLine($"**PR #{pr.PullRequestId}**: {pr.Title}");
        sb.AppendLine($"**Author**: {pr.CreatedBy}");
        sb.AppendLine($"**Branches**: `{pr.SourceBranch}` → `{pr.TargetBranch}`");
        if (!string.IsNullOrWhiteSpace(pr.Description))
            sb.AppendLine($"**Description**: {pr.Description}");
        sb.AppendLine();

        // Include linked work items context if available
        AppendWorkItemContext(sb, workItems);

        sb.AppendLine($"## Files Changed ({fileChanges.Count} total)");
        sb.AppendLine();

        foreach (var file in fileChanges)
        {
            sb.AppendLine($"### `{file.FilePath}` ({file.ChangeType})");

            // For Pass 1, include a truncated view — enough for cross-file understanding
            // but not the full file (that's for Pass 2)
            var summaryMaxLines = Math.Min(_maxInputLinesPerFile, 200); // Cap at 200 lines per file for Pass 1

            if (!string.IsNullOrEmpty(file.UnifiedDiff))
            {
                var truncatedDiff = TruncateContentToLines(file.UnifiedDiff, summaryMaxLines);
                sb.AppendLine("```diff");
                sb.AppendLine(truncatedDiff);
                sb.AppendLine("```");
            }
            else if (file.ChangeType == "add" && !string.IsNullOrEmpty(file.ModifiedContent))
            {
                var truncated = TruncateContentToLines(file.ModifiedContent, summaryMaxLines);
                sb.AppendLine("```");
                sb.AppendLine(truncated);
                sb.AppendLine("```");
            }
            else if (file.ChangeType == "delete")
            {
                sb.AppendLine("*(file deleted)*");
            }
            else
            {
                // Fallback for rename/edit change types when no unified diff is available
                if (!string.IsNullOrEmpty(file.ModifiedContent))
                {
                    var truncated = TruncateContentToLines(file.ModifiedContent, summaryMaxLines);
                    sb.AppendLine("```");
                    sb.AppendLine(truncated);
                    sb.AppendLine("```");
                }
                else if (!string.IsNullOrEmpty(file.OriginalContent))
                {
                    var truncated = TruncateContentToLines(file.OriginalContent, summaryMaxLines);
                    sb.AppendLine("```");
                    sb.AppendLine(truncated);
                    sb.AppendLine("```");
                }
                else
                {
                    sb.AppendLine("*(no diff or file content available for this change)*");
                }
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Truncate content to a specific number of lines.
    /// Unlike <see cref="TruncateContent"/> (which uses the instance _maxInputLinesPerFile),
    /// this takes an explicit limit — used for Pass 1 where we want shorter snippets.
    /// </summary>
    internal static string TruncateContentToLines(string content, int maxLines)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines)
            return content;

        var truncated = string.Join('\n', lines.Take(maxLines));
        return truncated + $"\n\n... [truncated: {lines.Length - maxLines} more lines] ...";
    }

    private static string BuildSystemPrompt(string? customInstructions)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(IdentityPreamble);

        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(customInstructions);
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.Append(ResponseFormatRules);

        return sb.ToString();
    }

    private static string BuildSingleFileSystemPrompt(string? customInstructions)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(IdentityPreamble);

        if (!string.IsNullOrWhiteSpace(customInstructions))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(customInstructions);
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.Append(SingleFileResponseRules);

        return sb.ToString();
    }

    private static string? LoadCustomInstructions(string? instructionsPath)
    {
        if (string.IsNullOrWhiteSpace(instructionsPath))
            return null;

        var resolvedPath = Path.IsPathRooted(instructionsPath)
            ? instructionsPath
            : Path.Combine(AppContext.BaseDirectory, instructionsPath);

        if (!File.Exists(resolvedPath))
            return null;

        try
        {
            var json = File.ReadAllText(resolvedPath);
            var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("customInstructions", out var el))
            {
                var text = el.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch (Exception ex)
        {
            // Custom instructions are optional — log a warning but continue
            System.Diagnostics.Trace.TraceWarning(
                $"Could not load custom instructions from '{resolvedPath}': {ex.Message}");
        }

        return null;
    }

    // ── Part 1: Identity (hardcoded) ────────────────────────────────────
    private const string IdentityPreamble = """
        You are an expert code reviewer for an enterprise development team. You perform thorough,
        constructive code reviews focusing on correctness, security, performance, maintainability,
        and best practices.

        REVIEW FOCUS — PRIORITIZED:
        Primary (always flag): bugs, security vulnerabilities, logic errors, missing error handling.
        Secondary (flag for significant deviations): performance issues, simplification opportunities,
        reusability improvements, best-practice violations.
        Tertiary (flag only for major divergence from codebase standards): maintainability, code consistency,
        naming conventions. Matters of style preference that make minimal practical difference should be excluded.

        QUALITY BAR: You are a senior engineer who values signal over noise. Only create inline comments
        for issues that a senior developer would genuinely care about. Avoid obvious, pedantic, or
        stylistic nit-picks. If a file is clean and well-written, say so with a short approval — do NOT
        manufacture comments just to have something to say. Aim for 0-3 inline comments per file.
        Positive feedback for thoughtful solutions or high-impact additions may be included, but avoid
        over-praising or becoming sycophantic.
        """;

    // ── Part 2: Response format and rules (hardcoded) ───────────────────
    private const string ResponseFormatRules = """
            You MUST respond with valid JSON matching this exact schema:

            {
              "summary": {
                "filesChanged": <int>,
                "editsCount": <int>,
                "addsCount": <int>,
                "deletesCount": <int>,
                "commitsCount": <int>,
                "description": "<one-sentence summary of the PR's overall intent>",
                "verdict": "APPROVED|APPROVED WITH SUGGESTIONS|NEEDS WORK|REJECTED",
                "verdictJustification": "<justification — see rule 15 for length requirements>"
              },
              "fileReviews": [
                {
                  "filePath": "<repo-relative path>",
                  "verdict": "NEEDS WORK|CONCERN|OBSERVATION",
                  "reviewText": "<concise assessment — what's wrong and what to fix>"
                }
              ],
              "inlineComments": [
                {
                  "filePath": "<repo-relative path>",
                  "startLine": <int>,
                  "endLine": <int>,
                  "codeSnippet": "<1-3 lines of actual code being commented on, copied verbatim from the file>",
                  "leadIn": "LGTM|Good catch|Important|Concern|Suggestion|Bug|Security|Performance",
                  "comment": "<detailed explanation>",
                  "status": "closed|active"
                }
              ],
              "acceptanceCriteriaAnalysis": {
                "summary": "<1-2 sentence overview of AC coverage>",
                "items": [
                  {
                    "criterion": "<abbreviated AC text>",
                    "status": "Addressed|Partially Addressed|Not Addressed|Cannot Determine",
                    "evidence": "<brief explanation with file paths where relevant>"
                  }
                ]
              },
              "recommendedVote": <10|5|-5|-10>
            }

            Rules for the review:
            1. Be constructive and specific. Reference actual code when pointing out issues.
            2. Use "closed" status for positive/informational inline comments, "active" for items needing author attention.
            3. Use the bold lead-in to categorize each inline comment:
               - LGTM: Code is correct and well-written
               - Good catch: Acknowledges a pre-existing issue being fixed
               - Important: Critical defensive change worth noting
               - Concern: Something that should be addressed before merge
               - Suggestion: Non-blocking improvement idea
               - Bug: Definite bug that must be fixed
               - Security: Security vulnerability or concern
               - Performance: Performance issue or optimization opportunity
            4. recommendedVote values:
               - 10 = Approved (no errors or concerns)
               - 5 = Approved with suggestions (only info/observations, no blockers)
               - -5 = Waiting for author (warnings or concerns to address)
               - -10 = Rejected (critical errors or security issues)
            5. If there are no significant issues, return verdict "APPROVED" with vote 10.
            6. SKIP CLEAN FILES: If a file has no inline comments and no genuine blocking issues, do NOT
               include it in fileReviews. Only include files that have inline comments OR critical concerns
               (security, bugs, missing error handling). Vague statements like "could benefit from improved
               error handling" or "opportunities to improve maintainability" are NOT worth a fileReview entry.
               An empty fileReviews array is perfectly fine — most clean PRs should have very few entries.
               If you must include a fileReview for a clean file, use EXACTLY: verdict "APPROVED", reviewText "No issues found."
            7. Be thorough but concise. Don't pad the review with unnecessary comments.
               Aim for 0-3 inline comments per file. Zero is perfectly fine for clean files.
            8. CODE SNIPPET — CRITICAL:
               Every inline comment MUST include a "codeSnippet" field containing 1-3 lines of code copied VERBATIM
               from the file content (without the line number prefix). This snippet identifies the exact code location.
               Example: if the file contains "  45 | public void Process()", your codeSnippet should be "public void Process()".
               The snippet must appear in the file — do NOT fabricate or paraphrase code.
            9. LINE NUMBERS:
               Use the line number prefix shown at the start of each line (e.g., "  45 | code..." means line 45).
               Set startLine and endLine to bracket the code you are commenting on.
               If you are unsure of exact line numbers, provide your best estimate — the system will verify using codeSnippet.
            10. INLINE COMMENTS MUST BE SPECIFIC:
               Each inline comment must reference a specific code construct (method, variable, statement).
               Generic file-level observations do NOT belong in inlineComments.
            11. The filePath in inlineComments MUST exactly match the file path shown in the "## File:" header. Copy it verbatim.
            12. Only output valid JSON. No markdown, no explanation text outside the JSON.
            13. SECURITY COMMENTS — CONTEXT MATTERS:
                - Empty/placeholder values (e.g., "") in config files are NOT security issues — they are templates.
                - Only flag ACTUAL hardcoded secrets (real tokens, real keys), not empty fields.
                - Flag the real secret in ONE place, don't repeat the same issue across multiple config files.
            14. COMMENT QUALITY: Only comment on things a senior engineer would care about. No pedantic nit-picks.
            15. REJECTION / NEEDS WORK — REQUIRE SPECIFIC JUSTIFICATION:
               When the verdict is REJECTED or NEEDS WORK, the verdictJustification MUST be a detailed
               paragraph (3-6 sentences) that:
               a) Names EVERY file that contributed to the rejection using its full repo-relative path
                  (e.g., "/src/services/Foo.cs") — never say "the file" or "a file" without the path.
               b) Describes the SPECIFIC problem in each named file.
               c) Explains WHY each issue is a blocker (e.g., security risk, data loss, logic error).
               d) Suggests concrete remediation steps.
               For APPROVED or APPROVED WITH SUGGESTIONS, a single sentence is fine.
               Additionally, every file that contributed to a REJECTED or NEEDS WORK verdict MUST appear
               in the fileReviews array with a CONCERN or NEEDS WORK verdict and a clear reviewText
               explaining the specific problem. Do NOT reject a PR without pointing to specific files and issues.
            16. NON-CODE / REFERENCE FILES:
               Files that contain only a commit hash, an empty object, whitespace, or no meaningful code
               are typically infrastructure artifacts (e.g., git submodule pointers, placeholder files,
               tracking references). These are NOT code quality issues.
               - Do NOT include such files in your verdict reasoning or verdictJustification.
               - Do NOT use them as grounds for REJECTED or NEEDS WORK verdicts.
               - If you encounter such a file, you may skip it from fileReviews entirely, or at most
                 include it with verdict "APPROVED" and a brief informational reviewText.
               - Focus your review exclusively on files that contain actual reviewable code or configuration.
            17. ACCEPTANCE CRITERIA / DEFINITION OF DONE ANALYSIS:
               When linked work items with acceptance criteria are provided in the user prompt,
               you MUST populate the "acceptanceCriteriaAnalysis" object. For each AC item:
               a) State whether the PR code changes address it ("Addressed"), partially address
                  it ("Partially Addressed"), don't address it ("Not Addressed"), or if you
                  can't tell from the code alone ("Cannot Determine").
               b) Cite specific files/code as evidence.
               c) "Cannot Determine" is for criteria that require runtime verification, external
                  system checks, or manual testing (e.g., "event visible in dashboard").
               d) If NO linked work items or AC are provided, omit the acceptanceCriteriaAnalysis
                  field entirely — do NOT fabricate criteria.
               e) AC analysis does NOT change the code quality verdict. A PR can be "APPROVED" for
                  code quality even if some AC items are "Not Addressed" — those are separate concerns.
                  However, note unaddressed AC items in the verdictJustification as informational context.
               f) If work item discussion comments mention AC changes or scope decisions, factor those in.
            18. EXAMPLE of a correct inline comment (given input file lines "  45 | public void Process() {" through "  70 | }"):
                {
                  "filePath": "/src/Services/MyService.cs",
                  "startLine": 45,
                  "endLine": 70,
                  "codeSnippet": "public void Process() {",
                  "leadIn": "Concern",
                  "comment": "This method has cyclomatic complexity >10 due to nested if/switch blocks. Consider extracting the validation logic into a separate method.",
                  "status": "active"
                }
                Notice: codeSnippet is the actual code from line 45, copied without the line number prefix.
            """;

    // ── Part 3: Single-file response format (hardcoded) ────────────────
    private const string SingleFileResponseRules = """
            You are reviewing ONE file in isolation. Give it your FULL attention.

            You MUST respond with valid JSON matching this exact schema:

            {
              "summary": {
                "filesChanged": 1,
                "editsCount": <0 or 1>,
                "addsCount": <0 or 1>,
                "deletesCount": <0 or 1>,
                "commitsCount": 1,
                "description": "<one-sentence description of this file's changes>",
                "verdict": "APPROVED|APPROVED WITH SUGGESTIONS|NEEDS WORK|REJECTED",
                "verdictJustification": "<justification — for REJECTED/NEEDS WORK: 3-6 sentences listing specific blocking issues, why they are blockers, and remediation steps. For APPROVED: one sentence is fine. ALWAYS include the full repo-relative file path for every file mentioned.>"
              },
              "fileReviews": [
                {
                  "filePath": "<repo-relative path — MUST match the File header exactly>",
                  "verdict": "NEEDS WORK|CONCERN",
                  "reviewText": "<concise assessment — what's wrong and what to fix>"
                }
              ],
              "inlineComments": [
                {
                  "filePath": "<repo-relative path — MUST match the File header exactly>",
                  "startLine": <int — READ the line number prefix, e.g. "  45 | code" means 45>,
                  "endLine": <int>,
                  "codeSnippet": "<1-3 lines copied VERBATIM from the file, WITHOUT line number prefix>",
                  "leadIn": "LGTM|Good catch|Important|Concern|Suggestion|Bug|Security|Performance",
                  "comment": "<specific, actionable explanation>",
                  "status": "closed|active"
                }
              ],
              "acceptanceCriteriaAnalysis": {
                "summary": "<1-2 sentence overview of AC coverage — only for this file's relevance>",
                "items": [
                  {
                    "criterion": "<abbreviated AC text>",
                    "status": "Addressed|Partially Addressed|Not Addressed|Cannot Determine",
                    "evidence": "<brief explanation referencing code in this file>"
                  }
                ]
              },
              "recommendedVote": <10|5|-5|-10>
            }

            CRITICAL RULES FOR THIS SINGLE-FILE REVIEW:

            1. LINE NUMBERS — THIS IS THE MOST IMPORTANT RULE:
               Every line in the file is prefixed with its number like "  45 | code here".
               You MUST read these prefixes and use them for startLine/endLine.
               Do NOT default to line 1. Do NOT guess. READ the actual line numbers from the content.
               If commenting on "  87 | var x = 10;" → startLine=87, endLine=87.
               If commenting on a range from "  20 | public void Foo()" to "  35 | }" → startLine=20, endLine=35.

            2. CODE SNIPPET — MANDATORY:
               Every inline comment MUST include "codeSnippet" with 1-3 lines copied VERBATIM from the file.
               Copy the code WITHOUT the line number prefix.
               Example: line " 102 | public async Task Run()" → codeSnippet: "public async Task Run()"
               The snippet MUST actually appear in the file content. Never fabricate code.

            3. FILE PATH:
               The filePath in EVERY inlineComment and fileReview MUST exactly match the path shown in "## File:" header. Copy it verbatim.

            4. COMMENT METADATA:
               - Be constructive, specific, and reference actual code constructs.
               - Status: use "closed" for positive/informational comments, "active" for items needing author attention.
               - Lead-in categories: LGTM, Good catch, Important, Concern, Suggestion, Bug, Security, Performance.
               - Vote values: 10=Approved, 5=Approved with suggestions, -5=Waiting for author, -10=Rejected.

            5. DIFF-FOCUSED REVIEW (for edited files):
               When a unified diff is provided, it shows exactly what was added (+) and removed (-).
               ONLY review and comment on the CHANGED lines and their immediate context (within ~5 lines).
               The full file is provided so you can understand types, method signatures, and surrounding logic,
               but you MUST NOT create inline comments on code that was NOT changed in this PR.
               Pre-existing issues (complexity, style, naming) in unchanged code are NOT the current author's
               responsibility — do not flag them. Only flag pre-existing issues if a NEW change directly
               interacts with them (e.g., the author added a call to an already-buggy method).
               EXCEPTION — SUBSTANTIAL METHOD REWRITES: If more than ~40% of a method/function body was
               changed in this PR, the author effectively owns that method. In that case, you MAY flag
               method-level concerns like high cyclomatic complexity, excessive length, or poor structure
               for the ENTIRE method, even if some lines within it are unchanged. Set startLine/endLine
               to span the full method signature through closing brace.
               Your startLine/endLine MUST reference line numbers from the numbered file content, NOT from the diff.
               An inline comment MUST point to a specific code location with correct line numbers.
               IMPORTANT: Comments on lines outside the diff will be automatically filtered out by the system,
               unless the comment spans a region where >40% of the lines were changed.

            6. OUTPUT FORMAT:
               Only output valid JSON. No markdown fences, no explanation text outside the JSON.

            7. COMMENT QUALITY — CRITICAL:
                - Only create inline comments for issues that genuinely matter: bugs, security risks with real
                  impact, performance problems, logic errors, missing error handling, or design concerns.
                - Do NOT comment on: trivial style preferences, obvious boilerplate, test helper internals,
                  well-known patterns, or things the author clearly did intentionally.
                - Generic file-level observations do NOT belong in inlineComments — skip them entirely.
                - Aim for 0-3 inline comments per file. Zero is fine for clean files.
                - If a file looks good, return verdict "APPROVED" with reviewText "No issues found." (exact text).
                  This sentinel value lets the system filter clean entries automatically.
                - A fileReview entry without a corresponding inline comment is almost always noise — skip it.

            8. SECURITY COMMENTS — CONTEXT MATTERS:
                - Empty/placeholder values (e.g., "", "your-key-here") in config files are NOT security issues.
                  They are templates. Only flag ACTUAL hardcoded secrets (real tokens, real keys, real passwords).
                - Development-only config files (appsettings.Development.json) commonly contain real values for
                  local dev. Flag these BUT note they should be in .gitignore or user-secrets, don't treat them
                  the same as production config.
                - Properties that HOLD secrets (like a PersonalAccessToken property declaration) are fine — the
                  concern is only when the VALUE is a real secret in the file.
                - Do NOT flag the same security issue on multiple files. Flag it once on the file that has the
                  actual secret, not on every file that has an empty placeholder for it.

            9. CONFIGURATION / NON-CODE FILES:
                - For .json config, .yml pipelines, .csproj, Dockerfile, .gitignore, launchSettings.json:
                  Review for actual problems only (wrong values, missing required fields, security issues).
                  Do NOT suggest "add comments" or "use environment variables" for standard config patterns.
                - For test files: focus on test correctness and coverage gaps, not on test helper implementation details.

            10. VERIFY BEFORE FLAGGING MISSING REFERENCES:
                You have the COMPLETE modified file content (every line, numbered). Before flagging any method,
                class, variable, or symbol as "not defined", "missing", or "not implemented", SEARCH the entire
                file content to confirm it truly does not exist. Methods may be defined hundreds of lines below
                the call site. If you find the symbol elsewhere in the file, do NOT flag it.

            11. ACCEPTANCE CRITERIA ANALYSIS (SINGLE FILE):
                When linked work items with acceptance criteria are provided, populate
                "acceptanceCriteriaAnalysis" ONLY with AC items relevant to THIS specific file.
                Skip AC items that don't relate to this file's purpose. If NO AC items are relevant
                to this file, omit the field entirely. If NO work items were provided, omit it.
            """;

    private string BuildUserPrompt(PullRequestInfo pr, List<FileChange> fileChanges, List<WorkItemInfo>? workItems = null)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Pull Request for Review");
        sb.AppendLine();
        sb.AppendLine($"**PR #{pr.PullRequestId}**: {pr.Title}");
        sb.AppendLine($"**Author**: {pr.CreatedBy}");
        sb.AppendLine($"**Branches**: `{pr.SourceBranch}` → `{pr.TargetBranch}`");
        if (!string.IsNullOrWhiteSpace(pr.Description))
        {
            sb.AppendLine($"**Description**: {pr.Description}");
        }
        sb.AppendLine();

        // Include linked work item context (AC/DoD)
        AppendWorkItemContext(sb, workItems);

        sb.AppendLine($"**Files Changed**: {fileChanges.Count}");
        sb.AppendLine();

        int edits = 0, adds = 0, deletes = 0;
        foreach (var fc in fileChanges)
        {
            switch (fc.ChangeType)
            {
                case "edit": edits++; break;
                case "add": adds++; break;
                case "delete": deletes++; break;
            }
        }

        sb.AppendLine($"Change breakdown: {edits} edits, {adds} adds, {deletes} deletes");
        sb.AppendLine();

        foreach (var file in fileChanges)
        {
            sb.AppendLine($"---");
            sb.AppendLine($"## File: `{file.FilePath}` (Change: {file.ChangeType})");
            sb.AppendLine();

            if (file.ChangeType == "delete")
            {
                sb.AppendLine("**This file was deleted.**");
                if (!string.IsNullOrEmpty(file.OriginalContent))
                {
                    sb.AppendLine("Previous content:");
                    sb.AppendLine("```");
                    sb.AppendLine(TruncateContent(file.OriginalContent));
                    sb.AppendLine("```");
                }
            }
            else if (file.ChangeType == "add")
            {
                sb.AppendLine("**This is a new file.**");
                if (!string.IsNullOrEmpty(file.ModifiedContent))
                {
                    sb.AppendLine("Content (each line is prefixed with its LINE NUMBER — use these in inlineComments startLine/endLine):");
                    sb.AppendLine("```");
                    sb.AppendLine(AddLineNumbers(TruncateContent(file.ModifiedContent)));
                    sb.AppendLine("```");
                }
            }
            else
            {
                // Edit: show both versions
                if (!string.IsNullOrEmpty(file.OriginalContent))
                {
                    sb.AppendLine("**Original (target branch):**");
                    sb.AppendLine("```");
                    sb.AppendLine(TruncateContent(file.OriginalContent));
                    sb.AppendLine("```");
                }
                sb.AppendLine();
                if (!string.IsNullOrEmpty(file.ModifiedContent))
                {
                    sb.AppendLine("**Modified (source/PR branch) — each line is prefixed with its LINE NUMBER for inline comments:**");
                    sb.AppendLine("```");
                    sb.AppendLine(AddLineNumbers(TruncateContent(file.ModifiedContent)));
                    sb.AppendLine("```");
                }
            }
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// Build a user prompt for reviewing a single file in isolation.
    /// Includes PR context for relevance but only contains one file's content.
    /// </summary>
    private string BuildSingleFileUserPrompt(PullRequestInfo pr, FileChange file, int totalFilesInPr, List<WorkItemInfo>? workItems = null)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Single-File Review");
        sb.AppendLine();
        sb.AppendLine($"**PR #{pr.PullRequestId}**: {pr.Title}");
        sb.AppendLine($"**Author**: {pr.CreatedBy}");
        sb.AppendLine($"**Branches**: `{pr.SourceBranch}` → `{pr.TargetBranch}`");
        if (!string.IsNullOrWhiteSpace(pr.Description))
            sb.AppendLine($"**Description**: {pr.Description}");
        sb.AppendLine();

        // Include linked work item context (AC/DoD)
        AppendWorkItemContext(sb, workItems);

        // Include cross-file context from Pass 1 (if available)
        if (pr.CrossFileSummary != null)
        {
            AppendCrossFileContext(sb, pr.CrossFileSummary, file.FilePath);
        }

        sb.AppendLine($"This PR has {totalFilesInPr} changed files total. You are reviewing **one file** below.");
        sb.AppendLine();

        sb.AppendLine("---");
        sb.AppendLine($"## File: `{file.FilePath}` (Change: {file.ChangeType})");
        sb.AppendLine();

        if (file.ChangeType == "delete")
        {
            sb.AppendLine("**This file was deleted.**");
            if (!string.IsNullOrEmpty(file.OriginalContent))
            {
                sb.AppendLine("Previous content:");
                sb.AppendLine("```");
                sb.AppendLine(TruncateContent(file.OriginalContent));
                sb.AppendLine("```");
            }
        }
        else if (file.ChangeType == "add")
        {
            sb.AppendLine("**This is a new file.**");
            if (!string.IsNullOrEmpty(file.ModifiedContent))
            {
                sb.AppendLine();
                sb.AppendLine("Content — EVERY line starts with its LINE NUMBER (e.g., \"  12 | code\"). Use these numbers for startLine/endLine:");
                sb.AppendLine("```");
                sb.AppendLine(AddLineNumbers(TruncateContent(file.ModifiedContent)));
                sb.AppendLine("```");
            }
        }
        else
        {
            // Edit: show unified diff (what changed) + full modified file (for line numbers)
            if (!string.IsNullOrEmpty(file.UnifiedDiff))
            {
                sb.AppendLine("**What changed (unified diff) — lines prefixed with `-` were removed, `+` were added, ` ` are context:**");
                sb.AppendLine("```diff");
                sb.AppendLine(file.UnifiedDiff);
                sb.AppendLine("```");
            }
            else if (!string.IsNullOrEmpty(file.OriginalContent))
            {
                // Fallback if diff wasn't computed
                sb.AppendLine("**Original (target branch):**");
                sb.AppendLine("```");
                sb.AppendLine(TruncateContent(file.OriginalContent));
                sb.AppendLine("```");
            }
            sb.AppendLine();
            if (!string.IsNullOrEmpty(file.ModifiedContent))
            {
                sb.AppendLine("**Current file (modified) — EVERY line starts with its LINE NUMBER (e.g., \"  12 | code\"). Use these for startLine/endLine:**");
                sb.AppendLine("```");
                sb.AppendLine(AddLineNumbers(TruncateContent(file.ModifiedContent)));
                sb.AppendLine("```");
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Prepend 1-based line numbers to each line of the content.
    /// This gives the AI accurate line references for inline comments.
    /// </summary>
    private static string AddLineNumbers(string content)
    {
        var lines = content.Split('\n');
        var width = lines.Length.ToString().Length;
        var numbered = new System.Text.StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            numbered.Append((i + 1).ToString().PadLeft(width));
            numbered.Append(" | ");
            numbered.AppendLine(lines[i].TrimEnd('\r'));
        }
        return numbered.ToString().TrimEnd();
    }

    /// <summary>
    /// Truncate very large files to avoid exceeding token limits.
    /// Uses the configured <see cref="_maxInputLinesPerFile"/> threshold.
    /// </summary>
    private string TruncateContent(string content)
    {
        var lines = content.Split('\n');
        if (lines.Length <= _maxInputLinesPerFile)
            return content;

        var truncated = string.Join('\n', lines.Take(_maxInputLinesPerFile));
        return truncated + $"\n\n... [truncated: {lines.Length - _maxInputLinesPerFile} more lines] ...";
    }

    /// <summary>
    /// Extract inline comment file/line info from raw AI JSON for debug logging.
    /// </summary>
    private static string ExtractInlineExcerpt(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("inlineComments", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                var items = new List<string>();
                foreach (var item in arr.EnumerateArray())
                {
                    var fp = item.TryGetProperty("filePath", out var fpv) ? fpv.GetString() ?? "?" : "?";
                    var sl = item.TryGetProperty("startLine", out var slv) ? slv.GetInt32() : -1;
                    var el = item.TryGetProperty("endLine", out var elv) ? elv.GetInt32() : -1;
                    var fname = fp.Contains('/') ? fp[(fp.LastIndexOf('/') + 1)..] : fp;
                    items.Add($"{fname}:{sl}-{el}");
                }
                return string.Join(", ", items);
            }
        }
        catch { /* ignore parse errors */ }
        return "(no inline data)";
    }

    /// <summary>
    /// Append linked work item context (AC/DoD, description, comments) to the user prompt.
    /// </summary>
    private static void AppendWorkItemContext(System.Text.StringBuilder sb, List<WorkItemInfo>? workItems)
    {
        if (workItems == null || workItems.Count == 0)
            return;

        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Linked Work Items");
        sb.AppendLine();

        foreach (var wi in workItems)
        {
            sb.AppendLine($"### {wi.WorkItemType} #{wi.Id}: {wi.Title}");
            sb.AppendLine($"**State**: {wi.State}");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(wi.Description))
            {
                sb.AppendLine("**Description:**");
                sb.AppendLine(wi.Description);
                sb.AppendLine();
            }

            if (!string.IsNullOrWhiteSpace(wi.AcceptanceCriteria))
            {
                sb.AppendLine("**Acceptance Criteria / Definition of Done:**");
                sb.AppendLine(wi.AcceptanceCriteria);
                sb.AppendLine();
            }

            if (wi.Comments.Count > 0)
            {
                sb.AppendLine("**Work Item Discussion** (may contain AC modifications or scope decisions):");
                foreach (var comment in wi.Comments)
                {
                    sb.AppendLine($"- [{comment.CreatedDate:yyyy-MM-dd}] {comment.Author}: {comment.Text}");
                }
                sb.AppendLine();
            }
        }

        sb.AppendLine("---");
        sb.AppendLine();
    }

    /// <summary>
    /// Append Pass 1 cross-file context to the per-file (Pass 2) user prompt.
    /// Highlights relationships and risks relevant to the specific file being reviewed.
    /// </summary>
    private static void AppendCrossFileContext(System.Text.StringBuilder sb, PrSummaryResult summary, string currentFilePath)
    {
        sb.AppendLine("---");
        sb.AppendLine();
        sb.AppendLine("## Cross-File Context (from PR-level analysis)");
        sb.AppendLine();

        if (!string.IsNullOrWhiteSpace(summary.Intent))
        {
            sb.AppendLine($"**PR Intent**: {summary.Intent}");
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(summary.ArchitecturalImpact) &&
            !summary.ArchitecturalImpact.Equals("None", StringComparison.OrdinalIgnoreCase))
        {
            sb.AppendLine($"**Architectural Impact**: {summary.ArchitecturalImpact}");
            sb.AppendLine();
        }

        // Show cross-file relationships
        if (summary.CrossFileRelationships.Count > 0)
        {
            sb.AppendLine("**Cross-File Relationships** (watch for dependencies with the file you're reviewing):");
            foreach (var rel in summary.CrossFileRelationships)
            {
                sb.AppendLine($"- {rel}");
            }
            sb.AppendLine();
        }

        // Highlight risks specific to this file
        var fileRisks = summary.RiskAreas
            .Where(r => r.Area.Contains(Path.GetFileName(currentFilePath), StringComparison.OrdinalIgnoreCase) ||
                         currentFilePath.Contains(r.Area, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (fileRisks.Count > 0)
        {
            sb.AppendLine("**⚠ Risk flags for THIS file**:");
            foreach (var risk in fileRisks)
            {
                sb.AppendLine($"- {risk.Reason}");
            }
            sb.AppendLine();
        }

        // Show which group this file belongs to
        var fileGroup = summary.FileGroupings
            .FirstOrDefault(g => g.Files.Any(f =>
                f.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase) ||
                currentFilePath.EndsWith(f, StringComparison.OrdinalIgnoreCase)));

        if (fileGroup != null)
        {
            sb.AppendLine($"**File Group**: {fileGroup.GroupName} — {fileGroup.Description}");
            var otherFiles = fileGroup.Files
                .Where(f => !f.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase) &&
                            !currentFilePath.EndsWith(f, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (otherFiles.Count > 0)
            {
                sb.AppendLine($"  Related files: {string.Join(", ", otherFiles.Select(f => $"`{f}`"))}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("---");
        sb.AppendLine();
    }

    // ── Pass 3: Deep holistic re-evaluation ─────────────────────────────────

    [ExcludeFromCodeCoverage(Justification = "Calls _chatClient.CompleteChatAsync (Azure OpenAI SDK).")]
    public async Task<DeepAnalysisResult?> GenerateDeepAnalysisAsync(
        PullRequestInfo pullRequest,
        PrSummaryResult? prSummary,
        CodeReviewResult reviewResult,
        List<FileChange> fileChanges)
    {
        var userPrompt = BuildDeepAnalysisUserPrompt(pullRequest, prSummary, reviewResult, fileChanges);

        _logger.LogInformation("[Pass 3] Generating deep analysis for PR #{PrId} ({FileCount} files, prompt ~{PromptLen} chars)",
            pullRequest.PullRequestId, fileChanges.Count, userPrompt.Length);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(GetDeepAnalysisSystemPrompt()),
            new UserChatMessage(userPrompt),
        };

        var options = BuildChatOptions(_reviewProfile.MaxOutputTokensDeepAnalysis);

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Retry with Retry-After-aware backoff for rate limiting (HTTP 429)
        var totalRetryTime = TimeSpan.Zero;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                if (_rateLimitSignal != null)
                    await _rateLimitSignal.WaitIfCoolingDownAsync();

                response = await ThrottledCompleteChatAsync(messages, options);
                break;
            }
            catch (ClientResultException cex) when (cex.Status == 429 && attempt < RateLimitHelper.MaxRateLimitRetries)
            {
                var delay = RateLimitHelper.ComputeRetryDelay(cex);
                totalRetryTime += delay;

                if (totalRetryTime > RateLimitHelper.MaxTotalRetryDuration)
                {
                    _logger.LogWarning(
                        "[Pass 3] Rate limit retries exhausted for PR #{PrId} deep analysis",
                        pullRequest.PullRequestId);
                    return null;
                }

                _logger.LogWarning(
                    "[Pass 3] Rate limited (429) during deep analysis, retry {Attempt}/{Max} after {Delay}s",
                    attempt + 1, RateLimitHelper.MaxRateLimitRetries, delay.TotalSeconds);

                _rateLimitSignal?.SignalCooldown(delay);
                await Task.Delay(delay);
            }
            catch (ClientResultException cex) when (cex.Status == 429)
            {
                _logger.LogWarning(
                    "[Pass 3] Rate limit retries exhausted for PR #{PrId} deep analysis after {Attempts} attempts",
                    pullRequest.PullRequestId, attempt + 1);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Pass 3] Deep analysis failed for PR #{PrId}", pullRequest.PullRequestId);
                return null;
            }
        }
        sw.Stop();

        var content = response.Value.Content[0].Text;
        var usage = response.Value.Usage;

        _logger.LogInformation("[Pass 3] Deep analysis received in {Duration}ms (tokens: {Prompt}/{Completion}/{Total})",
            sw.ElapsedMilliseconds,
            usage?.InputTokenCount, usage?.OutputTokenCount, usage?.TotalTokenCount);

        try
        {
            var result = JsonSerializer.Deserialize<DeepAnalysisResult>(content, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (result == null)
            {
                _logger.LogWarning("[Pass 3] AI returned null/empty deep analysis for PR #{PrId}", pullRequest.PullRequestId);
                return null;
            }

            // Attach metrics
            result.ModelName = _modelName;
            result.PromptTokens = usage?.InputTokenCount;
            result.CompletionTokens = usage?.OutputTokenCount;
            result.TotalTokens = usage?.TotalTokenCount;
            result.AiDurationMs = sw.ElapsedMilliseconds;

            return result;
        }
        catch (JsonException jex)
        {
            _logger.LogWarning(jex, "[Pass 3] Failed to parse deep analysis JSON for PR #{PrId}", pullRequest.PullRequestId);
            return null;
        }
    }

    private static string GetDeepAnalysisSystemPrompt()
    {
        return """
        You are an expert code reviewer performing the THIRD PASS (deep holistic analysis) of a three-pass review process.

        Pass 1 already generated a PR-level summary with cross-file relationships and risk areas.
        Pass 2 already reviewed each file individually and produced per-file verdicts and inline comments.

        Your job is to RE-EVALUATE the entire review holistically:

        1. **Executive Summary**: A concise paragraph summarizing the overall PR quality and readiness.
        2. **Cross-File Issues**: Issues that are ONLY visible when considering multiple files together.
           These are issues that per-file reviews could NOT have caught individually.
           Examples: interface contract mismatches, missing error propagation across layers,
           inconsistent naming conventions across files, missing integration between new components.
        3. **Verdict Consistency**: Are the per-file verdicts consistent with each other and with the
           overall verdict? If not, recommend a verdict override with justification.
        4. **Overall Risk Level**: "Low", "Medium", "High", or "Critical" based on all evidence.
        5. **Recommendations**: Key actionable recommendations that span multiple files.

        IMPORTANT: Only flag cross-file issues that per-file reviews genuinely missed.
        Do NOT repeat issues already caught in per-file reviews.

        Respond with valid JSON matching this schema:
        {
          "executiveSummary": "<concise overall assessment paragraph>",
          "crossFileIssues": [
            {
              "files": ["<file1>", "<file2>"],
              "severity": "Error|Warning|Info",
              "description": "<what the cross-file issue is>"
            }
          ],
          "verdictConsistency": {
            "isConsistent": true|false,
            "explanation": "<why verdicts are or aren't consistent>",
            "recommendedVerdict": "<override verdict or null>",
            "recommendedVote": <override vote int or null>
          },
          "overallRiskLevel": "Low|Medium|High|Critical",
          "recommendations": [
            "<actionable recommendation spanning multiple files>"
          ]
        }
        """;
    }

    private static string BuildDeepAnalysisUserPrompt(
        PullRequestInfo pullRequest,
        PrSummaryResult? prSummary,
        CodeReviewResult reviewResult,
        List<FileChange> fileChanges)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("# Deep Holistic Analysis — Pass 3");
        sb.AppendLine();
        sb.AppendLine($"**PR**: #{pullRequest.PullRequestId} — {pullRequest.Title}");
        sb.AppendLine($"**Author**: {pullRequest.CreatedBy}");
        sb.AppendLine($"**Files Changed**: {fileChanges.Count}");
        sb.AppendLine();

        // Pass 1 context
        if (prSummary != null)
        {
            sb.AppendLine("## Pass 1 — PR Summary");
            sb.AppendLine($"**Intent**: {prSummary.Intent}");
            sb.AppendLine($"**Architectural Impact**: {prSummary.ArchitecturalImpact}");

            if (prSummary.CrossFileRelationships.Count > 0)
            {
                sb.AppendLine("**Cross-File Relationships**:");
                foreach (var rel in prSummary.CrossFileRelationships)
                    sb.AppendLine($"- {rel}");
            }

            if (prSummary.RiskAreas.Count > 0)
            {
                sb.AppendLine("**Risk Areas**:");
                foreach (var risk in prSummary.RiskAreas)
                    sb.AppendLine($"- {risk.Area}: {risk.Reason}");
            }
            sb.AppendLine();
        }

        // Pass 2 context — per-file verdicts
        sb.AppendLine("## Pass 2 — Per-File Review Results");
        sb.AppendLine();
        sb.AppendLine($"**Merged Verdict**: {reviewResult.Summary.Verdict}");
        sb.AppendLine($"**Verdict Justification**: {reviewResult.Summary.VerdictJustification}");
        sb.AppendLine($"**Recommended Vote**: {reviewResult.RecommendedVote}");
        sb.AppendLine($"**Total Inline Comments**: {reviewResult.InlineComments.Count}");
        sb.AppendLine();

        if (reviewResult.FileReviews.Count > 0)
        {
            sb.AppendLine("### Per-File Verdicts");
            foreach (var fr in reviewResult.FileReviews)
            {
                sb.AppendLine($"- `{fr.FilePath}`: **{fr.Verdict}** — {fr.ReviewText}");
            }
            sb.AppendLine();
        }

        // Inline comment summary (severity distribution)
        if (reviewResult.InlineComments.Count > 0)
        {
            sb.AppendLine("### Inline Comment Summary");
            var byLeadIn = reviewResult.InlineComments
                .GroupBy(c => c.LeadIn)
                .OrderByDescending(g => g.Count());
            foreach (var group in byLeadIn)
            {
                sb.AppendLine($"- **{group.Key}**: {group.Count()} comment(s)");
            }
            sb.AppendLine();

            // Include the actual comments for context (truncate if many)
            var commentsToShow = reviewResult.InlineComments.Take(30).ToList();
            sb.AppendLine("### Inline Comments (detail)");
            foreach (var c in commentsToShow)
            {
                sb.AppendLine($"- `{c.FilePath}` L{c.StartLine}-{c.EndLine} [{c.LeadIn}]: {c.Comment}");
            }
            if (reviewResult.InlineComments.Count > 30)
                sb.AppendLine($"... and {reviewResult.InlineComments.Count - 30} more comments");
            sb.AppendLine();
        }

        // File list for cross-reference
        sb.AppendLine("## Files in This PR");
        foreach (var f in fileChanges)
        {
            sb.AppendLine($"- `{f.FilePath}` ({f.ChangeType})");
        }

        return sb.ToString();
    }
}
