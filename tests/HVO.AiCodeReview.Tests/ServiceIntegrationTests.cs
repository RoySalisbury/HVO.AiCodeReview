using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

/// <summary>
/// Integration tests for the new service-level features:
///   - Review history stored in PR properties
///   - ReviewCount metadata tracking
///   - PR description update with history table
///   - Metrics API response shape
///   - Tag resilience (deleting tag doesn't break review state)
///
/// Creates a disposable test repo with a PR, exercises the features, then deletes the repo.
/// Requires a valid Azure DevOps PAT in appsettings.Test.json.
/// </summary>
[TestClass]
public class ServiceIntegrationTests
{
    private static IConfiguration BuildConfig() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.Test.json")
        .Build();

    private static (ServiceProvider sp, CodeReviewOrchestrator orchestrator, AzureDevOpsSettings settings, string project)
        BuildServices(IConfiguration config)
    {
        var devOpsSettings = config.GetSection("AzureDevOps").Get<AzureDevOpsSettings>()!;
        var project = config["TestSettings:Project"]!;

        var services = new ServiceCollection();
        services.AddLogging(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
        services.Configure<AzureDevOpsSettings>(config.GetSection("AzureDevOps"));
        services.Configure<AiProviderSettings>(config.GetSection("AiProvider"));
        services.Configure<AzureOpenAISettings>(config.GetSection("AzureOpenAI"));
        services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();
        services.AddSingleton<ICodeReviewService>(new FakeCodeReviewService());
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddTransient<CodeReviewOrchestrator>();

        var sp = services.BuildServiceProvider();
        return (sp, sp.GetRequiredService<CodeReviewOrchestrator>(), devOpsSettings, project);
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Review History in PR Properties
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(180_000)]
    public async Task ReviewHistory_StoredInProperties_RoundTrips()
    {
        var config = BuildConfig();
        var (sp, orchestrator, settings, project) = BuildServices(config);
        await using var _ = sp;

        var devOps = sp.GetRequiredService<IAzureDevOpsService>();

        await using var pr = new TestPullRequestHelper(
            settings.Organization, settings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  Created PR #{prId} in disposable repo {repo} for review history test");

        // ── Run a review → should create history entry #1 ──
        var r1 = await orchestrator.ExecuteReviewAsync(project, repo, prId);
        Assert.AreEqual("Reviewed", r1.Status, "First review should succeed.");

        // ── Verify history property exists ──
        var history = await devOps.GetReviewHistoryAsync(project, repo, prId);
        Assert.IsNotNull(history, "History should not be null.");
        Assert.AreEqual(1, history.Count, "Should have exactly 1 history entry after first review.");

        var entry1 = history[0];
        Assert.AreEqual(1, entry1.ReviewNumber, "First entry should be ReviewNumber=1.");
        Assert.AreEqual("Full Review", entry1.Action, "First review action should be Full Review.");
        Assert.IsFalse(string.IsNullOrEmpty(entry1.Verdict), "Verdict should be populated.");
        Assert.IsFalse(string.IsNullOrEmpty(entry1.SourceCommit), "SourceCommit should be populated.");
        Assert.IsTrue(entry1.IsDraft, "PR was draft during first review.");
        Assert.IsTrue(entry1.FilesChanged > 0, "Should track files changed.");
        Assert.IsTrue(entry1.ReviewedAtUtc > DateTime.MinValue, "ReviewedAtUtc should be set.");

        Console.WriteLine($"  ✓ History entry #1: Action={entry1.Action}, Verdict={entry1.Verdict}, " +
                          $"Files={entry1.FilesChanged}, Comments={entry1.InlineComments}");

        // ── Push a commit + re-review → should create history entry #2 ──
        await pr.PushNewCommitAsync($"history-test-{Guid.NewGuid():N}.txt",
            "// Trigger re-review for history test\n");
        await Task.Delay(3000);

        var r2 = await orchestrator.ExecuteReviewAsync(project, repo, prId);
        Assert.AreEqual("Reviewed", r2.Status);

        var history2 = await devOps.GetReviewHistoryAsync(project, repo, prId);
        Assert.AreEqual(2, history2.Count, "Should have 2 history entries after re-review.");

        var entry2 = history2[1];
        Assert.AreEqual(2, entry2.ReviewNumber, "Second entry should be ReviewNumber=2.");
        Assert.AreEqual("Re-Review", entry2.Action, "Second review should be Re-Review.");
        Assert.AreNotEqual(entry1.SourceCommit, entry2.SourceCommit, "Commits should differ.");

        Console.WriteLine($"  ✓ History entry #2: Action={entry2.Action}, Verdict={entry2.Verdict}");
        Console.WriteLine($"  ✓ Review history round-trip passed.\n");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  ReviewCount Metadata Tracking
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(180_000)]
    public async Task ReviewCount_IncrementsCorrectly()
    {
        var config = BuildConfig();
        var (sp, orchestrator, settings, project) = BuildServices(config);
        await using var _ = sp;
        var devOps = sp.GetRequiredService<IAzureDevOpsService>();

        await using var pr = new TestPullRequestHelper(
            settings.Organization, settings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  Created PR #{prId} in disposable repo {repo} for ReviewCount test");

        // Before any review: count should be 0
        var meta0 = await devOps.GetReviewMetadataAsync(project, repo, prId);
        Assert.AreEqual(0, meta0.ReviewCount, "ReviewCount should be 0 before any review.");

        // Review #1
        await orchestrator.ExecuteReviewAsync(project, repo, prId);
        var meta1 = await devOps.GetReviewMetadataAsync(project, repo, prId);
        Assert.AreEqual(1, meta1.ReviewCount, "ReviewCount should be 1 after first review.");

        // Push + Review #2
        await pr.PushNewCommitAsync($"count-test-{Guid.NewGuid():N}.txt", "// count test\n");
        await Task.Delay(3000);
        await orchestrator.ExecuteReviewAsync(project, repo, prId);
        var meta2 = await devOps.GetReviewMetadataAsync(project, repo, prId);
        Assert.AreEqual(2, meta2.ReviewCount, "ReviewCount should be 2 after second review.");

        Console.WriteLine($"  ✓ ReviewCount: 0 → 1 → 2 correctly.\n");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  PR Description History Table
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(180_000)]
    public async Task PrDescription_ContainsHistoryTable()
    {
        var config = BuildConfig();
        var (sp, orchestrator, settings, project) = BuildServices(config);
        await using var _ = sp;
        var devOps = sp.GetRequiredService<IAzureDevOpsService>();

        await using var pr = new TestPullRequestHelper(
            settings.Organization, settings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  Created PR #{prId} in disposable repo {repo} for description history test");

        // Run a review
        await orchestrator.ExecuteReviewAsync(project, repo, prId);

        // Fetch the PR description
        var prInfo = await devOps.GetPullRequestAsync(project, repo, prId);

        Assert.IsTrue(prInfo.Description.Contains("AI-REVIEW-HISTORY-START"),
            "Description should contain history start marker.");
        Assert.IsTrue(prInfo.Description.Contains("AI-REVIEW-HISTORY-END"),
            "Description should contain history end marker.");
        Assert.IsTrue(prInfo.Description.Contains("AI Code Review History"),
            "Description should contain history heading.");
        Assert.IsTrue(prInfo.Description.Contains("| 1 |"),
            "Description should contain row for review #1.");
        Assert.IsTrue(prInfo.Description.Contains("Full Review"),
            "Description history should show Full Review action.");

        Console.WriteLine($"  ✓ PR description contains review history table.\n");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Tag Resilience — Deleting Tag Doesn't Break State
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(180_000)]
    public async Task TagResilience_DeletedTagDoesntBreakReview()
    {
        var config = BuildConfig();
        var (sp, orchestrator, settings, project) = BuildServices(config);
        await using var _ = sp;
        var devOps = sp.GetRequiredService<IAzureDevOpsService>();

        await using var pr = new TestPullRequestHelper(
            settings.Organization, settings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  Created PR #{prId} in disposable repo {repo} for tag resilience test");

        // Review #1
        await orchestrator.ExecuteReviewAsync(project, repo, prId);

        // Verify tag was added
        var labels1 = await pr.GetLabelsAsync();
        Assert.IsTrue(labels1.Contains("ai-code-review"), "Tag should be present after review.");

        // Delete the tag (simulating someone removing it manually)
        await pr.RemoveReviewTagAsync();
        var labelsAfterDelete = await pr.GetLabelsAsync();
        Assert.IsFalse(labelsAfterDelete.Contains("ai-code-review"), "Tag should be gone after removal.");

        // Same PR, no changes → should still Skip (metadata-driven, not tag-driven)
        var r2 = await orchestrator.ExecuteReviewAsync(project, repo, prId);
        Assert.AreEqual("Skipped", r2.Status,
            "Review should Skip based on metadata, even with tag deleted.");

        // Tag should be re-added (decorative re-add doesn't happen on Skip — that's fine)
        // Push a change to trigger re-review → tag should be re-added
        await pr.PushNewCommitAsync($"tag-test-{Guid.NewGuid():N}.txt", "// tag resilience\n");
        await Task.Delay(3000);

        var r3 = await orchestrator.ExecuteReviewAsync(project, repo, prId);
        Assert.AreEqual("Reviewed", r3.Status, "Re-review should succeed without tag.");

        var labelsAfterReview = await pr.GetLabelsAsync();
        Assert.IsTrue(labelsAfterReview.Contains("ai-code-review"),
            "Tag should be re-added after re-review.");

        // Metadata should be intact through all of this
        // ReviewCount = 3 because: review #1, skip #2, re-review #3 (skip events are now tracked)
        var meta = await devOps.GetReviewMetadataAsync(project, repo, prId);
        Assert.AreEqual(3, meta.ReviewCount, "ReviewCount should be 3 (review, skip, re-review).");

        Console.WriteLine($"  ✓ Tag resilience verified: delete tag → Skip works → re-review re-adds tag.\n");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  Metrics API — GetReviewHistory Round-Trip
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(180_000)]
    public async Task MetricsApi_ReturnsHistoryAndMetadata()
    {
        var config = BuildConfig();
        var (sp, orchestrator, settings, project) = BuildServices(config);
        await using var _ = sp;
        var devOps = sp.GetRequiredService<IAzureDevOpsService>();

        await using var pr = new TestPullRequestHelper(
            settings.Organization, settings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  Created PR #{prId} in disposable repo {repo} for metrics API test");

        // Run a review
        await orchestrator.ExecuteReviewAsync(project, repo, prId);

        // Simulate what the metrics endpoint does: fetch metadata + history
        var metadata = await devOps.GetReviewMetadataAsync(project, repo, prId);
        var history = await devOps.GetReviewHistoryAsync(project, repo, prId);

        // Validate metadata
        Assert.AreEqual(1, metadata.ReviewCount);
        Assert.IsTrue(metadata.HasPreviousReview);
        Assert.IsFalse(string.IsNullOrEmpty(metadata.LastReviewedSourceCommit));

        // Validate history
        Assert.AreEqual(1, history.Count);
        var h = history[0];
        Assert.AreEqual("Full Review", h.Action);
        Assert.IsTrue(h.FilesChanged > 0);
        Assert.IsTrue(h.ReviewedAtUtc > DateTime.UtcNow.AddMinutes(-5), "ReviewedAtUtc should be recent.");

        // Verify AI metrics are null (we used FakeCodeReviewService)
        // This is expected — only real CodeReviewService populates token counts
        Console.WriteLine($"  History entry: Model={h.ModelName ?? "(fake)"}, " +
                          $"Tokens={h.TotalTokens?.ToString() ?? "n/a"}, " +
                          $"AiMs={h.AiDurationMs?.ToString() ?? "n/a"}, " +
                          $"TotalMs={h.TotalDurationMs?.ToString() ?? "n/a"}");

        // TotalDurationMs should be set even with fake service (it's the Stopwatch in the orchestrator)
        Assert.IsTrue(h.TotalDurationMs.HasValue && h.TotalDurationMs > 0,
            "TotalDurationMs should be populated by orchestrator timing.");

        Console.WriteLine($"  ✓ Metrics data retrieval works.\n");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  History Survives Metadata Clear — Verifies Independent Storage
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(120_000)]
    public async Task ReviewHistory_PersistedAsProperty_DirectReadWrite()
    {
        var config = BuildConfig();
        var (sp, _, settings, project) = BuildServices(config);
        await using var _ = sp;
        var devOps = sp.GetRequiredService<IAzureDevOpsService>();

        await using var pr = new TestPullRequestHelper(
            settings.Organization, settings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  Created PR #{prId} in disposable repo {repo} for direct history read/write test");

        // Start with empty history
        var empty = await devOps.GetReviewHistoryAsync(project, repo, prId);
        Assert.AreEqual(0, empty.Count, "History should be empty on a new PR.");

        // Append an entry
        var entry = new ReviewHistoryEntry
        {
            ReviewNumber = 1,
            ReviewedAtUtc = DateTime.UtcNow,
            Action = "Test Action",
            Verdict = "Test Verdict",
            SourceCommit = "abc1234",
            Iteration = 1,
            IsDraft = true,
            InlineComments = 3,
            FilesChanged = 2,
            Vote = 5,
            ModelName = "test-model",
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
            AiDurationMs = 1234,
            TotalDurationMs = 5678,
        };
        await devOps.AppendReviewHistoryAsync(project, repo, prId, entry);

        // Read it back
        var history = await devOps.GetReviewHistoryAsync(project, repo, prId);
        Assert.AreEqual(1, history.Count, "Should have 1 entry.");

        var read = history[0];
        Assert.AreEqual(1, read.ReviewNumber);
        Assert.AreEqual("Test Action", read.Action);
        Assert.AreEqual("Test Verdict", read.Verdict);
        Assert.AreEqual("abc1234", read.SourceCommit);
        Assert.AreEqual(true, read.IsDraft);
        Assert.AreEqual(3, read.InlineComments);
        Assert.AreEqual(2, read.FilesChanged);
        Assert.AreEqual(5, read.Vote);
        Assert.AreEqual("test-model", read.ModelName);
        Assert.AreEqual(100, read.PromptTokens);
        Assert.AreEqual(50, read.CompletionTokens);
        Assert.AreEqual(150, read.TotalTokens);
        Assert.AreEqual(1234, read.AiDurationMs);
        Assert.AreEqual(5678, read.TotalDurationMs);

        // Append another entry — verify list grows
        var entry2 = new ReviewHistoryEntry
        {
            ReviewNumber = 2,
            ReviewedAtUtc = DateTime.UtcNow,
            Action = "Re-Review",
            Verdict = "Approved",
            SourceCommit = "def5678",
            Iteration = 2,
        };
        await devOps.AppendReviewHistoryAsync(project, repo, prId, entry2);

        var history2 = await devOps.GetReviewHistoryAsync(project, repo, prId);
        Assert.AreEqual(2, history2.Count, "Should have 2 entries after second append.");
        Assert.AreEqual("Test Action", history2[0].Action, "First entry preserved.");
        Assert.AreEqual("Re-Review", history2[1].Action, "Second entry appended.");

        Console.WriteLine($"  ✓ Direct history read/write round-trip passed (all fields verified).\n");
    }

    // ═════════════════════════════════════════════════════════════════════════
    //  UpdatePrDescription — Verifies the PATCH Works
    // ═════════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(60_000)]
    public async Task UpdatePrDescription_Works()
    {
        var config = BuildConfig();
        var (sp, _, settings, project) = BuildServices(config);
        await using var _ = sp;
        var devOps = sp.GetRequiredService<IAzureDevOpsService>();

        await using var pr = new TestPullRequestHelper(
            settings.Organization, settings.PersonalAccessToken,
            project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  Created PR #{prId} in disposable repo {repo} for description update test");

        // Read the original description
        var original = await devOps.GetPullRequestAsync(project, repo, prId);
        var originalDesc = original.Description;

        // Append a test section
        var testMarker = $"<!-- TEST-MARKER-{Guid.NewGuid():N} -->";
        var newDesc = originalDesc + "\n\n" + testMarker;
        await devOps.UpdatePrDescriptionAsync(project, repo, prId, newDesc);

        // Read back and verify
        var updated = await devOps.GetPullRequestAsync(project, repo, prId);
        Assert.IsTrue(updated.Description.Contains(testMarker),
            "Updated description should contain our test marker.");
        Assert.IsTrue(updated.Description.Contains(originalDesc.TrimEnd()),
            "Original description content should still be present.");

        Console.WriteLine($"  ✓ UpdatePrDescriptionAsync works.\n");
    }
}
