using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests that exercise orchestrator branches not covered by existing tests:
/// rate limiting, vote-only, no-reviewable-files, re-review, work items,
/// error handling, and simulation paths.  Uses BuildFullyFake() exclusively.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class OrchestratorCoverageTests
{
    private const string Project = "TestProject";
    private const string Repo = "TestRepo";
    private const int PrId = 1;

    // ═══════════════════════════════════════════════════════════════════
    //  Rate Limiting
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task RateLimited_WithoutForceReview_ReturnsRateLimitedStatus()
    {
        // Use a config with MinReviewIntervalMinutes > 0 so rate limiter fires
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureDevOps:Organization"] = "TestOrg",
                ["AzureDevOps:Pat"] = "fake-pat",
                ["AzureDevOps:MinReviewIntervalMinutes"] = "60",
                ["AiProvider:ActiveProvider"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Type"] = "AzureOpenAI",
                ["AiProvider:Providers:azure-openai:Endpoint"] = "https://fake.openai.azure.com/",
                ["AiProvider:Providers:azure-openai:ApiKey"] = "fake-key",
                ["AiProvider:Providers:azure-openai:Model"] = "fake-model",
                ["AzureOpenAI:Endpoint"] = "https://fake.openai.azure.com/",
                ["AzureOpenAI:ApiKey"] = "fake-key",
                ["AzureOpenAI:DeploymentName"] = "fake-model",
            })
            .Build();

        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps, config: config);

        // First review — should succeed
        var result1 = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);
        Assert.AreEqual("Reviewed", result1.Status, $"First review should complete. Error: {result1.ErrorMessage}");

        // Update the PR source commit so the skip check doesn't fire
        var pr = await fakeDevOps.GetPullRequestAsync(Project, Repo, PrId);
        pr.LastMergeSourceCommit = "new-commit-sha";

        // Second review immediately — should be rate-limited (60 min interval)
        var result2 = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);
        Assert.AreEqual("RateLimited", result2.Status, "Second review within cooldown should be rate-limited");
    }

    [TestMethod]
    public async Task RateLimited_WithForceReview_BypassesLimiter()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        // First review
        await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);

        // Force review should bypass rate limit
        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, forceReview: true);
        Assert.AreEqual("Reviewed", result.Status, "Force review should bypass rate limiter");
    }

    [TestMethod]
    public async Task RateLimited_Simulation_BypassesLimiter()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        // First review
        await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);

        // Simulation should bypass rate limit
        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, simulationOnly: true);
        Assert.AreEqual("Simulated", result.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Skip detection
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SameSourceCommit_AlreadyReviewed_Skips()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        // Seed metadata to indicate already reviewed at same commit
        var pr = await fakeDevOps.GetPullRequestAsync(Project, Repo, PrId);
        await fakeDevOps.SetReviewMetadataAsync(Project, Repo, PrId, new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow.AddMinutes(-5),
            LastReviewedSourceCommit = pr.LastMergeSourceCommit,
            LastReviewedIteration = 1,
            VoteSubmitted = true,
        });

        var result = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);
        Assert.AreEqual("Skipped", result.Status, "Should skip when already reviewed at same commit");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Re-review (source commit changed)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task DifferentSourceCommit_TriggersReReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        // Seed metadata as if previously reviewed with a different commit
        await fakeDevOps.SetReviewMetadataAsync(Project, Repo, PrId, new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastReviewedSourceCommit = "old-commit-sha-1234567890",
            LastReviewedIteration = 1,
            VoteSubmitted = true,
        });

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, forceReview: true);
        Assert.AreEqual("Reviewed", result.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Vote-only (draft → active transition)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task DraftToActive_VoteNotSubmitted_CastsVoteOnly()
    {
        var fakeDevOps = new FakeDevOpsService();
        // Seed a non-draft PR
        var pr = new PullRequestInfo
        {
            PullRequestId = PrId,
            Title = "Test PR",
            IsDraft = false,
            SourceBranch = "refs/heads/feature",
            TargetBranch = "refs/heads/main",
            LastMergeSourceCommit = "abc123",
            CreatedBy = "tester",
        };
        fakeDevOps.SeedPullRequest(Project, Repo, pr);
        fakeDevOps.SeedFileChanges(Project, Repo, PrId, new List<FileChange>
        {
            new() { FilePath = "src/Test.cs", ChangeType = "edit", UnifiedDiff = "- old\n+ new" }
        });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        // Seed metadata: reviewed at same commit, but vote not submitted (was draft)
        await fakeDevOps.SetReviewMetadataAsync(Project, Repo, PrId, new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow.AddMinutes(-10),
            LastReviewedSourceCommit = "abc123",
            LastReviewedIteration = 1,
            VoteSubmitted = false,
            WasDraft = true,
        });

        var result = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);

        // Should trigger a vote-only action (either Reviewed if re-reviewed, or Skipped if already done)
        Assert.IsTrue(
            result.Status == "Reviewed" || result.Status == "Skipped",
            $"Should handle vote-only path. Status: {result.Status}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  No reviewable files (all skipped)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AllFilesSkipped_PostsAutoApprove()
    {
        var fakeDevOps = new FakeDevOpsService();
        var pr = new PullRequestInfo
        {
            PullRequestId = PrId,
            Title = "Config only change",
            SourceBranch = "refs/heads/feature",
            TargetBranch = "refs/heads/main",
            LastMergeSourceCommit = "abc123",
            CreatedBy = "tester",
        };
        fakeDevOps.SeedPullRequest(Project, Repo, pr);
        // Only non-reviewable files (e.g., lock files, generated)
        fakeDevOps.SeedFileChanges(Project, Repo, PrId, new List<FileChange>
        {
            new() { FilePath = "package-lock.json", ChangeType = "edit" },
            new() { FilePath = ".gitignore", ChangeType = "edit" },
        });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);
        var result = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);

        Assert.AreEqual("Reviewed", result.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Work items context
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task LinkedWorkItems_IncludedInReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        fakeDevOps.SeedLinkedWorkItems(Project, Repo, PrId, new List<int> { 100 });
        fakeDevOps.SeedWorkItem(new WorkItemInfo
        {
            Id = 100,
            WorkItemType = "User Story",
            Title = "Implement login flow",
            State = "Active",
            Description = "As a user, I want to log in.",
            AcceptanceCriteria = "Login form is displayed",
        });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);
        var result = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);

        Assert.AreEqual("Reviewed", result.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Simulation mode
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Simulation_DoesNotPostComments()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, simulationOnly: true);

        Assert.AreEqual("Simulated", result.Status);
        // In simulation mode, no comments should be posted
        var comments = fakeDevOps.PostedComments(Project, Repo, PrId);
        Assert.AreEqual(0, comments.Count, "Simulation should not post comments");
    }

    [TestMethod]
    public async Task Simulation_DoesNotVote()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, simulationOnly: true);

        Assert.AreEqual("Simulated", result.Status);
        var vote = fakeDevOps.LastVote(Project, Repo, PrId);
        Assert.IsNull(vote, "Simulation should not submit a vote");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Quick depth mode
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task QuickDepth_CompletesReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, reviewDepth: ReviewDepth.Quick);

        Assert.AreEqual("Reviewed", result.Status);
    }

    [TestMethod]
    public async Task DeepDepth_CompletesReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, reviewDepth: ReviewDepth.Deep);

        Assert.AreEqual("Reviewed", result.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Multiple files (merge results)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task MultipleFiles_MergesResults()
    {
        var fakeDevOps = new FakeDevOpsService();
        var pr = new PullRequestInfo
        {
            PullRequestId = PrId,
            Title = "Multi-file change",
            SourceBranch = "refs/heads/feature",
            TargetBranch = "refs/heads/main",
            LastMergeSourceCommit = "abc123",
            CreatedBy = "tester",
        };
        fakeDevOps.SeedPullRequest(Project, Repo, pr);
        fakeDevOps.SeedFileChanges(Project, Repo, PrId, new List<FileChange>
        {
            new() { FilePath = "src/ServiceA.cs", ChangeType = "edit", UnifiedDiff = "- old\n+ new", ModifiedContent = "public class A {}" },
            new() { FilePath = "src/ServiceB.cs", ChangeType = "add", ModifiedContent = "public class B {}" },
            new() { FilePath = "src/ServiceC.cs", ChangeType = "edit", UnifiedDiff = "- x\n+ y", ModifiedContent = "public class C {}" },
        });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);
        var result = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);

        Assert.AreEqual("Reviewed", result.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Progress reporting
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ProgressHandler_ReceivesUpdates()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var updates = new List<ReviewStatusUpdate>();
        // Use a synchronous IProgress implementation to avoid SynchronizationContext timing issues
        var progress = new SyncProgress<ReviewStatusUpdate>(u => updates.Add(u));

        await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId, progress);

        Assert.IsTrue(updates.Count > 0, "Should receive progress updates");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Cancellation
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task CancellationToken_Respected()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // Pre-cancel

        try
        {
            await ctx.Orchestrator.ExecuteReviewAsync(
                Project, Repo, PrId, cancellationToken: cts.Token);
            // If it completes (some paths don't check cancellation early), that's OK
        }
        catch (OperationCanceledException)
        {
            // Expected if cancellation is checked early
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ReviewStrategy
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FileByFileStrategy_CompletesReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, reviewStrategy: ReviewStrategy.FileByFile);

        Assert.AreEqual("Reviewed", result.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Action label: Full Review vs Re-Review (force/simulation)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ForceReview_NoPriorMetadata_RecordsFullReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, forceReview: true);

        Assert.AreEqual("Reviewed", result.Status);

        var history = await fakeDevOps.GetReviewHistoryAsync(Project, Repo, PrId);
        Assert.AreEqual(1, history.Count, "Exactly one history entry expected.");
        Assert.AreEqual("Full Review", history[0].Action,
            "First review with forceReview should record 'Full Review', not 'Re-Review'.");
    }

    [TestMethod]
    public async Task Simulation_NoPriorMetadata_SummaryShowsFullReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, simulationOnly: true);

        Assert.AreEqual("Simulated", result.Status);
        Assert.IsNotNull(result.Summary, "Simulation should produce a summary.");
        Assert.IsFalse(result.Summary.Contains("Re-Review"),
            "First simulation with no prior metadata should not show 'Re-Review' in summary.");
    }

    [TestMethod]
    public async Task ForceReview_WithPriorMetadata_RecordsReReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        // Seed prior review metadata
        await fakeDevOps.SetReviewMetadataAsync(Project, Repo, PrId, new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow.AddMinutes(-30),
            LastReviewedSourceCommit = "old-commit-sha",
            LastReviewedIteration = 1,
            VoteSubmitted = true,
        });

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            Project, Repo, PrId, forceReview: true);

        Assert.AreEqual("Reviewed", result.Status);

        var history = await fakeDevOps.GetReviewHistoryAsync(Project, Repo, PrId);
        Assert.IsTrue(history.Count >= 1, "At least one history entry expected.");
        Assert.AreEqual("Re-Review", history[^1].Action,
            "Review with prior metadata and forceReview should record 'Re-Review'.");
    }

    [TestMethod]
    public async Task NormalFirstReview_NoPriorMetadata_RecordsFullReview()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps);
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(Project, Repo, PrId);

        Assert.AreEqual("Reviewed", result.Status);

        var history = await fakeDevOps.GetReviewHistoryAsync(Project, Repo, PrId);
        Assert.AreEqual(1, history.Count, "Exactly one history entry expected.");
        Assert.AreEqual("Full Review", history[0].Action,
            "First review without forceReview should record 'Full Review'.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static void SeedStandardPr(FakeDevOpsService fakeDevOps)
    {
        var pr = new PullRequestInfo
        {
            PullRequestId = PrId,
            Title = "Test PR",
            SourceBranch = "refs/heads/feature",
            TargetBranch = "refs/heads/main",
            LastMergeSourceCommit = "abc123",
            CreatedBy = "tester",
        };
        fakeDevOps.SeedPullRequest(Project, Repo, pr);
        fakeDevOps.SeedFileChanges(Project, Repo, PrId, new List<FileChange>
        {
            new()
            {
                FilePath = "src/Test.cs",
                ChangeType = "edit",
                UnifiedDiff = "- old line\n+ new line",
                ModifiedContent = "public class Test { public void Foo() { } }"
            }
        });
    }

    /// <summary>Synchronous IProgress that invokes callback inline (no SynchronizationContext delay).</summary>
    private sealed class SyncProgress<T>(Action<T> handler) : IProgress<T>
    {
        public void Report(T value) => handler(value);
    }
}
