using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for the <see cref="FakeDevOpsService"/> and the
/// <see cref="TestServiceBuilder.BuildFullyFake"/> DI builder.
/// These tests run entirely in-memory — no Azure DevOps credentials required.
/// </summary>
[TestClass]
public class FakeDevOpsServiceTests
{
    // ─────────────────────────────────────────────────────────────────────
    //  FakeDevOpsService — unit tests
    // ─────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task GetPullRequest_NoSeed_ReturnsSensibleDefault()
    {
        using var svc = new FakeDevOpsService();

        var pr = await svc.GetPullRequestAsync("proj", "repo", 42);

        Assert.AreEqual(42, pr.PullRequestId);
        Assert.AreEqual("active", pr.Status);
        Assert.IsFalse(pr.IsDraft);
        Assert.IsFalse(string.IsNullOrEmpty(pr.LastMergeSourceCommit));
    }

    [TestMethod]
    public async Task GetPullRequest_Seeded_ReturnsSeedData()
    {
        using var svc = new FakeDevOpsService();
        svc.SeedPullRequest("p", "r", new PullRequestInfo
        {
            PullRequestId = 7,
            Title = "My PR",
            IsDraft = true,
            Status = "active",
            LastMergeSourceCommit = "aaa",
            LastMergeTargetCommit = "bbb",
        });

        var pr = await svc.GetPullRequestAsync("p", "r", 7);

        Assert.AreEqual("My PR", pr.Title);
        Assert.IsTrue(pr.IsDraft);
    }

    [TestMethod]
    public async Task GetPullRequest_FactoryOverride_TakesPrecedence()
    {
        using var svc = new FakeDevOpsService();
        svc.PullRequestFactory = (proj, repo, id) => new PullRequestInfo
        {
            PullRequestId = id,
            Title = $"Factory PR {proj}/{repo}",
            Status = "completed",
            LastMergeSourceCommit = "x",
            LastMergeTargetCommit = "y",
        };

        var pr = await svc.GetPullRequestAsync("proj", "repo", 99);

        Assert.AreEqual("Factory PR proj/repo", pr.Title);
        Assert.AreEqual("completed", pr.Status);
    }

    [TestMethod]
    public async Task Metadata_RoundTrips()
    {
        using var svc = new FakeDevOpsService();

        var meta = new ReviewMetadata
        {
            LastReviewedSourceCommit = "abc",
            LastReviewedIteration = 3,
            ReviewCount = 1,
        };

        await svc.SetReviewMetadataAsync("p", "r", 1, meta);
        var result = await svc.GetReviewMetadataAsync("p", "r", 1);

        Assert.AreEqual("abc", result.LastReviewedSourceCommit);
        Assert.AreEqual(3, result.LastReviewedIteration);
    }

    [TestMethod]
    public async Task ReviewHistory_AppendAndRetrieve()
    {
        using var svc = new FakeDevOpsService();

        await svc.AppendReviewHistoryAsync("p", "r", 1, new ReviewHistoryEntry
        {
            ReviewNumber = 1,
            Action = "Full Review",
            Verdict = "Approved",
        });
        await svc.AppendReviewHistoryAsync("p", "r", 1, new ReviewHistoryEntry
        {
            ReviewNumber = 2,
            Action = "Re-Review",
            Verdict = "Needs Work",
        });

        var history = await svc.GetReviewHistoryAsync("p", "r", 1);

        Assert.AreEqual(2, history.Count);
        Assert.AreEqual("Full Review", history[0].Action);
        Assert.AreEqual("Re-Review", history[1].Action);
    }

    [TestMethod]
    public async Task Tags_AddAndCheck()
    {
        using var svc = new FakeDevOpsService();

        Assert.IsFalse(await svc.HasReviewTagAsync("p", "r", 1));

        await svc.AddReviewTagAsync("p", "r", 1);

        Assert.IsTrue(await svc.HasReviewTagAsync("p", "r", 1));
    }

    [TestMethod]
    public async Task PostComment_TracksInPostedComments()
    {
        using var svc = new FakeDevOpsService();

        await svc.PostCommentThreadAsync("p", "r", 1, "Hello world");

        var posted = svc.PostedComments("p", "r", 1);
        Assert.AreEqual(1, posted.Count);
        Assert.AreEqual("Hello world", posted[0].Content);
    }

    [TestMethod]
    public async Task PostInlineComment_TracksInPostedInlineComments()
    {
        using var svc = new FakeDevOpsService();

        await svc.PostInlineCommentThreadAsync("p", "r", 1, "/src/File.cs", 10, 15, "Fix this");

        var posted = svc.PostedInlineComments("p", "r", 1);
        Assert.AreEqual(1, posted.Count);
        Assert.AreEqual("/src/File.cs", posted[0].FilePath);
        Assert.AreEqual(10, posted[0].StartLine);
        Assert.AreEqual(15, posted[0].EndLine);
    }

    [TestMethod]
    public async Task AddReviewer_TracksVote()
    {
        using var svc = new FakeDevOpsService();

        await svc.AddReviewerAsync("p", "r", 1, 5);

        Assert.AreEqual(5, svc.LastVote("p", "r", 1));
    }

    [TestMethod]
    public async Task GetPullRequestChanges_DefaultsToOneFile()
    {
        using var svc = new FakeDevOpsService();
        var pr = await svc.GetPullRequestAsync("p", "r", 1);
        var changes = await svc.GetPullRequestChangesAsync("p", "r", 1, pr);

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual("edit", changes[0].ChangeType);
    }

    [TestMethod]
    public async Task GetPullRequestChanges_SeededChanges()
    {
        using var svc = new FakeDevOpsService();
        svc.SeedFileChanges("p", "r", 1, new List<FileChange>
        {
            new() { FilePath = "/a.cs", ChangeType = "add" },
            new() { FilePath = "/b.cs", ChangeType = "edit" },
        });
        var pr = await svc.GetPullRequestAsync("p", "r", 1);
        var changes = await svc.GetPullRequestChangesAsync("p", "r", 1, pr);

        Assert.AreEqual(2, changes.Count);
    }

    [TestMethod]
    public async Task ResolveServiceIdentity_ReturnsFakeId()
    {
        using var svc = new FakeDevOpsService();
        var id = await svc.ResolveServiceIdentityAsync();
        Assert.IsNotNull(id);
    }

    [TestMethod]
    public async Task IterationCount_DefaultsToOne()
    {
        using var svc = new FakeDevOpsService();
        var count = await svc.GetIterationCountAsync("p", "r", 1);
        Assert.AreEqual(1, count);
    }

    [TestMethod]
    public async Task IterationCount_SeededValue()
    {
        using var svc = new FakeDevOpsService();
        svc.SeedIterationCount("p", "r", 1, 5);
        var count = await svc.GetIterationCountAsync("p", "r", 1);
        Assert.AreEqual(5, count);
    }

    [TestMethod]
    public async Task WorkItems_SeededAndRetrieved()
    {
        using var svc = new FakeDevOpsService();
        svc.SeedLinkedWorkItems("p", "r", 1, new List<int> { 100, 200 });
        svc.SeedWorkItem(new WorkItemInfo { Id = 100, Title = "Story A", WorkItemType = "User Story" });
        svc.SeedWorkItem(new WorkItemInfo { Id = 200, Title = "Bug B", WorkItemType = "Bug" });

        var ids = await svc.GetLinkedWorkItemIdsAsync("p", "r", 1);
        Assert.AreEqual(2, ids.Count);

        var item = await svc.GetWorkItemAsync("p", 100);
        Assert.IsNotNull(item);
        Assert.AreEqual("Story A", item!.Title);
    }

    [TestMethod]
    public async Task ExistingThreads_SeededAndDeduped()
    {
        using var svc = new FakeDevOpsService();
        svc.SeedExistingThreads("p", "r", 1, new List<ExistingCommentThread>
        {
            new() { ThreadId = 1, FilePath = "/a.cs", Content = "Fix this", IsAiGenerated = true },
        });

        var threads = await svc.GetExistingReviewThreadsAsync("p", "r", 1);
        Assert.AreEqual(1, threads.Count);
        Assert.AreEqual("Fix this", threads[0].Content);
    }

    [TestMethod]
    public async Task CountReviewSummaryComments_TracksPosted()
    {
        using var svc = new FakeDevOpsService();
        Assert.AreEqual(0, await svc.CountReviewSummaryCommentsAsync("p", "r", 1));

        await svc.PostCommentThreadAsync("p", "r", 1, "Summary 1");
        Assert.AreEqual(1, await svc.CountReviewSummaryCommentsAsync("p", "r", 1));

        await svc.PostCommentThreadAsync("p", "r", 1, "Summary 2");
        Assert.AreEqual(2, await svc.CountReviewSummaryCommentsAsync("p", "r", 1));
    }

    // ─────────────────────────────────────────────────────────────────────
    //  BuildFullyFake — DI integration
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>Build an in-memory config so tests run without appsettings.Test.json.</summary>
    private static IConfiguration BuildMinimalConfig() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureDevOps:Organization"] = "fake-org",
            ["AzureDevOps:PersonalAccessToken"] = "fake-pat",
            ["TestSettings:Project"] = "TestProject",
            ["AiProvider:Mode"] = "single",
            ["AiProvider:ActiveProvider"] = "azure-openai",
            ["AiProvider:Providers:azure-openai:Type"] = "azure-openai",
            ["AiProvider:Providers:azure-openai:Endpoint"] = "https://fake.openai.azure.com",
            ["AiProvider:Providers:azure-openai:ApiKey"] = "fake-key",
            ["AiProvider:Providers:azure-openai:Model"] = "gpt-4o",
        })
        .Build();

    [TestMethod]
    public async Task BuildFullyFake_ResolvesAllServices()
    {
        await using var ctx = TestServiceBuilder.BuildFullyFake(config: BuildMinimalConfig());

        Assert.IsNotNull(ctx.Orchestrator);
        Assert.IsNotNull(ctx.DevOps);
        Assert.IsNotNull(ctx.FakeService);
        Assert.IsNotNull(ctx.FakeDevOps);
        Assert.IsFalse(ctx.UsesRealAi);
    }

    [TestMethod]
    public async Task BuildFullyFake_DevOpsIsFakeInstance()
    {
        var fakeDevOps = new FakeDevOpsService();
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps, config: BuildMinimalConfig());

        Assert.AreSame(fakeDevOps, ctx.FakeDevOps);
        Assert.AreSame(fakeDevOps, ctx.DevOps);
    }

    [TestMethod]
    public async Task BuildFullyFake_AiIsFakeInstance()
    {
        var fakeAi = new FakeCodeReviewService();
        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, config: BuildMinimalConfig());

        Assert.AreSame(fakeAi, ctx.FakeService);
    }

    [TestMethod]
    public async Task BuildFullyFake_OrchestratorUsesInjectedFakes()
    {
        var fakeDevOps = new FakeDevOpsService();
        fakeDevOps.SeedPullRequest("TestProject", "TestRepo", new PullRequestInfo
        {
            PullRequestId = 1,
            Title = "Fully Fake PR",
            Status = "active",
            LastMergeSourceCommit = "aaa111",
            LastMergeTargetCommit = "bbb222",
        });
        fakeDevOps.SeedFileChanges("TestProject", "TestRepo", 1, new List<FileChange>
        {
            new()
            {
                FilePath = "/src/Test.cs",
                ChangeType = "add",
                ModifiedContent = "public class Test { }",
                ChangedLineRanges = new List<(int, int)> { (1, 1) },
            }
        });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps, config: BuildMinimalConfig());

        // The orchestrator can resolve the PR through the fake DevOps service
        var pr = await ctx.DevOps.GetPullRequestAsync("TestProject", "TestRepo", 1);
        Assert.AreEqual("Fully Fake PR", pr.Title);
    }

    [TestMethod]
    public async Task BuildFullyFake_FakeDevOpsIsNotNull_WhileBuildWithFakeAiWouldBeNull()
    {
        // BuildFullyFake sets FakeDevOps; BuildWithFakeAi does not
        await using var ctx = TestServiceBuilder.BuildFullyFake(config: BuildMinimalConfig());

        Assert.IsNotNull(ctx.FakeDevOps, "BuildFullyFake should set FakeDevOps");
        Assert.IsNotNull(ctx.FakeService, "BuildFullyFake should set FakeService");
    }
}
