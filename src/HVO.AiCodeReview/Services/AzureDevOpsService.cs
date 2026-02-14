using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiCodeReview.Models;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Services;

public class AzureDevOpsService : IAzureDevOpsService
{
    private readonly HttpClient _httpClient;
    private readonly AzureDevOpsSettings _settings;
    private readonly ILogger<AzureDevOpsService> _logger;
    private const string ApiVersion = "api-version=7.1";

    // Lazy-resolved identity ID -- populated from config or auto-discovered from PAT
    private string? _resolvedIdentityId;
    private readonly SemaphoreSlim _identityLock = new(1, 1);

    public AzureDevOpsService(
        HttpClient httpClient,
        IOptions<AzureDevOpsSettings> settings,
        ILogger<AzureDevOpsService> logger)
    {
        _httpClient = httpClient;
        _settings = settings.Value;
        _logger = logger;

        // Set up base authentication for all requests
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{_settings.PersonalAccessToken}"));
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        // Use the configured identity ID if provided
        if (!string.IsNullOrWhiteSpace(_settings.ServiceAccountIdentityId))
        {
            _resolvedIdentityId = _settings.ServiceAccountIdentityId;
            _logger.LogInformation("Using configured ServiceAccountIdentityId: {Id}", _resolvedIdentityId);
        }
    }

    /// <summary>
    /// Gets the identity ID, auto-discovering from the PAT if not configured.
    /// Thread-safe and cached after first resolution.
    /// </summary>
    private async Task<string> GetIdentityIdAsync()
    {
        if (!string.IsNullOrEmpty(_resolvedIdentityId))
            return _resolvedIdentityId;

        await _identityLock.WaitAsync();
        try
        {
            // Double-check after acquiring lock
            if (!string.IsNullOrEmpty(_resolvedIdentityId))
                return _resolvedIdentityId;

            _logger.LogInformation("ServiceAccountIdentityId not configured. Auto-discovering from PAT via connectionData...");

            var url = $"https://dev.azure.com/{_settings.Organization}/_apis/connectionData";
            var response = await _httpClient.GetAsync(url);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            var identityId = json.GetProperty("authenticatedUser").GetProperty("id").GetString()
                ?? throw new InvalidOperationException("Could not resolve identity ID from connectionData response.");

            _resolvedIdentityId = identityId;
            _logger.LogInformation("Auto-discovered identity: {Id} ({DisplayName})",
                identityId,
                json.GetProperty("authenticatedUser").GetProperty("providerDisplayName").GetString() ?? "unknown");

            return _resolvedIdentityId;
        }
        finally
        {
            _identityLock.Release();
        }
    }

    private string BaseUrl(string project, string repository) =>
        $"https://dev.azure.com/{_settings.Organization}/{project}/_apis/git/repositories/{repository}";

    public async Task<PullRequestInfo> GetPullRequestAsync(string project, string repository, int pullRequestId)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}?{ApiVersion}";
        _logger.LogDebug("GET {Url}", url);

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var prInfo = new PullRequestInfo
        {
            PullRequestId = json.GetProperty("pullRequestId").GetInt32(),
            Title = json.GetProperty("title").GetString() ?? string.Empty,
            Description = json.TryGetProperty("description", out var desc) ? desc.GetString() ?? string.Empty : string.Empty,
            SourceBranch = json.GetProperty("sourceRefName").GetString() ?? string.Empty,
            TargetBranch = json.GetProperty("targetRefName").GetString() ?? string.Empty,
            CreatedBy = json.GetProperty("createdBy").GetProperty("displayName").GetString() ?? string.Empty,
            CreatedDate = json.GetProperty("creationDate").GetDateTime(),
            Status = json.GetProperty("status").GetString() ?? string.Empty,
            IsDraft = json.TryGetProperty("isDraft", out var draft) && draft.GetBoolean(),
            LastMergeSourceCommit = json.TryGetProperty("lastMergeSourceCommit", out var src) ? src.GetProperty("commitId").GetString() ?? string.Empty : string.Empty,
            LastMergeTargetCommit = json.TryGetProperty("lastMergeTargetCommit", out var tgt) ? tgt.GetProperty("commitId").GetString() ?? string.Empty : string.Empty,
        };

        if (json.TryGetProperty("reviewers", out var reviewers))
        {
            foreach (var r in reviewers.EnumerateArray())
            {
                prInfo.Reviewers.Add(new PullRequestReviewer
                {
                    Id = r.GetProperty("id").GetString() ?? string.Empty,
                    DisplayName = r.GetProperty("displayName").GetString() ?? string.Empty,
                    Vote = r.GetProperty("vote").GetInt32(),
                });
            }
        }

        return prInfo;
    }

    public async Task<bool> HasReviewTagAsync(string project, string repository, int pullRequestId)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/labels?{ApiVersion}";
        _logger.LogDebug("GET labels: {Url}", url);

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var labels = json.GetProperty("value");

        foreach (var label in labels.EnumerateArray())
        {
            var name = label.GetProperty("name").GetString() ?? string.Empty;
            if (name.Equals(_settings.ReviewTagName, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogInformation("Found review tag '{Tag}' on PR #{PrId}", _settings.ReviewTagName, pullRequestId);
                return true;
            }
        }

        return false;
    }

    public async Task AddReviewTagAsync(string project, string repository, int pullRequestId)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/labels?{ApiVersion}";
        var body = new { name = _settings.ReviewTagName };
        var json = JsonSerializer.Serialize(body);

        _logger.LogInformation("POST label '{Tag}' to PR #{PrId}: {Url}", _settings.ReviewTagName, pullRequestId, url);
        var response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to add review tag: {StatusCode} {ReasonPhrase} — {Body}",
                (int)response.StatusCode, response.ReasonPhrase, errorBody);
        }
        else
        {
            _logger.LogInformation("Added review tag '{Tag}' to PR #{PrId}", _settings.ReviewTagName, pullRequestId);
        }
    }

    private const string PropPrefix = "AiCodeReview";
    private const string PropsApiVersion = "api-version=7.1-preview.1";

    public async Task<ReviewMetadata> GetReviewMetadataAsync(string project, string repository, int pullRequestId)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/properties?{PropsApiVersion}";
        _logger.LogDebug("GET PR properties: {Url}", url);

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var metadata = new ReviewMetadata();

        if (!json.TryGetProperty("value", out var props))
            return metadata;

        // Properties come back as an object keyed by property name,
        // each with { "$type": "...", "$value": "..." }.
        foreach (var prop in props.EnumerateObject())
        {
            var key = prop.Name;
            if (!key.StartsWith(PropPrefix + "."))
                continue;

            if (!prop.Value.TryGetProperty("$value", out var val))
                continue;
            var value = val.GetString() ?? string.Empty;

            switch (key)
            {
                case $"{PropPrefix}.LastSourceCommit":
                    metadata.LastReviewedSourceCommit = value;
                    break;
                case $"{PropPrefix}.LastTargetCommit":
                    metadata.LastReviewedTargetCommit = value;
                    break;
                case $"{PropPrefix}.LastIteration":
                    if (int.TryParse(value, out var iter)) metadata.LastReviewedIteration = iter;
                    break;
                case $"{PropPrefix}.WasDraft":
                    if (bool.TryParse(value, out var wasDraft)) metadata.WasDraft = wasDraft;
                    break;
                case $"{PropPrefix}.ReviewedAtUtc":
                    if (DateTime.TryParse(value, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                        metadata.ReviewedAtUtc = dt;
                    break;
                case $"{PropPrefix}.VoteSubmitted":
                    if (bool.TryParse(value, out var voted)) metadata.VoteSubmitted = voted;
                    break;
                case $"{PropPrefix}.ReviewCount":
                    if (int.TryParse(value, out var count)) metadata.ReviewCount = count;
                    break;
            }
        }

        _logger.LogDebug("Review metadata for PR #{PrId}: LastCommit={Commit}, Iter={Iter}, WasDraft={Draft}, VoteSubmitted={Vote}, ReviewCount={Count}",
            pullRequestId, metadata.LastReviewedSourceCommit ?? "(none)",
            metadata.LastReviewedIteration, metadata.WasDraft, metadata.VoteSubmitted, metadata.ReviewCount);

        return metadata;
    }

    public async Task SetReviewMetadataAsync(string project, string repository, int pullRequestId, ReviewMetadata metadata)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/properties?{PropsApiVersion}";

        var patchOps = new[]
        {
            new { op = "add", path = $"/{PropPrefix}.LastSourceCommit", value = metadata.LastReviewedSourceCommit ?? "" },
            new { op = "add", path = $"/{PropPrefix}.LastTargetCommit", value = metadata.LastReviewedTargetCommit ?? "" },
            new { op = "add", path = $"/{PropPrefix}.LastIteration", value = metadata.LastReviewedIteration.ToString() },
            new { op = "add", path = $"/{PropPrefix}.WasDraft", value = metadata.WasDraft.ToString() },
            new { op = "add", path = $"/{PropPrefix}.ReviewedAtUtc", value = metadata.ReviewedAtUtc.ToString("O") },
            new { op = "add", path = $"/{PropPrefix}.VoteSubmitted", value = metadata.VoteSubmitted.ToString() },
            new { op = "add", path = $"/{PropPrefix}.ReviewCount", value = metadata.ReviewCount.ToString() },
        };

        var json = JsonSerializer.Serialize(patchOps);

        _logger.LogInformation("PATCH PR properties for PR #{PrId}: Commit={Commit}, Iter={Iter}, Draft={Draft}, Vote={Vote}",
            pullRequestId, metadata.LastReviewedSourceCommit, metadata.LastReviewedIteration,
            metadata.WasDraft, metadata.VoteSubmitted);

        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json-patch+json"),
        };
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to set PR properties: {StatusCode} {Reason} — {Body}",
                (int)response.StatusCode, response.ReasonPhrase, errorBody);
        }
    }

    public async Task<int> GetIterationCountAsync(string project, string repository, int pullRequestId)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/iterations?{ApiVersion}";
        _logger.LogDebug("GET iteration count: {Url}", url);

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("value").GetArrayLength();
    }

    public async Task<List<ReviewHistoryEntry>> GetReviewHistoryAsync(string project, string repository, int pullRequestId)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/properties?{PropsApiVersion}";
        _logger.LogDebug("GET PR properties for history: {Url}", url);

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (!json.TryGetProperty("value", out var props))
            return new List<ReviewHistoryEntry>();

        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Name == $"{PropPrefix}.ReviewHistory"
                && prop.Value.TryGetProperty("$value", out var val))
            {
                var historyJson = val.GetString() ?? "[]";
                try
                {
                    return JsonSerializer.Deserialize<List<ReviewHistoryEntry>>(historyJson,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning(ex, "Failed to deserialize review history for PR #{PrId}", pullRequestId);
                }
            }
        }

        return new List<ReviewHistoryEntry>();
    }

    public async Task AppendReviewHistoryAsync(string project, string repository, int pullRequestId, ReviewHistoryEntry entry)
    {
        // Read existing history, append, and write back
        var history = await GetReviewHistoryAsync(project, repository, pullRequestId);
        history.Add(entry);

        var historyJson = JsonSerializer.Serialize(history, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });

        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/properties?{PropsApiVersion}";

        var patchOps = new[]
        {
            new { op = "add", path = $"/{PropPrefix}.ReviewHistory", value = historyJson },
        };

        var json = JsonSerializer.Serialize(patchOps);

        _logger.LogInformation("PATCH PR #{PrId} ReviewHistory: {Count} entries, {Len} chars",
            pullRequestId, history.Count, historyJson.Length);

        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json-patch+json"),
        };
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to store review history: {StatusCode} {Reason} — {Body}",
                (int)response.StatusCode, response.ReasonPhrase, errorBody);
        }
    }

    public async Task<List<ExistingCommentThread>> GetExistingReviewThreadsAsync(
        string project, string repository, int pullRequestId,
        string? attributionTag = null)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/threads?{ApiVersion}";
        _logger.LogDebug("GET threads: {Url}", url);

        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        var threads = new List<ExistingCommentThread>();

        // Build the tag marker we look for, e.g. "_[AiCodeReview]_"
        var tagMarker = !string.IsNullOrEmpty(attributionTag) ? $"_[{attributionTag}]_" : null;

        foreach (var thread in json.GetProperty("value").EnumerateArray())
        {
            // Only look at threads with inline context (file-specific)
            if (!thread.TryGetProperty("threadContext", out var ctx))
                continue;

            // Get the first comment's content
            if (!thread.TryGetProperty("comments", out var comments))
                continue;

            var commentsArr = comments.EnumerateArray().ToList();
            if (commentsArr.Count == 0)
                continue;

            var firstComment = commentsArr[0];
            var content = firstComment.TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";

            // Only track our AI review comments (they start with a bold lead-in)
            if (!content.StartsWith("**"))
                continue;

            var threadId = thread.TryGetProperty("id", out var tid) ? tid.GetInt32() : 0;
            // ADO returns status as either an int or a string depending on API version
            int threadStatus = 0;
            if (thread.TryGetProperty("status", out var ts))
            {
                if (ts.ValueKind == JsonValueKind.Number)
                    threadStatus = ts.GetInt32();
                else if (ts.ValueKind == JsonValueKind.String)
                    threadStatus = StatusToInt(ts.GetString() ?? "");
            }

            var filePath = ctx.TryGetProperty("filePath", out var fp) ? fp.GetString() ?? "" : "";
            int startLine = 0, endLine = 0;
            if (ctx.TryGetProperty("rightFileStart", out var rfs) && rfs.TryGetProperty("line", out var sl))
                startLine = sl.GetInt32();
            if (ctx.TryGetProperty("rightFileEnd", out var rfe) && rfe.TryGetProperty("line", out var el))
                endLine = el.GetInt32();

            // Detect if this was posted by our AI reviewer via the attribution tag
            bool isAiGenerated = tagMarker != null && content.Contains(tagMarker, StringComparison.Ordinal);

            threads.Add(new ExistingCommentThread
            {
                ThreadId = threadId,
                FilePath = filePath,
                StartLine = startLine,
                EndLine = endLine,
                Content = content,
                Status = threadStatus,
                IsAiGenerated = isAiGenerated,
            });
        }

        _logger.LogDebug("Found {Count} existing AI review threads on PR #{PrId} ({AiCount} AI-tagged)",
            threads.Count, pullRequestId, threads.Count(t => t.IsAiGenerated));
        return threads;
    }

    public async Task UpdateThreadStatusAsync(
        string project, string repository, int pullRequestId,
        int threadId, string status)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/threads/{threadId}?{ApiVersion}";

        var body = new { status = StatusToInt(status) };
        var json = JsonSerializer.Serialize(body);

        _logger.LogDebug("PATCH thread {ThreadId} to status '{Status}': {Url}", threadId, status, url);
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to update thread {ThreadId} status: {StatusCode} — {Body}",
                threadId, (int)response.StatusCode, errorBody);
        }
    }

    public async Task<List<FileChange>> GetPullRequestChangesAsync(
        string project, string repository, int pullRequestId, PullRequestInfo prInfo)
    {
        // Get the list of changed items between source and target commits
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/iterations?{ApiVersion}";
        _logger.LogDebug("GET iterations: {Url}", url);

        var iterResponse = await _httpClient.GetAsync(url);
        iterResponse.EnsureSuccessStatusCode();
        var iterJson = await iterResponse.Content.ReadFromJsonAsync<JsonElement>();
        var iterations = iterJson.GetProperty("value");
        int lastIteration = iterations.GetArrayLength();

        // Get changes for the last iteration
        var changesUrl = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/iterations/{lastIteration}/changes?{ApiVersion}";
        _logger.LogDebug("GET changes: {Url}", changesUrl);

        var changesResponse = await _httpClient.GetAsync(changesUrl);
        changesResponse.EnsureSuccessStatusCode();
        var changesJson = await changesResponse.Content.ReadFromJsonAsync<JsonElement>();

        var fileChanges = new List<FileChange>();
        var changeEntries = changesJson.GetProperty("changeEntries");

        foreach (var entry in changeEntries.EnumerateArray())
        {
            var item = entry.GetProperty("item");

            // Skip folders
            if (item.TryGetProperty("isFolder", out var isFolder) && isFolder.GetBoolean())
                continue;

            var path = item.GetProperty("path").GetString() ?? string.Empty;
            var changeType = entry.GetProperty("changeType").GetString() ?? "edit";

            // Skip binary/image files by extension
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (IsBinaryExtension(ext))
            {
                _logger.LogDebug("Skipping binary file: {Path}", path);
                continue;
            }

            var fileChange = new FileChange
            {
                FilePath = path,
                ChangeType = changeType.ToLowerInvariant(),
            };

            // Fetch file contents at source and target commits
            try
            {
                if (changeType.ToLowerInvariant() != "add" && !string.IsNullOrEmpty(prInfo.LastMergeTargetCommit))
                {
                    fileChange.OriginalContent = await GetFileContentAsync(
                        project, repository, path, prInfo.LastMergeTargetCommit);
                }

                if (changeType.ToLowerInvariant() != "delete" && !string.IsNullOrEmpty(prInfo.LastMergeSourceCommit))
                {
                    fileChange.ModifiedContent = await GetFileContentAsync(
                        project, repository, path, prInfo.LastMergeSourceCommit);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to fetch content for {Path}, skipping", path);
                continue;
            }

            // Compute unified diff for edits (when we have both versions)
            if (fileChange.ChangeType == "edit" &&
                !string.IsNullOrEmpty(fileChange.OriginalContent) &&
                !string.IsNullOrEmpty(fileChange.ModifiedContent))
            {
                fileChange.UnifiedDiff = ComputeUnifiedDiff(
                    fileChange.OriginalContent, fileChange.ModifiedContent, path);
                fileChange.ChangedLineRanges = ParseChangedLineRanges(fileChange.UnifiedDiff);
            }
            else if (fileChange.ChangeType == "add" && !string.IsNullOrEmpty(fileChange.ModifiedContent))
            {
                // New file: every line is "changed"
                var lineCount = fileChange.ModifiedContent.Split('\n').Length;
                fileChange.ChangedLineRanges = new List<(int, int)> { (1, lineCount) };
            }
            // For deletes, ChangedLineRanges stays empty (nothing to comment on in modified file)

            fileChanges.Add(fileChange);
        }

        return fileChanges;
    }

    private async Task<string?> GetFileContentAsync(string project, string repository, string path, string commitId)
    {
        var url = $"{BaseUrl(project, repository)}/items?path={Uri.EscapeDataString(path)}&versionType=commit&version={commitId}&{ApiVersion}";
        _logger.LogDebug("GET file content: {Url}", url);

        // Use a custom request to override Accept header — the Items API returns
        // raw file content only when Accept is text/plain or application/octet-stream.
        // With the default application/json, it wraps the content in JSON, collapsing
        // newlines into escape sequences and making the whole file appear as one line.
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Clear();
        request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("text/plain"));

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        return await response.Content.ReadAsStringAsync();
    }

    public async Task<int> CountReviewSummaryCommentsAsync(
        string project, string repository, int pullRequestId)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/threads?{ApiVersion}";
        var response = await _httpClient.GetAsync(url);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        int count = 0;
        foreach (var thread in json.GetProperty("value").EnumerateArray())
        {
            // Skip inline threads (they have threadContext)
            if (thread.TryGetProperty("threadContext", out var ctx) && ctx.ValueKind != JsonValueKind.Null)
                continue;

            if (!thread.TryGetProperty("comments", out var comments))
                continue;

            var commentsArr = comments.EnumerateArray().ToList();
            if (commentsArr.Count == 0) continue;

            var content = commentsArr[0].TryGetProperty("content", out var c) ? c.GetString() ?? "" : "";
            if (content.StartsWith("## Code Review") || content.StartsWith("## Re-Review"))
                count++;
        }

        _logger.LogDebug("Found {Count} existing review summary comments on PR #{PrId}", count, pullRequestId);
        return count;
    }

    public async Task PostCommentThreadAsync(
        string project, string repository, int pullRequestId,
        string content, string status = "closed")
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/threads?{ApiVersion}";

        var threadBody = new
        {
            comments = new[]
            {
                new { parentCommentId = 0, content, commentType = 1 }
            },
            status = StatusToInt(status),
            properties = new Dictionary<string, object>
            {
                ["Microsoft.TeamFoundation.Discussion.UniqueID"] = new
                {
                    type = "System.String",
                    value = Guid.NewGuid().ToString()
                }
            }
        };

        var json = JsonSerializer.Serialize(threadBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });

        // Fix the property type key to use $type/$value format
        json = json.Replace("\"type\":", "\"$type\":").Replace("\"value\":", "\"$value\":");

        _logger.LogDebug("POST thread: {Url}", url);
        var response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task PostInlineCommentThreadAsync(
        string project, string repository, int pullRequestId,
        string filePath, int startLine, int endLine,
        string content, string status = "closed")
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/threads?{ApiVersion}";

        var threadBody = new
        {
            comments = new[]
            {
                new { parentCommentId = 0, content, commentType = 1 }
            },
            status = StatusToInt(status),
            threadContext = new
            {
                filePath,
                rightFileStart = new { line = startLine, offset = 1 },
                rightFileEnd = new { line = endLine, offset = int.MaxValue },
            },
            properties = new Dictionary<string, object>
            {
                ["Microsoft.TeamFoundation.Discussion.UniqueID"] = new
                {
                    type = "System.String",
                    value = Guid.NewGuid().ToString()
                }
            }
        };

        var json = JsonSerializer.Serialize(threadBody, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false,
        });

        json = json.Replace("\"type\":", "\"$type\":").Replace("\"value\":", "\"$value\":");

        _logger.LogDebug("POST inline thread: {Url} for {FilePath}:{StartLine}-{EndLine}", url, filePath, startLine, endLine);
        var response = await _httpClient.PostAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));
        response.EnsureSuccessStatusCode();
    }

    public async Task AddReviewerAsync(string project, string repository, int pullRequestId, int vote)
    {
        var identityId = await GetIdentityIdAsync();
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}/reviewers/{identityId}?{ApiVersion}";

        var reviewerBody = new { id = identityId, vote, isRequired = false };
        var json = JsonSerializer.Serialize(reviewerBody);

        _logger.LogInformation("PUT reviewer id={Id} vote={Vote}: {Url}", identityId, vote, url);
        var response = await _httpClient.PutAsync(url, new StringContent(json, Encoding.UTF8, "application/json"));

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogError("AddReviewer failed: {StatusCode} {ReasonPhrase} — {Body}",
                (int)response.StatusCode, response.ReasonPhrase, errorBody);
            response.EnsureSuccessStatusCode(); // still throw for the caller to handle
        }
    }

    public async Task UpdatePrDescriptionAsync(string project, string repository, int pullRequestId, string newDescription)
    {
        var url = $"{BaseUrl(project, repository)}/pullrequests/{pullRequestId}?{ApiVersion}";

        var body = new { description = newDescription };
        var json = JsonSerializer.Serialize(body);

        _logger.LogInformation("PATCH PR #{PrId} description ({Len} chars)", pullRequestId, newDescription.Length);
        var request = new HttpRequestMessage(new HttpMethod("PATCH"), url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json"),
        };
        var response = await _httpClient.SendAsync(request);

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync();
            _logger.LogWarning("Failed to update PR description: {StatusCode} {Reason} — {Body}",
                (int)response.StatusCode, response.ReasonPhrase, errorBody);
        }
    }

    private static int StatusToInt(string status) => status.ToLowerInvariant() switch
    {
        "active" => 1,
        "fixed" => 2,
        "wontfix" => 3,
        "closed" => 4,
        "bydesign" => 5,
        "pending" => 6,
        _ => 4 // default to closed
    };

    private static bool IsBinaryExtension(string ext) => ext switch
    {
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".ico" or ".svg" or
        ".woff" or ".woff2" or ".ttf" or ".eot" or
        ".zip" or ".gz" or ".tar" or ".rar" or
        ".dll" or ".exe" or ".pdb" or
        ".pdf" or ".doc" or ".docx" or
        ".xls" or ".xlsx" => true,
        _ => false
    };

    /// <summary>
    /// Compute a unified diff between two versions of a file.
    /// Uses a simple longest-common-subsequence algorithm to produce
    /// standard unified diff output with context lines.
    /// </summary>
    internal static string ComputeUnifiedDiff(string original, string modified, string filePath, int contextLines = 3)
    {
        var oldLines = original.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        var newLines = modified.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

        // Build edit script using Myers-like forward-only LCS
        var edits = ComputeEditScript(oldLines, newLines);

        if (edits.Count == 0)
            return "(no changes detected)";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"--- a{filePath}");
        sb.AppendLine($"+++ b{filePath}");

        // Group edits into hunks with context
        var hunks = GroupIntoHunks(edits, oldLines, newLines, contextLines);

        foreach (var hunk in hunks)
        {
            int oldStart = hunk.OldStart + 1; // 1-based
            int newStart = hunk.NewStart + 1;
            sb.AppendLine($"@@ -{oldStart},{hunk.OldCount} +{newStart},{hunk.NewCount} @@");

            foreach (var line in hunk.Lines)
            {
                sb.AppendLine(line);
            }
        }

        return sb.ToString().TrimEnd();
    }

    private enum EditType { Equal, Delete, Insert }
    private record Edit(EditType Type, int OldIndex, int NewIndex);

    private static List<Edit> ComputeEditScript(string[] oldLines, string[] newLines)
    {
        // Simple LCS-based diff
        int m = oldLines.Length, n = newLines.Length;

        // For very large files, if both are identical, short-circuit
        if (m == n)
        {
            bool identical = true;
            for (int i = 0; i < m; i++)
            {
                if (oldLines[i] != newLines[i]) { identical = false; break; }
            }
            if (identical) return new List<Edit>();
        }

        // Compute LCS lengths using rolling arrays (O(n) space)
        var prev = new int[n + 1];
        var curr = new int[n + 1];

        for (int i = m - 1; i >= 0; i--)
        {
            for (int j = n - 1; j >= 0; j--)
            {
                if (oldLines[i] == newLines[j])
                    curr[j] = prev[j + 1] + 1;
                else
                    curr[j] = Math.Max(prev[j], curr[j + 1]);
            }
            (prev, curr) = (curr, prev);
            Array.Clear(curr, 0, curr.Length);
        }

        // Trace forward to build edit script using the LCS table
        // We need to recompute with full table for traceback, but limit to reasonable sizes
        // For files > 5000 lines, fall back to line-by-line comparison
        if ((long)m * n > 25_000_000)
        {
            return ComputeSimpleEditScript(oldLines, newLines);
        }

        var dp = new int[m + 1, n + 1];
        for (int i = m - 1; i >= 0; i--)
        {
            for (int j = n - 1; j >= 0; j--)
            {
                if (oldLines[i] == newLines[j])
                    dp[i, j] = dp[i + 1, j + 1] + 1;
                else
                    dp[i, j] = Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var edits = new List<Edit>();
        int oi = 0, ni = 0;
        while (oi < m && ni < n)
        {
            if (oldLines[oi] == newLines[ni])
            {
                edits.Add(new Edit(EditType.Equal, oi, ni));
                oi++; ni++;
            }
            else if (dp[oi + 1, ni] >= dp[oi, ni + 1])
            {
                edits.Add(new Edit(EditType.Delete, oi, -1));
                oi++;
            }
            else
            {
                edits.Add(new Edit(EditType.Insert, -1, ni));
                ni++;
            }
        }
        while (oi < m) { edits.Add(new Edit(EditType.Delete, oi++, -1)); }
        while (ni < n) { edits.Add(new Edit(EditType.Insert, -1, ni++)); }

        return edits;
    }

    /// <summary>Fallback for very large files — simple line-by-line comparison.</summary>
    private static List<Edit> ComputeSimpleEditScript(string[] oldLines, string[] newLines)
    {
        var edits = new List<Edit>();
        int common = Math.Min(oldLines.Length, newLines.Length);
        for (int i = 0; i < common; i++)
        {
            if (oldLines[i] == newLines[i])
                edits.Add(new Edit(EditType.Equal, i, i));
            else
            {
                edits.Add(new Edit(EditType.Delete, i, -1));
                edits.Add(new Edit(EditType.Insert, -1, i));
            }
        }
        for (int i = common; i < oldLines.Length; i++)
            edits.Add(new Edit(EditType.Delete, i, -1));
        for (int i = common; i < newLines.Length; i++)
            edits.Add(new Edit(EditType.Insert, -1, i));
        return edits;
    }

    private record DiffHunk(int OldStart, int OldCount, int NewStart, int NewCount, List<string> Lines);

    private static List<DiffHunk> GroupIntoHunks(List<Edit> edits, string[] oldLines, string[] newLines, int context)
    {
        int oldLen = oldLines.Length, newLen = newLines.Length;

        // Find ranges of changed lines
        var changeRanges = new List<(int Start, int End)>(); // indices into edits list
        for (int i = 0; i < edits.Count; i++)
        {
            if (edits[i].Type != EditType.Equal)
            {
                int start = i;
                while (i < edits.Count && edits[i].Type != EditType.Equal) i++;
                changeRanges.Add((start, i - 1));
            }
        }

        if (changeRanges.Count == 0) return new List<DiffHunk>();

        // Merge nearby change ranges (within 2*context of each other)
        var mergedRanges = new List<(int Start, int End)> { changeRanges[0] };
        for (int i = 1; i < changeRanges.Count; i++)
        {
            var last = mergedRanges[^1];
            if (changeRanges[i].Start - last.End <= 2 * context)
                mergedRanges[^1] = (last.Start, changeRanges[i].End);
            else
                mergedRanges.Add(changeRanges[i]);
        }

        // Build hunks with context
        var hunks = new List<DiffHunk>();
        foreach (var (rangeStart, rangeEnd) in mergedRanges)
        {
            int hunkStart = Math.Max(0, rangeStart - context);
            int hunkEnd = Math.Min(edits.Count - 1, rangeEnd + context);

            var lines = new List<string>();
            int oldStart = -1, newStart = -1;
            int oldCount = 0, newCount = 0;

            for (int i = hunkStart; i <= hunkEnd; i++)
            {
                var edit = edits[i];
                switch (edit.Type)
                {
                    case EditType.Equal:
                        if (oldStart == -1) { oldStart = edit.OldIndex; newStart = edit.NewIndex; }
                        lines.Add($" {(edit.OldIndex < oldLen ? oldLines[edit.OldIndex] : "")}");
                        oldCount++;
                        newCount++;
                        break;
                    case EditType.Delete:
                        if (oldStart == -1) { oldStart = edit.OldIndex; newStart = FindNewStart(edits, i); }
                        lines.Add($"-{(edit.OldIndex < oldLen ? oldLines[edit.OldIndex] : "")}");
                        oldCount++;
                        break;
                    case EditType.Insert:
                        if (oldStart == -1) { oldStart = FindOldStart(edits, i); newStart = edit.NewIndex; }
                        lines.Add($"+{(edit.NewIndex < newLen ? newLines[edit.NewIndex] : "")}");
                        newCount++;
                        break;
                }
            }

            if (oldStart < 0) oldStart = 0;
            if (newStart < 0) newStart = 0;
            hunks.Add(new DiffHunk(oldStart, oldCount, newStart, newCount, lines));
        }

        return hunks;
    }

    private static int FindNewStart(List<Edit> edits, int fromIndex)
    {
        for (int i = fromIndex + 1; i < edits.Count; i++)
            if (edits[i].NewIndex >= 0) return edits[i].NewIndex;
        for (int i = fromIndex - 1; i >= 0; i--)
            if (edits[i].NewIndex >= 0) return edits[i].NewIndex + 1;
        return 0;
    }

    private static int FindOldStart(List<Edit> edits, int fromIndex)
    {
        for (int i = fromIndex - 1; i >= 0; i--)
            if (edits[i].OldIndex >= 0) return edits[i].OldIndex + 1;
        for (int i = fromIndex + 1; i < edits.Count; i++)
            if (edits[i].OldIndex >= 0) return edits[i].OldIndex;
        return 0;
    }

    /// <summary>
    /// Parse the "+N,M" hunk headers from a unified diff to extract the line ranges
    /// in the modified (new) file that were changed. Returns 1-based line ranges.
    /// </summary>
    internal static List<(int Start, int End)> ParseChangedLineRanges(string unifiedDiff)
    {
        var ranges = new List<(int Start, int End)>();
        if (string.IsNullOrEmpty(unifiedDiff))
            return ranges;

        foreach (var line in unifiedDiff.Split('\n'))
        {
            // Match @@ -oldStart,oldCount +newStart,newCount @@
            if (!line.StartsWith("@@")) continue;

            var plusIdx = line.IndexOf('+', 3);
            if (plusIdx < 0) continue;

            var spaceIdx = line.IndexOf(' ', plusIdx);
            if (spaceIdx < 0) spaceIdx = line.IndexOf('@', plusIdx);
            if (spaceIdx < 0) continue;

            var range = line[(plusIdx + 1)..spaceIdx];
            var parts = range.Split(',');

            if (int.TryParse(parts[0], out var start))
            {
                var count = parts.Length > 1 && int.TryParse(parts[1], out var c) ? c : 1;
                if (count > 0)
                    ranges.Add((start, start + count - 1));
            }
        }

        return ranges;
    }
}
