using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiCodeReview.Models;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Services;

/// <summary>
/// Reviews all changed files in a single Azure OpenAI Assistants API run.
/// Uploads files to a Vector Store with <c>file_search</c>, sends a single prompt
/// referencing the Pass 1 primer, and parses the structured JSON response.
/// 
/// This is NOT an <see cref="ICodeReviewService"/> — it has a fundamentally different
/// lifecycle (upload → vector store → assistant → thread → run → cleanup) that doesn't
/// map to the per-file Chat Completions interface.
/// </summary>
public class VectorStoreReviewService
{
    private readonly AzureOpenAISettings _openAiSettings;
    private readonly AssistantsSettings _assistantsSettings;
    private readonly ILogger<VectorStoreReviewService> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly string? _customInstructions;

    // Extensions natively supported by Azure OpenAI Files API (verified Feb 2026).
    // Files with these extensions are uploaded as-is; all others get .txt appended.
    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".c", ".cpp", ".css", ".csv", ".doc", ".docx", ".gif", ".go", ".html",
        ".java", ".jpeg", ".jpg", ".js", ".json", ".md", ".pdf", ".php", ".pkl",
        ".png", ".pptx", ".py", ".rb", ".tar", ".tex", ".ts", ".txt", ".webp",
        ".xlsx", ".xml", ".zip",
    };

    public VectorStoreReviewService(
        IOptions<AzureOpenAISettings> openAiSettings,
        IOptions<AssistantsSettings> assistantsSettings,
        IHttpClientFactory httpClientFactory,
        ILogger<VectorStoreReviewService> logger)
    {
        _openAiSettings = openAiSettings.Value;
        _assistantsSettings = assistantsSettings.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
        _customInstructions = LoadCustomInstructions(_openAiSettings.CustomInstructionsPath);
    }

    /// <summary>
    /// Execute a full Vector Store-based review of all changed files.
    /// Returns a merged <see cref="CodeReviewResult"/> from the assistant's response.
    /// </summary>
    public async Task<CodeReviewResult> ReviewAllFilesAsync(
        PullRequestInfo prInfo,
        List<FileChange> fileChanges,
        PrSummaryResult? prSummary,
        List<WorkItemInfo>? workItems,
        CancellationToken cancellationToken = default)
    {
        var sw = Stopwatch.StartNew();
        var uploadedFileIds = new List<string>();
        string? vectorStoreId = null;
        string? assistantId = null;

        try
        {
            using var httpClient = CreateHttpClient();

            // ── Step 1: Build filename mapping and upload files ──────────
            var fileMap = new Dictionary<string, string>(); // originalPath → uploadedName
            var reverseMap = new Dictionary<string, string>(); // uploadedName → originalPath

            _logger.LogInformation("[Vector] Uploading {Count} files for PR #{PrId}...",
                fileChanges.Count, prInfo.PullRequestId);

            var uploadSemaphore = new SemaphoreSlim(
                Math.Max(1, _assistantsSettings.MaxParallelUploads));
            var uploadTasks = fileChanges.Select(async file =>
            {
                await uploadSemaphore.WaitAsync(cancellationToken);
                try
                {
                    var content = file.ModifiedContent ?? file.OriginalContent;
                    if (string.IsNullOrEmpty(content))
                    {
                        _logger.LogWarning("[Vector] Skipping {File} — no content available", file.FilePath);
                        return (string?)null;
                    }

                    var uploadedName = GetUploadFilename(file.FilePath);
                    fileMap[file.FilePath] = uploadedName;
                    reverseMap[uploadedName] = file.FilePath;

                    var fileId = await UploadFileAsync(httpClient, content, uploadedName, cancellationToken);
                    lock (uploadedFileIds)
                    {
                        uploadedFileIds.Add(fileId);
                    }

                    _logger.LogDebug("[Vector] Uploaded {Original} → {Uploaded} (id: {Id})",
                        file.FilePath, uploadedName, fileId);
                    return fileId;
                }
                finally
                {
                    uploadSemaphore.Release();
                }
            }).ToArray();

            var results = await Task.WhenAll(uploadTasks);
            uploadSemaphore.Dispose();
            var successfulUploads = results.Where(id => id != null).ToList();

            _logger.LogInformation("[Vector] Uploaded {Success}/{Total} files in {Ms}ms",
                successfulUploads.Count, fileChanges.Count, sw.ElapsedMilliseconds);

            if (successfulUploads.Count == 0)
                throw new InvalidOperationException("No files were successfully uploaded to Azure OpenAI.");

            // ── Step 2: Create Vector Store and add files via file_batches ──
            vectorStoreId = await CreateVectorStoreAsync(httpClient,
                $"PR-{prInfo.PullRequestId}-review-{DateTime.UtcNow:yyyyMMdd-HHmmss}",
                cancellationToken);

            _logger.LogInformation("[Vector] Created vector store {VsId}", vectorStoreId);

            await AddFileBatchAsync(httpClient, vectorStoreId, uploadedFileIds, cancellationToken);

            _logger.LogInformation("[Vector] Added {Count} files to vector store via file_batches", uploadedFileIds.Count);

            // ── Step 3: Poll until vector store indexing is complete ─────
            await PollVectorStoreAsync(httpClient, vectorStoreId, cancellationToken);

            _logger.LogInformation("[Vector] Vector store {VsId} indexing complete ({Ms}ms elapsed)",
                vectorStoreId, sw.ElapsedMilliseconds);

            // ── Step 4: Create Assistant with file_search ────────────────
            var systemPrompt = BuildVectorSystemPrompt(fileMap, prInfo, workItems);
            assistantId = await CreateAssistantAsync(httpClient, vectorStoreId, systemPrompt, cancellationToken);

            _logger.LogInformation("[Vector] Created assistant {AsstId}", assistantId);

            // ── Step 5: Create Thread with user message ─────────────────
            var userMessage = BuildUserMessage(prInfo, fileChanges, prSummary);
            var (threadId, runId) = await CreateThreadAndRunAsync(
                httpClient, assistantId, userMessage, cancellationToken);

            _logger.LogInformation("[Vector] Created thread {ThreadId}, run {RunId}", threadId, runId);

            // ── Step 6: Poll run until completed ────────────────────────
            await PollRunAsync(httpClient, threadId, runId, cancellationToken);

            // ── Step 7: Retrieve response and parse ─────────────────────
            var responseText = await GetAssistantResponseAsync(httpClient, threadId, cancellationToken);

            sw.Stop();
            _logger.LogInformation("[Vector] Run completed in {Ms}ms, response length: {Len} chars",
                sw.ElapsedMilliseconds, responseText.Length);

            var reviewResult = ParseReviewResponse(responseText, fileChanges);

            // Set metrics
            reviewResult.ModelName = _openAiSettings.DeploymentName;
            reviewResult.AiDurationMs = sw.ElapsedMilliseconds;

            return reviewResult;
        }
        finally
        {
            // ── Cleanup: delete assistant, vector store, files ───────────
            await CleanupResourcesAsync(assistantId, vectorStoreId, uploadedFileIds);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // File Upload
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Compute the upload filename. Unsupported extensions get .txt appended.
    /// Includes directory path in filename so the model sees project structure.
    /// </summary>
    internal static string GetUploadFilename(string originalPath)
    {
        // Normalize path separators
        var normalized = originalPath.Replace('\\', '/').TrimStart('/');

        var ext = Path.GetExtension(normalized);
        if (string.IsNullOrEmpty(ext) || !SupportedExtensions.Contains(ext))
        {
            // Append .txt for unsupported extensions
            return normalized + ".txt";
        }

        return normalized;
    }

    /// <summary>
    /// Check if a given extension requires .txt rename for upload.
    /// </summary>
    internal static bool NeedsRename(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return string.IsNullOrEmpty(ext) || !SupportedExtensions.Contains(ext);
    }

    private async Task<string> UploadFileAsync(
        HttpClient httpClient, string content, string uploadFilename,
        CancellationToken cancellationToken)
    {
        // Write content to a temp file, then upload with filename override
        var tempFile = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(tempFile, content, Encoding.UTF8, cancellationToken);

            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(await File.ReadAllBytesAsync(tempFile, cancellationToken));
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
            form.Add(fileContent, "file", uploadFilename);
            form.Add(new StringContent("assistants"), "purpose");

            var url = $"{BaseUrl}/openai/files?api-version={_assistantsSettings.ApiVersion}";
            var response = await httpClient.PostAsync(url, form, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"File upload failed ({response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("id").GetString()
                   ?? throw new InvalidOperationException("File upload response missing 'id'.");
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Vector Store
    // ══════════════════════════════════════════════════════════════════════

    private async Task<string> CreateVectorStoreAsync(
        HttpClient httpClient, string name, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { name });
        var url = $"{BaseUrl}/openai/vector_stores?api-version={_assistantsSettings.ApiVersion}";
        var response = await httpClient.PostAsync(url,
            new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Vector store creation failed ({response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Vector store response missing 'id'.");
    }

    private async Task AddFileBatchAsync(
        HttpClient httpClient, string vectorStoreId, List<string> fileIds,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Serialize(new { file_ids = fileIds });
        var url = $"{BaseUrl}/openai/vector_stores/{vectorStoreId}/file_batches?api-version={_assistantsSettings.ApiVersion}";
        var response = await httpClient.PostAsync(url,
            new StringContent(payload, Encoding.UTF8, "application/json"), cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"File batch add failed ({response.StatusCode}): {body}");
    }

    private async Task PollVectorStoreAsync(
        HttpClient httpClient, string vectorStoreId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/openai/vector_stores/{vectorStoreId}?api-version={_assistantsSettings.ApiVersion}";

        for (int attempt = 0; attempt < _assistantsSettings.MaxPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await httpClient.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Vector store poll failed ({response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (status == "completed")
                return;

            if (status == "failed" || status == "cancelled" || status == "expired")
                throw new InvalidOperationException($"Vector store indexing {status}: {body}");

            _logger.LogDebug("[Vector] Vector store {VsId} status: {Status}, attempt {Attempt}/{Max}",
                vectorStoreId, status, attempt + 1, _assistantsSettings.MaxPollAttempts);

            await Task.Delay(_assistantsSettings.PollIntervalMs, cancellationToken);
        }

        throw new TimeoutException(
            $"Vector store {vectorStoreId} did not complete within {_assistantsSettings.MaxPollAttempts * _assistantsSettings.PollIntervalMs}ms.");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Assistant
    // ══════════════════════════════════════════════════════════════════════

    private async Task<string> CreateAssistantAsync(
        HttpClient httpClient, string vectorStoreId, string systemPrompt,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            model = _openAiSettings.DeploymentName,
            name = "AI Code Reviewer (Vector)",
            instructions = systemPrompt,
            tools = new[] { new { type = "file_search" } },
            tool_resources = new
            {
                file_search = new
                {
                    vector_store_ids = new[] { vectorStoreId },
                },
            },
        };

        var url = $"{BaseUrl}/openai/assistants?api-version={_assistantsSettings.ApiVersion}";
        var response = await httpClient.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Assistant creation failed ({response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("id").GetString()
               ?? throw new InvalidOperationException("Assistant response missing 'id'.");
    }

    // ══════════════════════════════════════════════════════════════════════
    // Thread + Run
    // ══════════════════════════════════════════════════════════════════════

    private async Task<(string threadId, string runId)> CreateThreadAndRunAsync(
        HttpClient httpClient, string assistantId, string userMessage,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            assistant_id = assistantId,
            thread = new
            {
                messages = new[]
                {
                    new { role = "user", content = userMessage },
                },
            },
        };

        var url = $"{BaseUrl}/openai/threads/runs?api-version={_assistantsSettings.ApiVersion}";
        var response = await httpClient.PostAsync(url,
            new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json"),
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Thread/Run creation failed ({response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var threadId = doc.RootElement.GetProperty("thread_id").GetString()
                       ?? throw new InvalidOperationException("Missing 'thread_id'.");
        var runId = doc.RootElement.GetProperty("id").GetString()
                    ?? throw new InvalidOperationException("Missing run 'id'.");

        return (threadId, runId);
    }

    private async Task PollRunAsync(
        HttpClient httpClient, string threadId, string runId,
        CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/openai/threads/{threadId}/runs/{runId}?api-version={_assistantsSettings.ApiVersion}";

        for (int attempt = 0; attempt < _assistantsSettings.MaxPollAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await httpClient.GetAsync(url, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Run poll failed ({response.StatusCode}): {body}");

            using var doc = JsonDocument.Parse(body);
            var status = doc.RootElement.GetProperty("status").GetString();

            if (status == "completed")
                return;

            if (status is "failed" or "cancelled" or "expired")
            {
                var lastError = doc.RootElement.TryGetProperty("last_error", out var errEl)
                    ? errEl.GetRawText() : "unknown";
                throw new InvalidOperationException($"Assistant run {status}: {lastError}");
            }

            _logger.LogDebug("[Vector] Run {RunId} status: {Status}, attempt {Attempt}/{Max}",
                runId, status, attempt + 1, _assistantsSettings.MaxPollAttempts);

            await Task.Delay(_assistantsSettings.PollIntervalMs, cancellationToken);
        }

        throw new TimeoutException(
            $"Run {runId} did not complete within {_assistantsSettings.MaxPollAttempts * _assistantsSettings.PollIntervalMs}ms.");
    }

    private async Task<string> GetAssistantResponseAsync(
        HttpClient httpClient, string threadId, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/openai/threads/{threadId}/messages?api-version={_assistantsSettings.ApiVersion}&order=desc&limit=1";
        var response = await httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Messages fetch failed ({response.StatusCode}): {body}");

        using var doc = JsonDocument.Parse(body);
        var data = doc.RootElement.GetProperty("data");
        if (data.GetArrayLength() == 0)
            throw new InvalidOperationException("No messages in thread after run completed.");

        var firstMessage = data[0];
        var contentArray = firstMessage.GetProperty("content");

        var sb = new StringBuilder();
        foreach (var item in contentArray.EnumerateArray())
        {
            if (item.GetProperty("type").GetString() == "text")
            {
                sb.Append(item.GetProperty("text").GetProperty("value").GetString());
            }
        }

        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Cleanup
    // ══════════════════════════════════════════════════════════════════════

    private async Task CleanupResourcesAsync(
        string? assistantId, string? vectorStoreId, List<string> fileIds)
    {
        using var httpClient = CreateHttpClient();

        // Delete assistant
        if (!string.IsNullOrEmpty(assistantId))
        {
            try
            {
                await httpClient.DeleteAsync(
                    $"{BaseUrl}/openai/assistants/{assistantId}?api-version={_assistantsSettings.ApiVersion}");
                _logger.LogDebug("[Vector] Deleted assistant {AsstId}", assistantId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Vector] Failed to delete assistant {AsstId}", assistantId);
            }
        }

        // Delete vector store
        if (!string.IsNullOrEmpty(vectorStoreId))
        {
            try
            {
                await httpClient.DeleteAsync(
                    $"{BaseUrl}/openai/vector_stores/{vectorStoreId}?api-version={_assistantsSettings.ApiVersion}");
                _logger.LogDebug("[Vector] Deleted vector store {VsId}", vectorStoreId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Vector] Failed to delete vector store {VsId}", vectorStoreId);
            }
        }

        // Delete uploaded files
        foreach (var fileId in fileIds)
        {
            try
            {
                await httpClient.DeleteAsync(
                    $"{BaseUrl}/openai/files/{fileId}?api-version={_assistantsSettings.ApiVersion}");
                _logger.LogDebug("[Vector] Deleted file {FileId}", fileId);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Vector] Failed to delete file {FileId}", fileId);
            }
        }

        _logger.LogInformation("[Vector] Cleanup complete: {AsstDeleted} assistant, {VsDeleted} vector store, {FilesDeleted} files",
            assistantId != null ? "1" : "0",
            vectorStoreId != null ? "1" : "0",
            fileIds.Count);
    }

    // ══════════════════════════════════════════════════════════════════════
    // Prompt Construction
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Build the system prompt for the Vector Store assistant.
    /// Includes the filename mapping so the AI reports original filenames.
    /// </summary>
    private string BuildVectorSystemPrompt(
        Dictionary<string, string> fileMap,
        PullRequestInfo prInfo,
        List<WorkItemInfo>? workItems)
    {
        var sb = new StringBuilder();

        // ── Identity ────────────────────────────────────────────────────
        sb.AppendLine("""
            You are an expert code reviewer for an enterprise development team. You perform thorough,
            constructive code reviews focusing on correctness, security, performance, maintainability,
            and best practices.

            REVIEW FOCUS — PRIORITIZED:
            Primary (always flag): bugs, security vulnerabilities, logic errors, missing error handling.
            Secondary (flag for significant deviations): performance issues, simplification opportunities,
            reusability improvements, best-practice violations.
            Tertiary (flag only for major divergence from codebase standards): maintainability, code consistency,
            naming conventions.

            QUALITY BAR: You are a senior engineer who values signal over noise. Only create inline comments
            for issues that a senior developer would genuinely care about. Avoid obvious, pedantic, or
            stylistic nit-picks.
            """);

        // ── Custom instructions ─────────────────────────────────────────
        if (!string.IsNullOrWhiteSpace(_customInstructions))
        {
            sb.AppendLine();
            sb.AppendLine(_customInstructions);
        }

        // ── Filename mapping instruction ────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("## FILE NAMING CONVENTION");
        sb.AppendLine();
        sb.AppendLine("Some files have been uploaded with a `.txt` extension appended to bypass ingestion limits.");
        sb.AppendLine("When referencing files in your response, ALWAYS use the **original filename** (without the `.txt` suffix).");
        sb.AppendLine("Use the `file_search` tool to read files, but report paths using the original names below.");
        sb.AppendLine();

        // Only include mappings where the name was actually changed
        var renamedFiles = fileMap.Where(kv => kv.Value != kv.Key.Replace('\\', '/').TrimStart('/'))
            .ToList();

        if (renamedFiles.Count > 0)
        {
            sb.AppendLine("File mapping (uploaded_name → original_name):");
            sb.AppendLine("```json");
            sb.AppendLine("{");
            for (int i = 0; i < renamedFiles.Count; i++)
            {
                var comma = i < renamedFiles.Count - 1 ? "," : "";
                sb.AppendLine($"  \"{renamedFiles[i].Value}\": \"{renamedFiles[i].Key}\"{comma}");
            }
            sb.AppendLine("}");
            sb.AppendLine("```");
        }
        else
        {
            sb.AppendLine("All files were uploaded with their original names — no mapping needed.");
        }

        // ── All file paths for reference ────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("## ALL FILES IN THIS PR (use these exact paths in your response):");
        foreach (var kv in fileMap)
        {
            sb.AppendLine($"- `{kv.Key}`");
        }

        // ── Response format ─────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine(VectorResponseFormatRules);

        return sb.ToString();
    }

    /// <summary>
    /// Build the user message with PR context, Pass 1 primer, and diff summary.
    /// </summary>
    private static string BuildUserMessage(
        PullRequestInfo prInfo,
        List<FileChange> fileChanges,
        PrSummaryResult? prSummary)
    {
        var sb = new StringBuilder();

        // ── PR metadata ─────────────────────────────────────────────────
        sb.AppendLine("## Pull Request Context");
        sb.AppendLine();
        sb.AppendLine($"- **PR #{prInfo.PullRequestId}**: {prInfo.Title}");
        sb.AppendLine($"- **Author**: {prInfo.CreatedBy}");
        sb.AppendLine($"- **Branch**: `{prInfo.SourceBranch}` → `{prInfo.TargetBranch}`");

        if (!string.IsNullOrWhiteSpace(prInfo.Description))
        {
            sb.AppendLine();
            sb.AppendLine("**Description**:");
            // Truncate very long descriptions
            var desc = prInfo.Description.Length > 2000
                ? prInfo.Description[..2000] + "\n... [truncated]"
                : prInfo.Description;
            sb.AppendLine(desc);
        }

        // ── Pass 1 primer (cross-file context) ──────────────────────────
        if (prSummary != null)
        {
            sb.AppendLine();
            sb.AppendLine("## Prior Analysis (Pass 1 — Cross-File Summary)");
            sb.AppendLine();
            sb.AppendLine($"**Intent**: {prSummary.Intent}");

            if (!string.IsNullOrWhiteSpace(prSummary.ArchitecturalImpact))
                sb.AppendLine($"**Architectural Impact**: {prSummary.ArchitecturalImpact}");

            if (prSummary.CrossFileRelationships.Count > 0)
            {
                sb.AppendLine("**Cross-File Relationships**:");
                foreach (var rel in prSummary.CrossFileRelationships)
                    sb.AppendLine($"  - {rel}");
            }

            if (prSummary.RiskAreas.Count > 0)
            {
                sb.AppendLine("**Risk Areas**:");
                foreach (var risk in prSummary.RiskAreas)
                    sb.AppendLine($"  - **{risk.Area}**: {risk.Reason}");
            }
        }

        // ── File change summary ─────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("## Changed Files");
        sb.AppendLine();
        sb.AppendLine($"Total: {fileChanges.Count} files changed");
        sb.AppendLine();

        foreach (var file in fileChanges)
        {
            var changeBadge = file.ChangeType switch
            {
                "add" => "[NEW]",
                "delete" => "[DELETED]",
                "edit" => "[MODIFIED]",
                "rename" => "[RENAMED]",
                _ => $"[{file.ChangeType.ToUpper()}]",
            };

            var lineInfo = "";
            if (file.ChangedLineRanges.Count > 0)
            {
                var ranges = file.ChangedLineRanges
                    .Take(5) // limit to first 5 ranges for prompt brevity
                    .Select(r => r.Start == r.End ? $"L{r.Start}" : $"L{r.Start}-{r.End}");
                lineInfo = $" (changed: {string.Join(", ", ranges)})";
                if (file.ChangedLineRanges.Count > 5)
                    lineInfo += $" +{file.ChangedLineRanges.Count - 5} more ranges";
            }

            sb.AppendLine($"- {changeBadge} `{file.FilePath}`{lineInfo}");
        }

        // ── Diff snippets (compact) ─────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("## Diffs (unified format)");
        sb.AppendLine();
        sb.AppendLine("Use the `file_search` tool to read the full file content. The diffs below show what changed:");
        sb.AppendLine();

        foreach (var file in fileChanges)
        {
            if (!string.IsNullOrWhiteSpace(file.UnifiedDiff))
            {
                // Truncate very large diffs
                var diff = file.UnifiedDiff.Length > 3000
                    ? file.UnifiedDiff[..3000] + "\n... [diff truncated]"
                    : file.UnifiedDiff;
                sb.AppendLine($"### `{file.FilePath}`");
                sb.AppendLine("```diff");
                sb.AppendLine(diff);
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        // ── Instruction ─────────────────────────────────────────────────
        sb.AppendLine();
        sb.AppendLine("## Your Task");
        sb.AppendLine();
        sb.AppendLine("Review ALL changed files listed above. Use `file_search` to read their full content.");
        sb.AppendLine("Focus on the diffs (what changed) but use the full file context to understand cross-file impacts.");
        sb.AppendLine("Return your review as a single JSON object matching the schema in your instructions.");
        sb.AppendLine("Respond ONLY with valid JSON — no markdown wrappers, no explanation text outside the JSON.");

        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════════════
    // Response Parsing
    // ══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Parse the assistant's JSON response into a <see cref="CodeReviewResult"/>.
    /// Handles common JSON wrapper issues (markdown fences, leading text).
    /// </summary>
    internal static CodeReviewResult ParseReviewResponse(string responseText, List<FileChange> fileChanges)
    {
        // Strip markdown JSON fences if present
        var text = responseText.Trim();
        if (text.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
            text = text[7..];
        else if (text.StartsWith("```"))
            text = text[3..];

        if (text.EndsWith("```"))
            text = text[..^3];

        text = text.Trim();

        // Find the first '{' in case there's preamble text
        var jsonStart = text.IndexOf('{');
        if (jsonStart > 0)
            text = text[jsonStart..];

        // Find the last '}' in case there's trailing text
        var jsonEnd = text.LastIndexOf('}');
        if (jsonEnd >= 0 && jsonEnd < text.Length - 1)
            text = text[..(jsonEnd + 1)];

        try
        {
            var options = new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            };

            var result = JsonSerializer.Deserialize<CodeReviewResult>(text, options);
            if (result == null)
                throw new JsonException("Deserialized result was null.");

            // Ensure summary has correct file count
            result.Summary ??= new ReviewSummary();
            result.Summary.FilesChanged = fileChanges.Count;

            return result;
        }
        catch (JsonException ex)
        {
            // If JSON parsing fails, return a fallback result
            return new CodeReviewResult
            {
                Summary = new ReviewSummary
                {
                    FilesChanged = fileChanges.Count,
                    Description = "Vector Store review completed but response parsing failed.",
                    Verdict = "APPROVED WITH SUGGESTIONS",
                    VerdictJustification = $"AI response could not be parsed as structured JSON: {ex.Message}. Raw response preserved in observations.",
                },
                Observations = new List<string>
                {
                    $"Raw AI response (first 2000 chars): {responseText[..Math.Min(responseText.Length, 2000)]}",
                },
                RecommendedVote = 5,
            };
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    // Helpers
    // ══════════════════════════════════════════════════════════════════════

    private string BaseUrl => _openAiSettings.Endpoint.TrimEnd('/');

    private HttpClient CreateHttpClient()
    {
        var client = _httpClientFactory.CreateClient("AzureOpenAIAssistants");
        client.DefaultRequestHeaders.Add("api-key", _openAiSettings.ApiKey);
        client.Timeout = TimeSpan.FromMinutes(5);
        return client;
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
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("customInstructions", out var el))
            {
                var text = el.GetString();
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning(
                $"Could not load custom instructions from '{resolvedPath}': {ex.Message}");
        }

        return null;
    }

    // ══════════════════════════════════════════════════════════════════════
    // Response Format Rules (Vector Store variant)
    // ══════════════════════════════════════════════════════════════════════

    private const string VectorResponseFormatRules = """
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
            "verdictJustification": "<justification — for REJECTED/NEEDS WORK: detailed paragraph naming every blocking file and issue. For APPROVED: one sentence is fine.>"
          },
          "fileReviews": [
            {
              "filePath": "<repo-relative path — use ORIGINAL filename, not the uploaded .txt name>",
              "verdict": "NEEDS WORK|CONCERN|OBSERVATION",
              "reviewText": "<concise assessment — what's wrong and what to fix>"
            }
          ],
          "inlineComments": [
            {
              "filePath": "<repo-relative path — use ORIGINAL filename, not the uploaded .txt name>",
              "startLine": <int>,
              "endLine": <int>,
              "codeSnippet": "<1-3 lines of actual code being commented on, copied VERBATIM from the file>",
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
        1. Be constructive and specific. Reference actual code.
        2. Use "closed" for positive/informational comments, "active" for items needing attention.
        3. Lead-in categories: LGTM, Good catch, Important, Concern, Suggestion, Bug, Security, Performance.
        4. recommendedVote: 10=Approved, 5=Approved with suggestions, -5=Wait for author, -10=Rejected.
        5. If no significant issues, return verdict "APPROVED" with vote 10.
        6. SKIP CLEAN FILES from fileReviews. An empty fileReviews array is fine.
        7. Aim for 0-3 inline comments per file. Zero is fine for clean files.
        8. Every inline comment MUST include codeSnippet — 1-3 lines copied VERBATIM from the file.
        9. Use file_search to determine accurate line numbers from the actual file content.
        10. Inline comments must reference specific code constructs, not generic observations.
        11. FILE PATHS: Use the ORIGINAL filename from the mapping above, NOT the uploaded .txt name.
            For example, use "/src/Services/MyService.cs" not "src/Services/MyService.cs.txt".
        12. Only output valid JSON. No markdown, no explanation text outside JSON.
        13. SECURITY: Empty config values are NOT security issues. Only flag real hardcoded secrets.
        14. REJECTION/NEEDS WORK: The verdictJustification MUST name every blocking file by path,
            describe the specific problem, explain why it's a blocker, and suggest remediation.
        15. CROSS-FILE REVIEW: You have access to ALL changed files via file_search.
            Look for cross-file issues: broken contracts, missing dependency updates,
            inconsistent patterns across related files, interface/implementation mismatches.
        16. ACCEPTANCE CRITERIA: If work item AC are provided, populate acceptanceCriteriaAnalysis.
            If no AC provided, omit it entirely.
        """;
}
