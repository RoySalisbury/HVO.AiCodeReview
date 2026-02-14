using System.ClientModel;
using System.Text.Json;
using AiCodeReview.Models;
using Azure.AI.OpenAI;
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
    private readonly string _systemPrompt;
    private readonly string _singleFileSystemPrompt;

    // ── Legacy constructor: used by direct DI registration via IOptions ──

    public AzureOpenAiReviewService(
        IOptions<AzureOpenAISettings> settings,
        ILogger<AzureOpenAiReviewService> logger)
        : this(
            settings.Value.Endpoint,
            settings.Value.ApiKey,
            settings.Value.DeploymentName,
            settings.Value.CustomInstructionsPath,
            logger)
    { }

    // ── Factory constructor: used by CodeReviewServiceFactory from ProviderConfig ──

    public AzureOpenAiReviewService(
        string endpoint,
        string apiKey,
        string modelName,
        string? customInstructionsPath,
        ILogger<AzureOpenAiReviewService> logger)
    {
        _modelName = modelName;
        _logger = logger;

        var client = new AzureOpenAIClient(
            new Uri(endpoint),
            new ApiKeyCredential(apiKey));

        _chatClient = client.GetChatClient(modelName);

        // Build system prompts
        _systemPrompt = BuildSystemPrompt(customInstructionsPath);
        _singleFileSystemPrompt = BuildSingleFileSystemPrompt(customInstructionsPath);
        _logger.LogInformation("[{Provider}] System prompts assembled (multi-file: {MultiLen} chars, single-file: {SingleLen} chars)",
            modelName, _systemPrompt.Length, _singleFileSystemPrompt.Length);
    }

    public async Task<CodeReviewResult> ReviewAsync(PullRequestInfo pullRequest, List<FileChange> fileChanges)
    {
        var systemPrompt = _systemPrompt;
        var userPrompt = BuildUserPrompt(pullRequest, fileChanges);

        _logger.LogInformation("Sending {FileCount} files to AI for review of PR #{PrId}",
            fileChanges.Count, pullRequest.PullRequestId);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(systemPrompt),
            new UserChatMessage(userPrompt),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            MaxOutputTokenCount = 16000,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            response = await _chatClient.CompleteChatAsync(messages, options);
        }
        catch (ClientResultException cex)
        {
            _logger.LogError(cex, "Azure OpenAI API error. Status: {Status}. Message: {Message}",
                cex.Status, cex.Message);
            throw;
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
    public async Task<CodeReviewResult> ReviewFileAsync(PullRequestInfo pullRequest, FileChange file, int totalFilesInPr)
    {
        var userPrompt = BuildSingleFileUserPrompt(pullRequest, file, totalFilesInPr);

        _logger.LogInformation("Reviewing file {FilePath} ({ChangeType}) for PR #{PrId}",
            file.FilePath, file.ChangeType, pullRequest.PullRequestId);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(_singleFileSystemPrompt),
            new UserChatMessage(userPrompt),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            MaxOutputTokenCount = 4000,  // single file needs fewer tokens
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Retry with exponential backoff for rate limiting (HTTP 429)
        const int maxRetries = 3;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                response = await _chatClient.CompleteChatAsync(messages, options);
                break; // Success
            }
            catch (ClientResultException cex) when (cex.Status == 429 && attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1)); // 2s, 4s, 8s
                _logger.LogWarning("Rate limited (429) reviewing {FilePath}, retry {Attempt}/{Max} after {Delay}s",
                    file.FilePath, attempt + 1, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay);
            }
            catch (ClientResultException cex)
            {
                _logger.LogError(cex, "AI error reviewing {FilePath}. Status: {Status}", file.FilePath, cex.Status);
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

            return result;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse single-file AI response for {FilePath}", file.FilePath);
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
    public async Task<List<ThreadVerificationResult>> VerifyThreadResolutionsAsync(
        List<ThreadVerificationCandidate> candidates)
    {
        if (candidates.Count == 0)
            return new List<ThreadVerificationResult>();

        _logger.LogInformation("Verifying {Count} prior AI comment(s) for resolution...", candidates.Count);

        var userPrompt = BuildVerificationUserPrompt(candidates);

        var messages = new List<ChatMessage>
        {
            new SystemChatMessage(ThreadVerificationSystemPrompt),
            new UserChatMessage(userPrompt),
        };

        var options = new ChatCompletionOptions
        {
            Temperature = 0.1f,
            MaxOutputTokenCount = 2000,
            ResponseFormat = ChatResponseFormat.CreateJsonObjectFormat(),
        };

        ClientResult<ChatCompletion> response;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Retry with exponential backoff for rate limiting (HTTP 429)
        const int maxRetries = 3;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                response = await _chatClient.CompleteChatAsync(messages, options);
                break;
            }
            catch (ClientResultException cex) when (cex.Status == 429 && attempt < maxRetries)
            {
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt + 1));
                _logger.LogWarning("Rate limited (429) during thread verification, retry {Attempt}/{Max} after {Delay}s",
                    attempt + 1, maxRetries, delay.TotalSeconds);
                await Task.Delay(delay);
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

    private static string BuildSystemPrompt(string? instructionsPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(IdentityPreamble);

        // Load optional custom instructions from file
        var custom = LoadCustomInstructions(instructionsPath);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(custom);
        }

        sb.AppendLine();
        sb.AppendLine();
        sb.Append(ResponseFormatRules);

        return sb.ToString();
    }

    private static string BuildSingleFileSystemPrompt(string? instructionsPath)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(IdentityPreamble);

        var custom = LoadCustomInstructions(instructionsPath);
        if (!string.IsNullOrWhiteSpace(custom))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(custom);
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
                "verdictJustification": "<one-sentence justification>"
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
            15. EXAMPLE of a correct inline comment (given input file lines "  45 | public void Process() {" through "  70 | }"):
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
                "verdictJustification": "<one-sentence justification for this file>"
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
            """;

    private static string BuildUserPrompt(PullRequestInfo pr, List<FileChange> fileChanges)
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
    private static string BuildSingleFileUserPrompt(PullRequestInfo pr, FileChange file, int totalFilesInPr)
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
    /// </summary>
    private static string TruncateContent(string content, int maxLines = 500)
    {
        var lines = content.Split('\n');
        if (lines.Length <= maxLines)
            return content;

        var truncated = string.Join('\n', lines.Take(maxLines));
        return truncated + $"\n\n... [truncated: {lines.Length - maxLines} more lines] ...";
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
}
