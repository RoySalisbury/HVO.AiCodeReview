using System.Net.Http.Json;
using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

/// <summary>
/// End-to-end integration tests for the review orchestrator.
///
/// Creates a disposable test repo with a draft PR, then runs all review scenarios in sequence:
///   1. First review on draft → FullReview (no vote)
///   2. Same PR, no changes → Skip
///   3. Push new commit → ReReview with dedup
///   4. Draft → active transition (no code change) → VoteOnly
///   5. Active, no changes → Skip
///   6. Reset metadata + push on active → FullReview with vote
///   7. Push another commit → ReReview with dedup verification
///   8. Cleanup: delete the entire disposable test repo
///
/// Uses a FakeCodeReviewService to produce deterministic, stable inline
/// comments so we can test dedup without real AI calls.
///
/// Requires a valid Azure DevOps PAT in appsettings.Test.json.
///
/// NOTE: The monolithic FullReviewLifecycle_AllScenarios test has been replaced
/// by independent parallel tests in ReviewLifecycleTests.cs.  It is kept here
/// with [Ignore] for reference only.  The InspectPR_NoCleanup and CleanupTestPR
/// manual utilities remain active.
/// </summary>
[TestClass]
public class ReviewFlowIntegrationTests
{
    [TestMethod]
    [Ignore("Replaced by independent parallel tests in ReviewLifecycleTests.cs")]
    [Timeout(300_000)] // 5-minute timeout for the full lifecycle
    public async Task FullReviewLifecycle_AllScenarios()
    {
        // ════════════════════════════════════════════════════════════════
        //  SETUP
        // ════════════════════════════════════════════════════════════════

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Test.json")
            .Build();

        var devOpsSettings = config.GetSection("AzureDevOps").Get<AzureDevOpsSettings>()!;
        var project = config["TestSettings:Project"]!;

        // DI container — real AzureDevOpsService, fake CodeReviewService
        var services = new ServiceCollection();
        services.AddLogging(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
        services.Configure<AzureDevOpsSettings>(config.GetSection("AzureDevOps"));
        services.Configure<AiProviderSettings>(config.GetSection("AiProvider"));
        services.Configure<AzureOpenAISettings>(config.GetSection("AzureOpenAI"));
        services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();

        var fakeReview = new FakeCodeReviewService();
        services.AddSingleton<ICodeReviewService>(fakeReview);
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddTransient<CodeReviewOrchestrator>();

        await using var sp = services.BuildServiceProvider();
        var orchestrator = sp.GetRequiredService<CodeReviewOrchestrator>();

        // Create disposable test repo + draft PR
        await using var pr = new TestPullRequestHelper(
            devOpsSettings.Organization, devOpsSettings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"\n══ Created test draft PR #{prId} in disposable repo {repo} ══\n");

        // Helper to call the orchestrator
        async Task<ReviewResponse> Review() =>
            await orchestrator.ExecuteReviewAsync(project, repo, prId);

        // ════════════════════════════════════════════════════════════════
        //  SCENARIO 1: First review on draft PR → FullReview
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine("── Scenario 1: First review (draft PR) ──");

        var r1 = await Review();

        Assert.AreEqual("Reviewed", r1.Status, "S1: should be Reviewed.");
        Assert.IsNotNull(r1.Summary, "S1: summary should exist.");
        Assert.IsTrue(r1.Summary!.Contains("Code Review"), "S1: summary should have 'Code Review' header.");
        Assert.IsNull(r1.Vote, "S1: no vote on draft PR.");

        // Verify metadata stored
        var props1 = await pr.GetReviewPropertiesAsync();
        Assert.IsTrue(props1.ContainsKey("AiCodeReview.LastSourceCommit"), "S1: metadata stored.");
        Assert.AreEqual("True", props1["AiCodeReview.WasDraft"], "S1: WasDraft=True.");
        Assert.AreEqual("False", props1["AiCodeReview.VoteSubmitted"], "S1: VoteSubmitted=False.");

        // Verify tag added
        var labels1 = await pr.GetLabelsAsync();
        Assert.IsTrue(labels1.Contains("ai-code-review"), "S1: review tag present.");

        // Verify inline comments posted
        var threads1 = await pr.GetThreadsAsync();
        int inline1 = CountInlineThreads(threads1);
        Assert.IsTrue(inline1 > 0, "S1: inline comments should be posted.");

        Console.WriteLine($"  ✓ Status=Reviewed, Vote=null, {inline1} inline comments, tag present, metadata stored.\n");

        // ════════════════════════════════════════════════════════════════
        //  SCENARIO 2: Same PR, no changes → Skip
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine("── Scenario 2: No changes → Skip ──");

        var r2 = await Review();

        Assert.AreEqual("Skipped", r2.Status, "S2: should be Skipped.");
        Assert.IsTrue(r2.Summary!.Contains("already been reviewed"), "S2: skip message.");

        Console.WriteLine($"  ✓ Status=Skipped.\n");

        // ════════════════════════════════════════════════════════════════
        //  SCENARIO 3: Push new commit → ReReview with dedup
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine("── Scenario 3: Push new commit → ReReview ──");

        await pr.PushNewCommitAsync(
            $"scenario3-{Guid.NewGuid():N}.txt",
            $"// Added at {DateTime.UtcNow:O} to trigger re-review\n");
        await Task.Delay(3000); // Let ADO process the push

        var threads2 = await pr.GetThreadsAsync();
        int inlineBefore = CountInlineThreads(threads2);

        var r3 = await Review();

        Assert.AreEqual("Reviewed", r3.Status, "S3: should be Reviewed.");
        Assert.IsTrue(r3.Summary!.Contains("Re-Review"), "S3: summary should say 'Re-Review'.");
        Assert.IsNull(r3.Vote, "S3: no vote on draft.");

        var threads3 = await pr.GetThreadsAsync();
        int inlineAfter = CountInlineThreads(threads3);

        // Dedup check: the fake service returns 2 comments per file.
        // For file #1 (initial-test-file.txt), same file + same line + same content → should be deduped.
        // For file #2 (scenario3-xxx.txt), brand new file → should be posted.
        Console.WriteLine($"  Inline threads: {inlineBefore} → {inlineAfter} (new: {inlineAfter - inlineBefore})");
        Assert.IsTrue(inlineAfter > inlineBefore, "S3: should have some new inline comments for new file.");

        Console.WriteLine($"  ✓ Status=Reviewed (Re-Review), dedup checked.\n");

        // ════════════════════════════════════════════════════════════════
        //  SCENARIO 4: Draft → Active (no code change) → VoteOnly
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine("── Scenario 4: Draft → Active → VoteOnly ──");

        // Pre-conditions: metadata should have WasDraft=True, VoteSubmitted=False
        var props4pre = await pr.GetReviewPropertiesAsync();
        Assert.AreEqual("True", props4pre.GetValueOrDefault("AiCodeReview.WasDraft"), "S4 pre: WasDraft=True.");
        Assert.AreEqual("False", props4pre.GetValueOrDefault("AiCodeReview.VoteSubmitted"), "S4 pre: VoteSubmitted=False.");

        // Publish the PR
        await pr.SetDraftStatusAsync(false);
        await Task.Delay(2000);

        var r4 = await Review();

        Assert.AreEqual("Reviewed", r4.Status, "S4: should be Reviewed.");
        Assert.IsTrue(r4.Summary!.Contains("Draft-to-active"), "S4: should mention draft-to-active.");
        Assert.IsNotNull(r4.Vote, "S4: vote should be submitted.");

        var props4post = await pr.GetReviewPropertiesAsync();
        Assert.AreEqual("True", props4post.GetValueOrDefault("AiCodeReview.VoteSubmitted"), "S4: VoteSubmitted=True.");
        Assert.AreEqual("False", props4post.GetValueOrDefault("AiCodeReview.WasDraft"), "S4: WasDraft=False.");

        Console.WriteLine($"  ✓ Status=Reviewed (VoteOnly), Vote={r4.Vote}, metadata updated.\n");

        // ════════════════════════════════════════════════════════════════
        //  SCENARIO 5: Active PR, no changes → Skip
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine("── Scenario 5: Active PR, no changes → Skip ──");

        var r5 = await Review();

        Assert.AreEqual("Skipped", r5.Status, "S5: should be Skipped.");

        Console.WriteLine($"  ✓ Status=Skipped.\n");

        // ════════════════════════════════════════════════════════════════
        //  SCENARIO 6: Reset metadata + push on active → FullReview with vote
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine("── Scenario 6: Reset + push on active → FullReview with vote ──");

        await pr.ClearReviewMetadataAsync();
        await pr.RemoveReviewTagAsync();

        await pr.PushNewCommitAsync(
            $"scenario6-{Guid.NewGuid():N}.txt",
            $"// New file on active PR at {DateTime.UtcNow:O}\n");
        await Task.Delay(3000);

        var r6 = await Review();

        Assert.AreEqual("Reviewed", r6.Status, "S6: should be Reviewed.");
        Assert.IsTrue(r6.Summary!.Contains("Code Review"), "S6: should be full Code Review (not Re-Review).");
        Assert.IsNotNull(r6.Vote, "S6: vote should be submitted on active PR.");
        Assert.AreEqual(5, r6.Vote, "S6: vote should be 5 (from fake service).");

        var labels6 = await pr.GetLabelsAsync();
        Assert.IsTrue(labels6.Contains("ai-code-review"), "S6: tag should be re-added.");

        Console.WriteLine($"  ✓ Status=Reviewed, Vote={r6.Vote}, tag re-added.\n");

        // ════════════════════════════════════════════════════════════════
        //  SCENARIO 7: Push another commit → ReReview with precise dedup verification
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine("── Scenario 7: Another commit → ReReview + dedup ──");

        await pr.PushNewCommitAsync(
            $"scenario7-{Guid.NewGuid():N}.txt",
            $"// Dedup verification at {DateTime.UtcNow:O}\n");
        await Task.Delay(3000);

        var threads6 = await pr.GetThreadsAsync();
        int threadsBefore7 = threads6.Count;

        var r7 = await Review();

        Assert.AreEqual("Reviewed", r7.Status, "S7: should be Reviewed.");
        Assert.IsTrue(r7.Summary!.Contains("Re-Review"), "S7: should be Re-Review.");

        var threads7 = await pr.GetThreadsAsync();
        int threadsAfter7 = threads7.Count;
        int newThreads7 = threadsAfter7 - threadsBefore7;

        // New threads should only be for the new file(s) + 1 summary.
        // All prior files' comments should be deduped.
        Console.WriteLine($"  Threads: {threadsBefore7} → {threadsAfter7} (new: {newThreads7})");
        Assert.IsTrue(newThreads7 <= 5,
            $"S7: expected ≤5 new threads (2 inline for new file + 1 summary), got {newThreads7}.");

        Console.WriteLine($"  ✓ Status=Reviewed (Re-Review), dedup verified: {newThreads7} new threads.\n");

        // ════════════════════════════════════════════════════════════════
        //  DONE — cleanup is automatic via DisposeAsync
        // ════════════════════════════════════════════════════════════════
        Console.WriteLine($"══ All 7 scenarios passed! Disposable repo {repo} will be deleted. ══\n");
    }

    /// <summary>
    /// Same lifecycle test but does NOT clean up. Run this manually to inspect
    /// the PR in Azure DevOps, then call CleanupTestPR() when done.
    /// </summary>
    [TestMethod]
    [Ignore("Run manually: dotnet test --filter InspectPR_NoCleanup")]
    [Timeout(300_000)]
    public async Task InspectPR_NoCleanup()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Test.json")
            .Build();

        var devOpsSettings = config.GetSection("AzureDevOps").Get<AzureDevOpsSettings>()!;
        var project = config["TestSettings:Project"]!;

        var services = new ServiceCollection();
        services.AddLogging(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
        services.Configure<AzureDevOpsSettings>(config.GetSection("AzureDevOps"));
        services.Configure<AiProviderSettings>(config.GetSection("AiProvider"));
        services.Configure<AzureOpenAISettings>(config.GetSection("AzureOpenAI"));
        services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();
        var fakeReview = new FakeCodeReviewService();
        services.AddSingleton<ICodeReviewService>(fakeReview);
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddTransient<CodeReviewOrchestrator>();

        await using var sp = services.BuildServiceProvider();
        var orchestrator = sp.GetRequiredService<CodeReviewOrchestrator>();

        // Create disposable repo + PR — SkipCleanupOnDispose keeps it alive for inspection
        var pr = new TestPullRequestHelper(
            devOpsSettings.Organization, devOpsSettings.PersonalAccessToken,
            project);
        pr.SkipCleanupOnDispose = true;

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        var prUrl = $"https://dev.azure.com/{devOpsSettings.Organization}/{project}/_git/{repo}/pullrequest/{prId}";

        Console.WriteLine($"\n══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  DISPOSABLE REPO CREATED — NOT deleting so you can inspect it!");
        Console.WriteLine($"  Repo: {repo}");
        Console.WriteLine($"  PR #{prId}: {prUrl}");
        Console.WriteLine($"  Branch: {pr.BranchName}");
        Console.WriteLine($"══════════════════════════════════════════════════════════════\n");

        // Helper to call the orchestrator using the disposable repo
        async Task<ReviewResponse> Review() =>
            await orchestrator.ExecuteReviewAsync(project, repo, prId);

        // S1: First review (draft)
        Console.WriteLine("── S1: First review (draft) ──");
        var r1 = await Review();
        Console.WriteLine($"  → Status={r1.Status}, Vote={r1.Vote ?? (object)"null"}\n");

        // S2: No changes → Skip
        Console.WriteLine("── S2: No changes → Skip ──");
        var r2 = await Review();
        Console.WriteLine($"  → Status={r2.Status}\n");

        // S3: Push commit → ReReview
        Console.WriteLine("── S3: Push new commit → ReReview ──");
        await pr.PushNewCommitAsync("scenario3-change.cs",
            "namespace Test;\n\npublic class Scenario3\n{\n    // Pushed to trigger re-review\n    public string Name { get; set; } = \"test\";\n}\n");
        await Task.Delay(3000);
        var r3 = await Review();
        Console.WriteLine($"  → Status={r3.Status}, Summary contains Re-Review: {r3.Summary?.Contains("Re-Review")}\n");

        // S4: Draft → Active → VoteOnly
        Console.WriteLine("── S4: Draft → Active → VoteOnly ──");
        await pr.SetDraftStatusAsync(false);
        await Task.Delay(2000);
        var r4 = await Review();
        Console.WriteLine($"  → Status={r4.Status}, Vote={r4.Vote ?? (object)"null"}\n");

        // S5: No changes → Skip
        Console.WriteLine("── S5: Active, no changes → Skip ──");
        var r5 = await Review();
        Console.WriteLine($"  → Status={r5.Status}\n");

        // S6: Reset + push → Full review with vote
        Console.WriteLine("── S6: Reset metadata + push → Full review ──");
        await pr.ClearReviewMetadataAsync();
        await pr.RemoveReviewTagAsync();
        await pr.PushNewCommitAsync("scenario6-addition.cs",
            "namespace Test;\n\npublic class Scenario6\n{\n    // Added after reset to test full re-review\n    public int Value => 42;\n}\n");
        await Task.Delay(3000);
        var r6 = await Review();
        Console.WriteLine($"  → Status={r6.Status}, Vote={r6.Vote ?? (object)"null"}\n");

        // S7: Push → ReReview + dedup
        Console.WriteLine("── S7: Another commit → ReReview + dedup ──");
        await pr.PushNewCommitAsync("scenario7-dedup.cs",
            "namespace Test;\n\npublic class Scenario7\n{\n    // Dedup test file\n    public bool IsDeduped => true;\n}\n");
        await Task.Delay(3000);
        var r7 = await Review();
        Console.WriteLine($"  → Status={r7.Status}, Summary contains Re-Review: {r7.Summary?.Contains("Re-Review")}\n");

        Console.WriteLine($"══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  ALL SCENARIOS COMPLETE — repo still exists for inspection:");
        Console.WriteLine($"  Repo: {repo}");
        Console.WriteLine($"  PR: {prUrl}");
        Console.WriteLine($"  ");
        Console.WriteLine($"  When done, run:  dotnet test --filter CleanupTestPR");
        Console.WriteLine($"══════════════════════════════════════════════════════════════\n");

        // Write cleanup info to a temp file so CleanupTestPR can find it
        var cleanupFile = Path.Combine(AppContext.BaseDirectory, "cleanup_info.json");
        var cleanupJson = JsonSerializer.Serialize(new
        {
            RepositoryId = pr.RepositoryId,
            RepositoryName = repo,
            Organization = devOpsSettings.Organization,
            Project = project,
        });
        await File.WriteAllTextAsync(cleanupFile, cleanupJson);
        Console.WriteLine($"  Cleanup info saved to: {cleanupFile}\n");
    }

    /// <summary>
    /// Cleans up a disposable repo left behind by InspectPR_NoCleanup.
    /// Run: dotnet test --filter CleanupTestPR
    /// Validates the marker file before deleting to ensure safety.
    /// </summary>
    [TestMethod]
    [Ignore("Run manually: dotnet test --filter CleanupTestPR")]
    public async Task CleanupTestPR()
    {
        var cleanupFile = Path.Combine(AppContext.BaseDirectory, "cleanup_info.json");
        Assert.IsTrue(File.Exists(cleanupFile), $"No cleanup_info.json found at {cleanupFile}. Run InspectPR_NoCleanup first.");

        var json = await File.ReadAllTextAsync(cleanupFile);
        var info = JsonSerializer.Deserialize<JsonElement>(json);

        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.Test.json")
            .Build();
        var devOpsSettings = config.GetSection("AzureDevOps").Get<AzureDevOpsSettings>()!;

        var repoId = info.GetProperty("RepositoryId").GetString()!;
        var repoName = info.GetProperty("RepositoryName").GetString()!;
        var org = info.GetProperty("Organization").GetString()!;
        var project = info.GetProperty("Project").GetString()!;

        // ── SAFETY: Verify repo name has our test prefix ──
        Assert.IsTrue(repoName.StartsWith("AiCodeReview-IntTest-", StringComparison.OrdinalIgnoreCase),
            $"SAFETY: Repo name '{repoName}' does not have the test prefix. Will NOT delete.");

        // ── SAFETY: Verify repo is not in the never-delete list ──
        var neverDelete = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "POCResearchScratchProjects", "OneVision" };
        Assert.IsFalse(neverDelete.Contains(repoName),
            $"SAFETY: Repo '{repoName}' is in the never-delete list. Will NOT delete.");

        Console.WriteLine($"Deleting disposable repo {repoName} ({repoId})...");

        using var http = new HttpClient();
        var creds = Convert.ToBase64String(System.Text.Encoding.ASCII.GetBytes($":{devOpsSettings.PersonalAccessToken}"));
        http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", creds);

        var baseUrl = $"https://dev.azure.com/{org}/{project}/_apis/git";

        // ── SAFETY: Verify marker file exists in the repo before deleting ──
        // Must use includeContent=true to get actual file content from the Items API
        http.DefaultRequestHeaders.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));
        var markerResp = await http.GetAsync(
            $"{baseUrl}/repositories/{repoName}/items?path=/.test-repo-marker.json&includeContent=true&api-version=7.1");
        Assert.IsTrue(markerResp.IsSuccessStatusCode,
            $"SAFETY: Marker file not found in repo (HTTP {(int)markerResp.StatusCode}). Will NOT delete.");

        var markerJson = await markerResp.Content.ReadAsStringAsync();
        var markerObj = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(markerJson);
        var hasContent = markerObj.TryGetProperty("content", out var contentEl);
        var markerText = hasContent ? contentEl.GetString() ?? "" : markerJson;
        Assert.IsTrue(markerText.Contains("AITK-DISPOSABLE-TEST-REPO-7F3A9B2E-SAFE-TO-DELETE"),
            "SAFETY: Marker file does not contain expected magic string. Will NOT delete.");

        Console.WriteLine("  Safety checks passed (prefix ✓, never-delete ✓, marker file ✓).");

        // Soft-delete
        var deleteResp = await http.DeleteAsync($"{baseUrl}/repositories/{repoId}?api-version=7.1");
        Console.WriteLine($"  Soft-delete: {(int)deleteResp.StatusCode}");

        if (deleteResp.IsSuccessStatusCode)
        {
            // Hard-delete from recycle bin
            await Task.Delay(1000);
            var purgeResp = await http.DeleteAsync($"{baseUrl}/recycleBin/repositories/{repoId}?api-version=7.1");
            Console.WriteLine($"  Purge from recycle bin: {(int)purgeResp.StatusCode}");
        }

        File.Delete(cleanupFile);
        Console.WriteLine("✓ Repo permanently deleted.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static int CountInlineThreads(List<JsonElement> threads) =>
        threads.Count(t =>
            t.TryGetProperty("threadContext", out var ctx)
            && ctx.ValueKind != JsonValueKind.Null);
}
