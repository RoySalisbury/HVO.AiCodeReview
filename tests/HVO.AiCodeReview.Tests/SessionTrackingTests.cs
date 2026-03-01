using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Validates Issue #10: Review Session Tracking — correlation IDs flow end-to-end.
/// All tests use <see cref="FakeDevOpsService"/> + <see cref="FakeCodeReviewService"/>
/// so they run locally without credentials.
/// </summary>
[TestCategory("Integration")]
[TestClass]
public class SessionTrackingTests
{
    // ── Model lifecycle tests ────────────────────────────────────────────

    [TestMethod]
    public void Session_NewInstance_HasUniqueId()
    {
        var s1 = new ReviewSession();
        var s2 = new ReviewSession();

        Assert.AreNotEqual(Guid.Empty, s1.SessionId);
        Assert.AreNotEqual(Guid.Empty, s2.SessionId);
        Assert.AreNotEqual(s1.SessionId, s2.SessionId, "Each session should have a distinct GUID.");
    }

    [TestMethod]
    public void Session_InitialState_IsQueued()
    {
        var s = new ReviewSession();

        Assert.AreEqual(ReviewSessionStatus.Queued, s.Status);
        Assert.AreNotEqual(default(DateTime), s.RequestedAtUtc);
        Assert.IsNull(s.CompletedAtUtc);
    }

    [TestMethod]
    public void Session_Start_TransitionsToInProgress()
    {
        var s = new ReviewSession();
        s.Start();

        Assert.AreEqual(ReviewSessionStatus.InProgress, s.Status);
        Assert.IsNull(s.CompletedAtUtc, "CompletedAtUtc should be null while in progress.");
    }

    [TestMethod]
    public void Session_Complete_TransitionsToCompleted()
    {
        var s = new ReviewSession();
        s.Start();
        s.Complete("Approved", vote: 10);

        Assert.AreEqual(ReviewSessionStatus.Completed, s.Status);
        Assert.IsNotNull(s.CompletedAtUtc);
        Assert.AreEqual("Approved", s.Verdict);
        Assert.AreEqual(10, s.Vote);
    }

    [TestMethod]
    public void Session_Fail_TransitionsToFailed()
    {
        var s = new ReviewSession();
        s.Start();
        s.Fail(new InvalidOperationException("Something broke"));

        Assert.AreEqual(ReviewSessionStatus.Failed, s.Status);
        Assert.IsNotNull(s.CompletedAtUtc);
        Assert.AreEqual("Something broke", s.ErrorMessage);
        Assert.AreEqual("InvalidOperationException", s.ErrorType);
        Assert.IsNull(s.Verdict);
    }

    [TestMethod]
    public void Session_DurationCalculation_IsPositive()
    {
        var s = new ReviewSession();
        s.Start();
        s.Complete("Approved");

        Assert.IsNotNull(s.CompletedAtUtc);
        Assert.IsTrue(s.CompletedAtUtc.Value >= s.RequestedAtUtc,
            "CompletedAtUtc should be >= RequestedAtUtc.");
        Assert.IsTrue(s.TotalDurationMs >= 0,
            "TotalDurationMs should be non-negative.");
    }

    // ── Orchestrator integration: SessionId in response ──────────────────

    [TestMethod]
    [Timeout(30_000)]
    public async Task FullReview_Response_HasSessionId()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps, project: "SessionProj", repo: "SessionRepo", prId: 100);

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);
        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            "SessionProj", "SessionRepo", 100);

        Assert.IsNotNull(result.SessionId, "SessionId should be populated in the response.");
        Assert.AreNotEqual(Guid.Empty, result.SessionId.Value);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task FullReview_ExternalSession_IsAdopted()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps, project: "SessionProj", repo: "SessionRepo", prId: 101);

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);

        var externalSession = new ReviewSession
        {
            Project = "SessionProj",
            Repository = "SessionRepo",
            PullRequestId = 101,
        };
        var externalId = externalSession.SessionId;

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            "SessionProj", "SessionRepo", 101, session: externalSession);

        Assert.AreEqual(externalId, result.SessionId,
            "When caller provides a session, its ID should be used.");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SkippedReview_Response_HasSessionId()
    {
        var fakeDevOps = new FakeDevOpsService();

        // Seed a draft PR that was already reviewed → should skip
        var prInfo = new PullRequestInfo
        {
            PullRequestId = 200,
            Title = "Already reviewed PR",
            Status = "active",
            IsDraft = false,
            LastMergeSourceCommit = "abc123",
            TargetBranch = "refs/heads/main",
            SourceBranch = "refs/heads/feature/skip-test",
        };
        fakeDevOps.SeedPullRequest("SkipProj", "SkipRepo", prInfo);
        fakeDevOps.SeedIterationCount("SkipProj", "SkipRepo", 200, 1);

        // Seed metadata showing this PR was already reviewed at the same source commit
        await fakeDevOps.SetReviewMetadataAsync("SkipProj", "SkipRepo", 200, new ReviewMetadata
        {
            ReviewCount = 1,
            LastReviewedSourceCommit = "abc123",
            LastReviewedIteration = 1,
        });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);
        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            "SkipProj", "SkipRepo", 200);

        Assert.AreEqual("Skipped", result.Status);
        Assert.IsNotNull(result.SessionId,
            "Even skipped reviews should have a SessionId for traceability.");
        Assert.AreNotEqual(Guid.Empty, result.SessionId.Value);
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SimulationReview_Response_HasSessionId()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps, project: "SimProj", repo: "SimRepo", prId: 300);

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);
        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            "SimProj", "SimRepo", 300, simulationOnly: true);

        Assert.AreEqual("Simulated", result.Status);
        Assert.IsNotNull(result.SessionId,
            "Simulation reviews should still have a SessionId.");
    }

    // ── SessionId in review history ──────────────────────────────────────

    [TestMethod]
    [Timeout(30_000)]
    public async Task FullReview_HistoryEntry_HasSessionId()
    {
        var fakeDevOps = new FakeDevOpsService();
        SeedStandardPr(fakeDevOps, project: "HistProj", repo: "HistRepo", prId: 400);

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeDevOps: fakeDevOps);
        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            "HistProj", "HistRepo", 400);

        // Retrieve the stored history from FakeDevOpsService
        var history = await fakeDevOps.GetReviewHistoryAsync("HistProj", "HistRepo", 400);
        Assert.IsTrue(history.Count > 0, "Should have at least one history entry.");

        var lastEntry = history[^1];
        Assert.IsNotNull(lastEntry.SessionId,
            "History entry should contain the SessionId for correlation.");
        Assert.AreEqual(result.SessionId, lastEntry.SessionId,
            "History and response should share the same SessionId.");
    }

    // ── SessionId in summary markdown ────────────────────────────────────

    [TestMethod]
    public void BuildSummaryMarkdown_IncludesSessionId()
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                Verdict = "APPROVED WITH SUGGESTIONS",
                Description = "Looks good overall.",
                VerdictJustification = "Minor issues only.",
            },
            InlineComments = new List<InlineComment>(),
            FileReviews = new List<FileReview>(),
            RecommendedVote = 5,
        };
        var sessionId = Guid.NewGuid();

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(
            pullRequestId: 42, result: result, sessionId: sessionId);

        Assert.IsTrue(markdown.Contains(sessionId.ToString()),
            "Summary markdown should contain the session ID in the footer.");
        Assert.IsTrue(markdown.Contains("<sub>Session:"),
            "Session ID should be rendered in a <sub> tag.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_OmitsSessionId_WhenNull()
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                Verdict = "APPROVED",
                Description = "All good.",
                VerdictJustification = "No issues.",
            },
            InlineComments = new List<InlineComment>(),
            FileReviews = new List<FileReview>(),
            RecommendedVote = 10,
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(
            pullRequestId: 43, result: result, sessionId: null);

        Assert.IsFalse(markdown.Contains("<sub>Session:"),
            "Summary markdown should not contain session footer when no session ID is provided.");
    }

    // ── Helper ───────────────────────────────────────────────────────────

    /// <summary>
    /// Seeds a minimal active PR with one reviewable file change so the orchestrator
    /// goes through the full review path.
    /// </summary>
    private static void SeedStandardPr(FakeDevOpsService fakeDevOps,
        string project, string repo, int prId)
    {
        var prInfo = new PullRequestInfo
        {
            PullRequestId = prId,
            Title = $"Test PR {prId}",
            Status = "active",
            IsDraft = false,
            LastMergeSourceCommit = $"commit-{prId}",
            TargetBranch = "refs/heads/main",
            SourceBranch = $"refs/heads/feature/session-{prId}",
        };
        fakeDevOps.SeedPullRequest(project, repo, prInfo);
        fakeDevOps.SeedFileChanges(project, repo, prId, new List<FileChange>
        {
            new()
            {
                FilePath = "/src/Example.cs",
                ChangeType = "edit",
                OriginalContent = "// old",
                ModifiedContent = "// new",
            },
        });
        fakeDevOps.SeedIterationCount(project, repo, prId, 1);
    }
}
