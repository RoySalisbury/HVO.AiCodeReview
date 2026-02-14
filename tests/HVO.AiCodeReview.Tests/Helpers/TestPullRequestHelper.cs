using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace AiCodeReview.Tests.Helpers;

/// <summary>
/// Helper that creates a **disposable test repository** in Azure DevOps.
///
/// Each test run gets a brand-new repo → branch → PR. When the test
/// completes, the entire repository is deleted (soft delete + hard delete
/// from the recycle bin), which permanently removes all PRs, branches,
/// comments, and metadata — zero leftover artifacts.
///
/// Flow:
///   1. CreateDraftPullRequestAsync() creates the repo, pushes an initial
///      file to "main", creates a test branch with a change, opens a
///      draft PR.
///   2. Tests exercise the PR exactly as before (PushNewCommitAsync,
///      SetDraftStatusAsync, etc.).
///   3. DisposeAsync() deletes the repo from ADO and purges it from
///      the recycle bin.
///
/// The disposable repo name is "AiCodeReview-IntTest-{8-char-guid}" so
/// it's easily identifiable.
/// </summary>
public class TestPullRequestHelper : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly string _org;
    private readonly string _project;
    private const string ApiVersion = "api-version=7.1";
    private const string TestBranchName = "refs/heads/test/changes";

    // ═══════════════════════════════════════════════════════════════════════
    //  SAFETY: Multiple layers to prevent accidental deletion of real repos
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>Required prefix for any repo this helper will create or delete.</summary>
    private const string RepoNamePrefix = "AiCodeReview-IntTest-";

    /// <summary>
    /// Magic string embedded in the marker file. If this string is not present
    /// in the repo's marker file, the repo will NOT be deleted.
    /// </summary>
    private const string SafetyMagicString = "AITK-DISPOSABLE-TEST-REPO-7F3A9B2E-SAFE-TO-DELETE";

    /// <summary>
    /// Repositories that must NEVER be deleted, regardless of any other checks.
    /// Add any production or shared repo names here as an absolute last line of defense.
    /// </summary>
    private static readonly HashSet<string> NeverDeleteRepoNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "POCResearchScratchProjects",
        "OneVision",
        // Add any other protected repo names here
    };

    /// <summary>Set to true ONLY when this instance successfully created the repo.</summary>
    private bool _createdByThisInstance;

    /// <summary>Unique token generated at creation time and stored in the marker file.</summary>
    private string? _instanceMarkerToken;

    /// <summary>The project GUID (captured from repo creation response).</summary>
    private string? _projectId;

    /// <summary>
    /// The security descriptor of the PAT-authenticated user (e.g. "Microsoft.IdentityModel.Claims.ClaimsIdentity;...")
    /// fetched from the Connection Data API during repo creation.
    /// </summary>
    private string? _authenticatedUserDescriptor;

    /// <summary>
    /// The identity ID of the PAT-authenticated user.
    /// </summary>
    private string? _authenticatedUserId;

    // ── Permission bit constants for Git Repositories security namespace ──
    private const string GitRepoSecurityNamespaceId = "2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87";
    private const int DeleteRepositoryBit = 512;  // Bit 10 in the enumeration

    /// <summary>The ID of the created repo (GUID).</summary>
    public string? RepositoryId { get; private set; }

    /// <summary>The name of the disposable test repository.</summary>
    public string? RepositoryName { get; private set; }

    /// <summary>The PR ID within the disposable repo.</summary>
    public int PullRequestId { get; private set; }

    /// <summary>The source branch of the PR.</summary>
    public string BranchName => TestBranchName;

    /// <summary>When true, DisposeAsync will NOT delete the repo (for manual inspection).</summary>
    public bool SkipCleanupOnDispose { get; set; }

    public TestPullRequestHelper(string org, string pat, string project)
    {
        _org = org;
        _project = project;

        _http = new HttpClient();
        var creds = Convert.ToBase64String(Encoding.ASCII.GetBytes($":{pat}"));
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", creds);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private string BaseUrl
    {
        get
        {
            if (string.IsNullOrEmpty(RepositoryName))
                throw new InvalidOperationException("Repository has not been created yet. Call CreateDraftPullRequestAsync first.");
            return $"https://dev.azure.com/{_org}/{_project}/_apis/git/repositories/{RepositoryName}";
        }
    }

    private string OrgUrl => $"https://dev.azure.com/{_org}/{_project}/_apis/git";

    // ═══════════════════════════════════════════════════════════════════════
    //  Setup — create disposable repo + branch + PR
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a brand-new disposable repo, pushes an initial file to "main",
    /// creates a test branch with a change, and opens a draft PR.
    /// </summary>
    public async Task<int> CreateDraftPullRequestAsync()
    {
        // 1. Create the disposable repo
        RepositoryName = $"AiCodeReview-IntTest-{Guid.NewGuid().ToString("N")[..8]}";
        _instanceMarkerToken = Guid.NewGuid().ToString("N");
        Console.WriteLine($"  [TestHelper] Creating disposable repo: {RepositoryName}");

        var createBody = JsonSerializer.Serialize(new { name = RepositoryName });
        var createResp = await _http.PostAsync(
            $"{OrgUrl}/repositories?{ApiVersion}",
            new StringContent(createBody, Encoding.UTF8, "application/json"));
        createResp.EnsureSuccessStatusCode();

        var repoJson = await createResp.Content.ReadFromJsonAsync<JsonElement>();
        RepositoryId = repoJson.GetProperty("id").GetString()!;
        _createdByThisInstance = true;

        // Capture project ID from repo response for security token construction
        if (repoJson.TryGetProperty("project", out var projectEl) &&
            projectEl.TryGetProperty("id", out var pidEl))
        {
            _projectId = pidEl.GetString();
        }

        Console.WriteLine($"  [TestHelper] Repo created: {RepositoryId} (project: {_projectId})");

        // Fetch the PAT-authenticated user's identity once (used by safety check 6)
        await CaptureAuthenticatedUserIdentityAsync();

        // 2. Push initial commit to create "main" branch (includes safety marker file)
        var markerContent = JsonSerializer.Serialize(new
        {
            magic = SafetyMagicString,
            instanceToken = _instanceMarkerToken,
            createdAtUtc = DateTime.UtcNow.ToString("O"),
            repoName = RepositoryName,
            repoId = RepositoryId,
            purpose = "Disposable integration test repo created by AiCodeReview test suite. Safe to delete.",
        }, new JsonSerializerOptions { WriteIndented = true });

        var initialContent =
            "// Auto-generated test file for AiCodeReview integration tests.\n" +
            $"// Created at {DateTime.UtcNow:O}\n" +
            "using System;\n" +
            "namespace TestNamespace\n" +
            "{\n" +
            "    public class TestClass\n" +
            "    {\n" +
            "        public string Name { get; set; }\n" +
            "        public int Value { get; set; }\n" +
            "        public void DoWork()\n" +
            "        {\n" +
            "            Console.WriteLine(\"Hello, World!\");\n" +
            "        }\n" +
            "    }\n" +
            "}\n";

        var mainPush = new
        {
            refUpdates = new[]
            {
                new { name = "refs/heads/main", oldObjectId = "0000000000000000000000000000000000000000" }
            },
            commits = new[]
            {
                new
                {
                    comment = "[TEST] Initial commit — create main branch with safety marker",
                    changes = new object[]
                    {
                        new
                        {
                            changeType = "add",
                            item = new { path = "/test-files/initial-test-file.txt" },
                            newContent = new { content = initialContent, contentType = "rawtext" },
                        },
                        new
                        {
                            changeType = "add",
                            item = new { path = "/.test-repo-marker.json" },
                            newContent = new { content = markerContent, contentType = "rawtext" },
                        }
                    }
                }
            }
        };

        var mainPushJson = JsonSerializer.Serialize(mainPush, SerializerOptions);
        var mainPushResp = await _http.PostAsync(
            $"{BaseUrl}/pushes?{ApiVersion}",
            new StringContent(mainPushJson, Encoding.UTF8, "application/json"));
        mainPushResp.EnsureSuccessStatusCode();

        // 3. Get the main branch commit to use as base for test branch
        var mainCommit = await GetLatestCommitAsync("refs/heads/main");

        // 4. Create test branch with an additional file (so there's a diff)
        var testFileContent =
            "// Test change file — triggers a diff for the PR.\n" +
            $"// Created at {DateTime.UtcNow:O}\n" +
            "namespace TestNamespace\n" +
            "{\n" +
            "    public class TestChange\n" +
            "    {\n" +
            "        public string Description => \"Initial test change\";\n" +
            "    }\n" +
            "}\n";

        var branchPush = new
        {
            refUpdates = new[]
            {
                new { name = TestBranchName, oldObjectId = mainCommit }
            },
            commits = new[]
            {
                new
                {
                    comment = "[TEST] Add test change file on test branch",
                    changes = new[]
                    {
                        new
                        {
                            changeType = "add",
                            item = new { path = "/test-files/test-change.txt" },
                            newContent = new { content = testFileContent, contentType = "rawtext" },
                        }
                    }
                }
            }
        };

        var branchPushJson = JsonSerializer.Serialize(branchPush, SerializerOptions);
        var branchPushResp = await _http.PostAsync(
            $"{BaseUrl}/pushes?{ApiVersion}",
            new StringContent(branchPushJson, Encoding.UTF8, "application/json"));
        branchPushResp.EnsureSuccessStatusCode();

        // 5. Create draft PR
        var prBody = new
        {
            sourceRefName = TestBranchName,
            targetRefName = "refs/heads/main",
            title = "[TEST] AiCodeReview Integration Test PR — DO NOT MERGE",
            description = "Auto-generated PR in a disposable repository. The entire repo will be deleted after tests.",
            isDraft = true,
        };

        var prJson = JsonSerializer.Serialize(prBody);
        var prResp = await _http.PostAsync(
            $"{BaseUrl}/pullrequests?{ApiVersion}",
            new StringContent(prJson, Encoding.UTF8, "application/json"));
        prResp.EnsureSuccessStatusCode();

        var prResult = await prResp.Content.ReadFromJsonAsync<JsonElement>();
        PullRequestId = prResult.GetProperty("pullRequestId").GetInt32();
        Console.WriteLine($"  [TestHelper] Created PR #{PullRequestId} in repo {RepositoryName}");

        return PullRequestId;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  PR operations (same public API as before)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Push a new file to the test branch (creates a new commit / iteration).
    /// </summary>
    public async Task PushNewCommitAsync(string fileName, string content)
    {
        var latestCommit = await GetLatestCommitAsync(TestBranchName);
        await PushFileAsync(latestCommit, fileName, content);
    }

    /// <summary>
    /// Set the PR draft status (true/false).
    /// </summary>
    public async Task SetDraftStatusAsync(bool isDraft)
    {
        var body = JsonSerializer.Serialize(new { isDraft });
        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"{BaseUrl}/pullrequests/{PullRequestId}?{ApiVersion}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        var resp = await _http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Clear all AiCodeReview.* PR properties (reset metadata).
    /// </summary>
    public async Task ClearReviewMetadataAsync()
    {
        var getResp = await _http.GetAsync(
            $"{BaseUrl}/pullrequests/{PullRequestId}/properties?api-version=7.1-preview.1");
        getResp.EnsureSuccessStatusCode();
        var json = await getResp.Content.ReadFromJsonAsync<JsonElement>();

        if (!json.TryGetProperty("value", out var props))
            return;

        var ops = new List<object>();
        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Name.StartsWith("AiCodeReview."))
            {
                ops.Add(new { op = "remove", path = $"/{prop.Name}" });
            }
        }

        if (ops.Count == 0) return;

        var patchJson = JsonSerializer.Serialize(ops);
        var request = new HttpRequestMessage(HttpMethod.Patch,
            $"{BaseUrl}/pullrequests/{PullRequestId}/properties?api-version=7.1-preview.1")
        {
            Content = new StringContent(patchJson, Encoding.UTF8, "application/json-patch+json"),
        };
        var resp = await _http.SendAsync(request);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Remove the ai-code-review tag if present.
    /// </summary>
    public async Task RemoveReviewTagAsync()
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/pullrequests/{PullRequestId}/labels?{ApiVersion}");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        foreach (var label in json.GetProperty("value").EnumerateArray())
        {
            var name = label.GetProperty("name").GetString() ?? "";
            if (name.Equals("ai-code-review", StringComparison.OrdinalIgnoreCase))
            {
                var id = label.GetProperty("id").GetString();
                await _http.DeleteAsync(
                    $"{BaseUrl}/pullrequests/{PullRequestId}/labels/{id}?{ApiVersion}");
                break;
            }
        }
    }

    /// <summary>
    /// Get PR properties (for test assertions).
    /// </summary>
    public async Task<Dictionary<string, string>> GetReviewPropertiesAsync()
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/pullrequests/{PullRequestId}/properties?api-version=7.1-preview.1");
        resp.EnsureSuccessStatusCode();

        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var result = new Dictionary<string, string>();

        if (!json.TryGetProperty("value", out var props)) return result;

        foreach (var prop in props.EnumerateObject())
        {
            if (prop.Name.StartsWith("AiCodeReview."))
            {
                var val = prop.Value.TryGetProperty("$value", out var v) ? v.GetString() ?? "" : "";
                result[prop.Name] = val;
            }
        }
        return result;
    }

    /// <summary>
    /// Get all comment threads on the PR (for assertions).
    /// </summary>
    public async Task<List<JsonElement>> GetThreadsAsync()
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/pullrequests/{PullRequestId}/threads?{ApiVersion}");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("value").EnumerateArray().ToList();
    }

    /// <summary>
    /// Get labels on the PR (for assertions).
    /// </summary>
    public async Task<List<string>> GetLabelsAsync()
    {
        var resp = await _http.GetAsync(
            $"{BaseUrl}/pullrequests/{PullRequestId}/labels?{ApiVersion}");
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        return json.GetProperty("value")
            .EnumerateArray()
            .Select(l => l.GetProperty("name").GetString() ?? "")
            .ToList();
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Teardown — delete the entire disposable repo (with safety checks)
    // ═══════════════════════════════════════════════════════════════════════

    public async ValueTask DisposeAsync()
    {
        if (SkipCleanupOnDispose || string.IsNullOrEmpty(RepositoryId))
        {
            _http.Dispose();
            return;
        }

        try
        {
            // ── SAFETY CHECK 1: Was the repo created by THIS instance? ──
            if (!_createdByThisInstance)
            {
                Console.WriteLine($"  [SAFETY] BLOCKED: Repo {RepositoryName} was NOT created by this test instance. Skipping delete.");
                return;
            }

            // ── SAFETY CHECK 2: Is the repo in the never-delete list? ──
            if (!string.IsNullOrEmpty(RepositoryName) && NeverDeleteRepoNames.Contains(RepositoryName))
            {
                Console.WriteLine($"  [SAFETY] BLOCKED: Repo '{RepositoryName}' is in the never-delete list. Skipping delete.");
                return;
            }

            // ── SAFETY CHECK 3: Does the repo name have our test prefix? ──
            if (string.IsNullOrEmpty(RepositoryName) || !RepositoryName.StartsWith(RepoNamePrefix, StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"  [SAFETY] BLOCKED: Repo name '{RepositoryName}' does not start with '{RepoNamePrefix}'. Skipping delete.");
                return;
            }

            // ── SAFETY CHECK 4: Does the marker file exist with the correct magic string? ──
            var markerVerified = await VerifyMarkerFileAsync();
            if (!markerVerified)
            {
                Console.WriteLine($"  [SAFETY] BLOCKED: Marker file verification failed for repo {RepositoryName}. Skipping delete.");
                return;
            }

            // ── SAFETY CHECK 5: Was the repo created recently (within 2 hours)? ──
            var createdRecently = await VerifyRepoCreatedRecentlyAsync(TimeSpan.FromHours(2));
            if (!createdRecently)
            {
                Console.WriteLine($"  [SAFETY] BLOCKED: Repo {RepositoryName} was not created recently. Skipping delete.");
                return;
            }

            // ── SAFETY CHECK 6: Verify PAT identity has explicit Delete permission on this repo ──
            var hasDeletePermission = await VerifyIdentityAndDeletePermissionAsync();
            if (!hasDeletePermission)
            {
                Console.WriteLine($"  [SAFETY] BLOCKED: PAT user does not have explicit Delete permission on repo {RepositoryName}. Skipping delete.");
                return;
            }

            Console.WriteLine($"  [TestHelper] All 6 safety checks passed. Deleting disposable repo {RepositoryName} ({RepositoryId})...");

            // Step 1: Soft-delete the repository
            var deleteResp = await _http.DeleteAsync(
                $"{OrgUrl}/repositories/{RepositoryId}?{ApiVersion}");

            if (deleteResp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  [TestHelper] Repo soft-deleted.");

                // Step 2: Hard-delete from recycle bin (permanent removal)
                await Task.Delay(1000); // Brief pause for ADO to process
                var purgeResp = await _http.DeleteAsync(
                    $"https://dev.azure.com/{_org}/{_project}/_apis/git/recycleBin/repositories/{RepositoryId}?{ApiVersion}");

                Console.WriteLine(purgeResp.IsSuccessStatusCode
                    ? "  [TestHelper] Repo permanently purged from recycle bin."
                    : $"  [TestHelper] Recycle bin purge returned {(int)purgeResp.StatusCode} (repo is soft-deleted and will auto-purge).");
            }
            else
            {
                Console.WriteLine($"  [TestHelper] Repo delete returned {(int)deleteResp.StatusCode}. It may have already been deleted.");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [TestHelper] Cleanup warning for repo {RepositoryName}: {ex.Message}");
        }
        finally
        {
            _http.Dispose();
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Safety verification methods
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Reads the /.test-repo-marker.json file from the repo and verifies it
    /// contains the expected magic string and was created by this instance.
    /// </summary>
    private async Task<bool> VerifyMarkerFileAsync()
    {
        try
        {
            // Must use includeContent=true because Accept: application/json causes the Items API
            // to return item metadata instead of raw content. The actual file content will be in
            // the "content" property of the response.
            var url = $"{BaseUrl}/items?path=/.test-repo-marker.json&includeContent=true&{ApiVersion}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  [SAFETY] Marker file not found in repo (HTTP {(int)resp.StatusCode}).");
                return false;
            }

            var responseText = await resp.Content.ReadAsStringAsync();
            var responseJson = JsonSerializer.Deserialize<JsonElement>(responseText);

            // The actual file content is inside the "content" property of the Items API response
            if (!responseJson.TryGetProperty("content", out var contentElement))
            {
                Console.WriteLine($"  [SAFETY] Items API response has no 'content' property.");
                return false;
            }

            var markerText = contentElement.GetString();
            if (string.IsNullOrEmpty(markerText))
            {
                Console.WriteLine($"  [SAFETY] Marker file content is empty.");
                return false;
            }

            var marker = JsonSerializer.Deserialize<JsonElement>(markerText);

            // Check magic string
            var magic = marker.TryGetProperty("magic", out var m) ? m.GetString() : null;
            if (magic != SafetyMagicString)
            {
                Console.WriteLine($"  [SAFETY] Marker file magic string mismatch. Expected: '{SafetyMagicString}', Got: '{magic}'.");
                return false;
            }

            // Check instance token matches
            var token = marker.TryGetProperty("instanceToken", out var t) ? t.GetString() : null;
            if (token != _instanceMarkerToken)
            {
                Console.WriteLine($"  [SAFETY] Marker instance token mismatch. This instance did not create this repo.");
                return false;
            }

            // Check repo ID matches
            var markerId = marker.TryGetProperty("repoId", out var rid) ? rid.GetString() : null;
            if (markerId != RepositoryId)
            {
                Console.WriteLine($"  [SAFETY] Marker repoId mismatch. Expected: {RepositoryId}, Got: {markerId}.");
                return false;
            }

            Console.WriteLine($"  [SAFETY] ✓ Marker file verified: magic string, instance token, and repo ID all match.");
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [SAFETY] Marker file verification error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Checks that the repo was created within the specified time window.
    /// Prevents deletion of repos that have been around too long.
    /// </summary>
    private async Task<bool> VerifyRepoCreatedRecentlyAsync(TimeSpan maxAge)
    {
        try
        {
            var url = $"{OrgUrl}/repositories/{RepositoryId}?{ApiVersion}";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode) return false;

            var repo = await resp.Content.ReadFromJsonAsync<JsonElement>();

            // The marker file has the creation timestamp — use that as secondary check
            // ADO repo API doesn't expose createdDate directly, but we can check the
            // first commit's timestamp
            var commitsUrl = $"{BaseUrl}/commits?$top=1&searchCriteria.itemVersion.version=main&{ApiVersion}";
            var commitsResp = await _http.GetAsync(commitsUrl);
            if (!commitsResp.IsSuccessStatusCode) return false;

            var commitsJson = await commitsResp.Content.ReadFromJsonAsync<JsonElement>();
            var commits = commitsJson.GetProperty("value");
            if (commits.GetArrayLength() == 0) return false;

            var firstCommit = commits[0];
            if (firstCommit.TryGetProperty("author", out var author) &&
                author.TryGetProperty("date", out var dateStr))
            {
                if (DateTime.TryParse(dateStr.GetString(), out var createdAt))
                {
                    var age = DateTime.UtcNow - createdAt.ToUniversalTime();
                    if (age > maxAge)
                    {
                        Console.WriteLine($"  [SAFETY] Repo is {age.TotalMinutes:F0} minutes old (max: {maxAge.TotalMinutes:F0}). Too old to auto-delete.");
                        return false;
                    }
                    Console.WriteLine($"  [SAFETY] ✓ Repo age: {age.TotalMinutes:F0} minutes (within {maxAge.TotalMinutes:F0} minute limit).");
                    return true;
                }
            }

            Console.WriteLine($"  [SAFETY] Could not determine repo creation time. Denying delete.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [SAFETY] Age verification error: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Fetches the authenticated user's identity descriptor and ID from the
    /// Connection Data API. Called once during repo creation.
    /// </summary>
    private async Task CaptureAuthenticatedUserIdentityAsync()
    {
        try
        {
            var url = $"https://dev.azure.com/{_org}/_apis/connectionData";
            var resp = await _http.GetAsync(url);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  [TestHelper] Connection Data API returned {(int)resp.StatusCode}. Identity check will be skipped.");
                return;
            }

            var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (json.TryGetProperty("authenticatedUser", out var authUser))
            {
                _authenticatedUserId = authUser.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                _authenticatedUserDescriptor = authUser.TryGetProperty("descriptor", out var descEl) ? descEl.GetString() : null;
                Console.WriteLine($"  [TestHelper] PAT identity captured: {_authenticatedUserId} (descriptor: {_authenticatedUserDescriptor?[..Math.Min(40, _authenticatedUserDescriptor?.Length ?? 0)]}...)");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [TestHelper] Warning: Could not capture PAT identity: {ex.Message}");
        }
    }

    /// <summary>
    /// Safety check 6: Verifies the PAT-authenticated user has explicit Delete permission
    /// on this specific repository by querying the ACLs for the Git Repositories
    /// security namespace (2e9eb7ed-3c0a-47d4-87c1-0ffdd275fd87).
    ///
    /// Token format: repoV2/{projectId}/{repoId}
    ///
    /// This also serves as an ownership heuristic — in most orgs, the only individual
    /// user with explicit "Allow" on a repo is its creator.
    /// </summary>
    private async Task<bool> VerifyIdentityAndDeletePermissionAsync()
    {
        try
        {
            // Must have both projectId and the user descriptor from earlier
            if (string.IsNullOrEmpty(_projectId) || string.IsNullOrEmpty(_authenticatedUserDescriptor))
            {
                Console.WriteLine($"  [SAFETY] Cannot verify permissions: missing projectId or user descriptor. Denying delete.");
                return false;
            }

            // Build the security token for this specific repo
            var securityToken = $"repoV2/{_projectId}/{RepositoryId}";
            var encodedToken = Uri.EscapeDataString(securityToken);

            // Query ACLs for this repo in the Git Repositories namespace
            var aclUrl = $"https://dev.azure.com/{_org}/_apis/accesscontrollists/{GitRepoSecurityNamespaceId}" +
                         $"?token={encodedToken}&includeExtendedInfo=true&{ApiVersion}";
            var resp = await _http.GetAsync(aclUrl);
            if (!resp.IsSuccessStatusCode)
            {
                Console.WriteLine($"  [SAFETY] ACL query returned {(int)resp.StatusCode}. Cannot verify permissions.");
                return false;
            }

            var aclJson = await resp.Content.ReadFromJsonAsync<JsonElement>();
            if (!aclJson.TryGetProperty("value", out var aclList) || aclList.GetArrayLength() == 0)
            {
                Console.WriteLine($"  [SAFETY] No ACLs found for repo token {securityToken}.");
                return false;
            }

            // Search through ACEs for our user's descriptor
            foreach (var acl in aclList.EnumerateArray())
            {
                if (!acl.TryGetProperty("acesDictionary", out var aces))
                    continue;

                foreach (var ace in aces.EnumerateObject())
                {
                    // The key is the identity descriptor
                    var aceDescriptor = ace.Name;

                    // Match against our authenticated user's descriptor
                    if (!string.Equals(aceDescriptor, _authenticatedUserDescriptor, StringComparison.OrdinalIgnoreCase))
                        continue;

                    var aceValue = ace.Value;

                    // Check for explicit allow (not just inherited)
                    var allow = aceValue.TryGetProperty("resolvedPermissions", out var resolved)
                        ? (resolved.TryGetProperty("effectiveAllow", out var ea) ? ea.GetInt32() : 0)
                        : (aceValue.TryGetProperty("allow", out var a) ? a.GetInt32() : 0);

                    var deny = aceValue.TryGetProperty("resolvedPermissions", out var resDeny)
                        ? (resDeny.TryGetProperty("effectiveDeny", out var ed) ? ed.GetInt32() : 0)
                        : (aceValue.TryGetProperty("deny", out var d) ? d.GetInt32() : 0);

                    // Also check extendedInfo for explicit vs inherited
                    var isExplicit = false;
                    if (aceValue.TryGetProperty("extendedInfo", out var extInfo))
                    {
                        // effectiveAllow includes both explicit + inherited
                        var effectiveAllow = extInfo.TryGetProperty("effectiveAllow", out var effA) ? effA.GetInt32() : allow;
                        var inheritedAllow = extInfo.TryGetProperty("inheritedAllow", out var inhA) ? inhA.GetInt32() : 0;

                        // Explicit = effective minus inherited
                        var explicitAllow = effectiveAllow & ~inheritedAllow;
                        isExplicit = (explicitAllow & DeleteRepositoryBit) != 0;

                        allow = effectiveAllow;
                        deny = extInfo.TryGetProperty("effectiveDeny", out var effD) ? effD.GetInt32() : deny;
                    }

                    var hasDelete = (allow & DeleteRepositoryBit) != 0 && (deny & DeleteRepositoryBit) == 0;

                    if (hasDelete)
                    {
                        Console.WriteLine($"  [SAFETY] ✓ PAT user has Delete permission on repo" +
                                          $" (explicit: {isExplicit}, allow bits: {allow}, deny bits: {deny}).");
                        return true;
                    }
                    else
                    {
                        Console.WriteLine($"  [SAFETY] PAT user ACE found but Delete bit not set" +
                                          $" (allow: {allow}, deny: {deny}).");
                        return false;
                    }
                }
            }

            // The user descriptor was not found in any ACE — they may only have inherited permissions.
            // For safety, we also check if they can actually call the delete API by looking at
            // project-level ACLs (the creator typically has explicit repo-level Allow).
            Console.WriteLine($"  [SAFETY] PAT user descriptor not found in repo-level ACLs. " +
                              $"Checking project-level fallback...");

            // Fallback: check project-level Git permissions
            var projectToken = $"repoV2/{_projectId}";
            var projectAclUrl = $"https://dev.azure.com/{_org}/_apis/accesscontrollists/{GitRepoSecurityNamespaceId}" +
                                $"?token={Uri.EscapeDataString(projectToken)}&includeExtendedInfo=true&{ApiVersion}";
            var projResp = await _http.GetAsync(projectAclUrl);
            if (projResp.IsSuccessStatusCode)
            {
                var projAcl = await projResp.Content.ReadFromJsonAsync<JsonElement>();
                if (projAcl.TryGetProperty("value", out var projAclList))
                {
                    foreach (var acl in projAclList.EnumerateArray())
                    {
                        if (!acl.TryGetProperty("acesDictionary", out var aces))
                            continue;

                        foreach (var ace in aces.EnumerateObject())
                        {
                            if (!string.Equals(ace.Name, _authenticatedUserDescriptor, StringComparison.OrdinalIgnoreCase))
                                continue;

                            var aceVal = ace.Value;
                            var projAllow = 0;
                            var projDeny = 0;
                            if (aceVal.TryGetProperty("extendedInfo", out var ext))
                            {
                                projAllow = ext.TryGetProperty("effectiveAllow", out var pa) ? pa.GetInt32() : 0;
                                projDeny = ext.TryGetProperty("effectiveDeny", out var pd) ? pd.GetInt32() : 0;
                            }
                            else
                            {
                                projAllow = aceVal.TryGetProperty("allow", out var pa2) ? pa2.GetInt32() : 0;
                                projDeny = aceVal.TryGetProperty("deny", out var pd2) ? pd2.GetInt32() : 0;
                            }

                            var hasProjectDelete = (projAllow & DeleteRepositoryBit) != 0 && (projDeny & DeleteRepositoryBit) == 0;
                            if (hasProjectDelete)
                            {
                                Console.WriteLine($"  [SAFETY] ✓ PAT user has Delete permission at project level" +
                                                  $" (allow: {projAllow}, deny: {projDeny}).");
                                return true;
                            }
                        }
                    }
                }
            }

            Console.WriteLine($"  [SAFETY] PAT user does NOT have Delete permission for this repo.");
            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [SAFETY] Permission verification error: {ex.Message}");
            return false;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Private helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private async Task<string> GetLatestCommitAsync(string refName)
    {
        var filter = Uri.EscapeDataString(refName.Replace("refs/", ""));
        var url = $"{BaseUrl}/refs?filter={filter}&{ApiVersion}";
        var resp = await _http.GetAsync(url);
        resp.EnsureSuccessStatusCode();
        var json = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var refs = json.GetProperty("value");
        if (refs.GetArrayLength() == 0)
            throw new InvalidOperationException($"Ref '{refName}' not found in repo {RepositoryName}.");
        return refs[0].GetProperty("objectId").GetString()!;
    }

    private async Task PushFileAsync(string baseCommit, string fileName, string content)
    {
        var pushBody = new
        {
            refUpdates = new[]
            {
                new { name = TestBranchName, oldObjectId = baseCommit }
            },
            commits = new[]
            {
                new
                {
                    comment = $"[TEST] Add {fileName} for integration testing",
                    changes = new[]
                    {
                        new
                        {
                            changeType = "add",
                            item = new { path = $"/test-files/{fileName}" },
                            newContent = new { content, contentType = "rawtext" },
                        }
                    }
                }
            }
        };

        var json = JsonSerializer.Serialize(pushBody, SerializerOptions);
        var resp = await _http.PostAsync(
            $"{BaseUrl}/pushes?{ApiVersion}",
            new StringContent(json, Encoding.UTF8, "application/json"));

        if (!resp.IsSuccessStatusCode)
        {
            var error = await resp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Push failed: {(int)resp.StatusCode} — {error}");
        }
    }
}
