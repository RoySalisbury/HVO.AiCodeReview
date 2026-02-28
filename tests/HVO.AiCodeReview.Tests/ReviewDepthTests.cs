using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Issue #9 — Review Depth Modes (Quick / Standard / Deep).
/// Covers depth enum parsing, orchestrator routing, summary markdown badges,
/// and the Quick/Deep specific code paths.
/// </summary>
[TestClass]
public class ReviewDepthTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Unit Tests — ReviewDepth enum + request model
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewRequest_DefaultDepth_IsStandard()
    {
        var request = new ReviewRequest();
        Assert.AreEqual(ReviewDepth.Standard, request.ReviewDepth);
    }

    [TestMethod]
    [DataRow("Quick", ReviewDepth.Quick)]
    [DataRow("Standard", ReviewDepth.Standard)]
    [DataRow("Deep", ReviewDepth.Deep)]
    public void ReviewDepth_ParsesFromString(string input, ReviewDepth expected)
    {
        var parsed = Enum.Parse<ReviewDepth>(input, ignoreCase: true);
        Assert.AreEqual(expected, parsed);
    }

    [TestMethod]
    public void ReviewDepth_SerializesToString()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(ReviewDepth.Quick);
        Assert.AreEqual("\"Quick\"", json);
    }

    [TestMethod]
    public void ReviewRequest_RoundTripsDepthAsJson()
    {
        var request = new ReviewRequest
        {
            ProjectName = "Test",
            RepositoryName = "Repo",
            PullRequestId = 1,
            ReviewDepth = ReviewDepth.Deep,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(request);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ReviewRequest>(json)!;

        Assert.AreEqual(ReviewDepth.Deep, roundTripped.ReviewDepth);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Unit Tests — BuildSummaryMarkdown with depth badges
    // ═══════════════════════════════════════════════════════════════════════

    private static CodeReviewResult MakeResult(string verdict = "APPROVED", int inlineCount = 0)
    {
        return new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 2,
                EditsCount = 1,
                AddsCount = 1,
                Description = "Test description.",
                Verdict = verdict,
                VerdictJustification = "Test justification.",
            },
            InlineComments = Enumerable.Range(0, inlineCount).Select(i => new InlineComment
            {
                FilePath = "test.cs",
                StartLine = i + 5,
                EndLine = i + 5,
                LeadIn = "Suggestion",
                Comment = $"Comment {i}",
                Status = "closed",
            }).ToList(),
        };
    }

    [TestMethod]
    public void BuildSummary_QuickMode_ShowsZapBadge()
    {
        var result = MakeResult();
        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, reviewDepth: ReviewDepth.Quick);

        Assert.IsTrue(md.Contains(":zap: Quick"), "Should contain Quick badge");
        Assert.IsTrue(md.Contains("Code Review"), "Should contain Code Review header");
    }

    [TestMethod]
    public void BuildSummary_DeepMode_ShowsMagBadge()
    {
        var result = MakeResult();
        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, reviewDepth: ReviewDepth.Deep);

        Assert.IsTrue(md.Contains(":mag: Deep"), "Should contain Deep badge");
    }

    [TestMethod]
    public void BuildSummary_StandardMode_NoBadge()
    {
        var result = MakeResult();
        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, reviewDepth: ReviewDepth.Standard);

        Assert.IsFalse(md.Contains(":zap:"), "Standard mode should not have Quick badge");
        Assert.IsFalse(md.Contains(":mag:"), "Standard mode should not have Deep badge");
    }

    [TestMethod]
    public void BuildSummary_DeepMode_WithDeepAnalysis_ShowsSection()
    {
        var result = MakeResult();
        var deepAnalysis = new DeepAnalysisResult
        {
            ExecutiveSummary = "Overall the PR looks solid.",
            CrossFileIssues = new List<CrossFileIssue>
            {
                new CrossFileIssue
                {
                    Files = new List<string> { "A.cs", "B.cs" },
                    Severity = "Warning",
                    Description = "Interface mismatch between A and B.",
                }
            },
            VerdictConsistency = new VerdictConsistencyAssessment
            {
                IsConsistent = true,
                Explanation = "Verdicts are consistent.",
            },
            OverallRiskLevel = "Medium",
            Recommendations = new List<string>
            {
                "Add integration tests for the A→B interaction.",
            },
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, reviewDepth: ReviewDepth.Deep, deepAnalysis: deepAnalysis);

        Assert.IsTrue(md.Contains("Deep Analysis (Pass 3"), "Should have Deep Analysis section");
        Assert.IsTrue(md.Contains("Executive Summary"), "Should show executive summary");
        Assert.IsTrue(md.Contains("Overall Risk Level"), "Should show risk level");
        Assert.IsTrue(md.Contains("Interface mismatch"), "Should show cross-file issues");
        Assert.IsTrue(md.Contains("integration tests"), "Should show recommendations");
    }

    [TestMethod]
    public void BuildSummary_DeepMode_WithVerdictOverride_ShowsWarning()
    {
        var result = MakeResult();
        var deepAnalysis = new DeepAnalysisResult
        {
            ExecutiveSummary = "PR has issues.",
            VerdictConsistency = new VerdictConsistencyAssessment
            {
                IsConsistent = false,
                Explanation = "Per-file reviews missed a critical integration bug.",
                RecommendedVerdict = "NEEDS WORK",
                RecommendedVote = -5,
            },
            OverallRiskLevel = "High",
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, reviewDepth: ReviewDepth.Deep, deepAnalysis: deepAnalysis);

        Assert.IsTrue(md.Contains(":warning: **Verdict Override**"), "Should show verdict override warning");
        Assert.IsTrue(md.Contains("critical integration bug"), "Should show override explanation");
    }

    [TestMethod]
    public void BuildSummary_DeepMode_WithoutDeepAnalysis_NoSection()
    {
        var result = MakeResult();
        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, reviewDepth: ReviewDepth.Deep, deepAnalysis: null);

        Assert.IsFalse(md.Contains("Deep Analysis"), "Should not have Deep Analysis section when null");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Unit Tests — ReviewResponse includes depth
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewResponse_IncludesDepth()
    {
        var response = new ReviewResponse { ReviewDepth = "Quick" };
        Assert.AreEqual("Quick", response.ReviewDepth);
    }

    [TestMethod]
    public void ReviewHistoryEntry_IncludesDepth()
    {
        var entry = new ReviewHistoryEntry { ReviewDepth = "Deep" };
        Assert.AreEqual("Deep", entry.ReviewDepth);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Unit Tests — DeepAnalysisResult model
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void DeepAnalysisResult_Defaults()
    {
        var result = new DeepAnalysisResult();
        Assert.AreEqual("Low", result.OverallRiskLevel);
        Assert.AreEqual(0, result.CrossFileIssues.Count);
        Assert.AreEqual(0, result.Recommendations.Count);
        Assert.IsTrue(result.VerdictConsistency.IsConsistent);
    }

    [TestMethod]
    public void DeepAnalysisResult_RoundTripsJson()
    {
        var result = new DeepAnalysisResult
        {
            ExecutiveSummary = "Test summary",
            CrossFileIssues = new List<CrossFileIssue>
            {
                new CrossFileIssue
                {
                    Files = new List<string> { "a.cs", "b.cs" },
                    Severity = "Error",
                    Description = "Test issue",
                }
            },
            VerdictConsistency = new VerdictConsistencyAssessment
            {
                IsConsistent = false,
                Explanation = "Mismatch",
                RecommendedVerdict = "NEEDS WORK",
                RecommendedVote = -5,
            },
            OverallRiskLevel = "High",
            Recommendations = new List<string> { "Fix the issue" },
        };

        var json = System.Text.Json.JsonSerializer.Serialize(result);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<DeepAnalysisResult>(json)!;

        Assert.AreEqual("Test summary", roundTripped.ExecutiveSummary);
        Assert.AreEqual(1, roundTripped.CrossFileIssues.Count);
        Assert.AreEqual("Error", roundTripped.CrossFileIssues[0].Severity);
        Assert.IsFalse(roundTripped.VerdictConsistency.IsConsistent);
        Assert.AreEqual("NEEDS WORK", roundTripped.VerdictConsistency.RecommendedVerdict);
        Assert.AreEqual(-5, roundTripped.VerdictConsistency.RecommendedVote);
        Assert.AreEqual("High", roundTripped.OverallRiskLevel);
        Assert.AreEqual(1, roundTripped.Recommendations.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Unit Tests — ReviewProfile includes MaxOutputTokensDeepAnalysis
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewProfile_DeepAnalysisTokens_HasDefault()
    {
        var profile = new ReviewProfile();
        Assert.AreEqual(6000, profile.MaxOutputTokensDeepAnalysis);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Integration Tests — Orchestrator with Fake AI (Quick mode)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("QuickReview")]
    [Timeout(120_000)]
    public async Task QuickMode_SkipsPass2_ReturnsPass1Only()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Quick);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual("Quick", response.ReviewDepth);
        Assert.AreEqual(0, response.IssueCount, "Quick mode should have 0 inline comments");

        // Verdict should still be populated from Pass 1
        Assert.IsNotNull(response.Recommendation, "Should have a recommendation");
        Assert.IsNotNull(response.Summary, "Should have a summary");
        Assert.IsTrue(response.Summary!.Contains(":zap: Quick"), "Summary should contain Quick badge");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("QuickReview")]
    [Timeout(120_000)]
    public async Task QuickMode_Pass1Fails_VerdictAndVoteAreConsistent()
    {
        // Simulate Pass 1 returning null (e.g. AI timeout / error)
        var fake = new FakeCodeReviewService();
        fake.PrSummaryFactory = (_, _, _) => null;

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Quick);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual("Quick", response.ReviewDepth);
        Assert.AreEqual(0, response.IssueCount, "Quick mode should have 0 inline comments");

        // When Pass 1 fails, both verdict and vote should agree on "Approved with Suggestions" / 5
        Assert.AreEqual("ApprovedWithSuggestions", response.Recommendation,
            "Null summary should give 'Approved with Suggestions' — limited analysis can't confirm full approval.");
        Assert.AreEqual(5, response.Vote,
            "Vote should be 5 (Approved with Suggestions), consistent with the verdict.");

        // Summary should still have Quick badge and mention limited analysis
        Assert.IsNotNull(response.Summary);
        Assert.IsTrue(response.Summary!.Contains(":zap: Quick"), "Summary should contain Quick badge");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Integration Tests — Orchestrator with Fake AI (Standard mode — default)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("StandardReview")]
    [Timeout(120_000)]
    public async Task StandardMode_IsDefault_RunsPass1And2()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Standard);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual("Standard", response.ReviewDepth);

        // Standard mode should have inline comments from Pass 2
        Assert.IsTrue(response.IssueCount > 0, "Standard mode should have inline comments from Pass 2");
        Assert.IsNotNull(response.Summary, "Should have a summary");
        Assert.IsFalse(response.Summary!.Contains(":zap:"), "No Quick badge in Standard mode");
        Assert.IsFalse(response.Summary!.Contains(":mag:"), "No Deep badge in Standard mode");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Integration Tests — Orchestrator with Fake AI (Deep mode)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_RunsAllThreePasses()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Deep);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual("Deep", response.ReviewDepth);

        // Deep mode should have inline comments from Pass 2
        Assert.IsTrue(response.IssueCount > 0, "Deep mode should have inline comments from Pass 2");
        Assert.IsNotNull(response.Summary, "Should have a summary");
        Assert.IsTrue(response.Summary!.Contains(":mag: Deep"), "Summary should contain Deep badge");
        Assert.IsTrue(response.Summary!.Contains("Deep Analysis"), "Should contain Deep Analysis section");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_VerdictOverride_AppliesWhenInconsistent()
    {
        var fake = new FakeCodeReviewService();

        // Configure deep analysis factory to return an inconsistent verdict
        fake.DeepAnalysisFactory = (_, _, _, _) => new DeepAnalysisResult
        {
            ExecutiveSummary = "Critical cross-file issue found.",
            CrossFileIssues = new List<CrossFileIssue>
            {
                new CrossFileIssue
                {
                    Files = new List<string> { "A.cs", "B.cs" },
                    Severity = "Error",
                    Description = "Interface contract broken between A and B.",
                }
            },
            VerdictConsistency = new VerdictConsistencyAssessment
            {
                IsConsistent = false,
                Explanation = "Per-file reviews gave APPROVED but there's a critical cross-file bug.",
                RecommendedVerdict = "NEEDS WORK",
                RecommendedVote = -5,
            },
            OverallRiskLevel = "High",
            Recommendations = new List<string> { "Fix the interface contract." },
        };

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Deep);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual("Deep", response.ReviewDepth);

        // Verdict should be overridden
        Assert.AreEqual("NeedsWork", response.Recommendation,
            "Deep mode should override verdict to NeedsWork when inconsistent");
        Assert.IsTrue(response.Summary!.Contains("Verdict Override"),
            "Summary should show verdict override warning");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Unit Tests — FakeCodeReviewService deep analysis
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FakeService_GenerateDeepAnalysis_ReturnsDeterministic()
    {
        var fake = new FakeCodeReviewService();
        var pr = new PullRequestInfo
        {
            PullRequestId = 1,
            Title = "Test PR",
            CreatedBy = "test-author",
        };
        var files = new List<FileChange>
        {
            new FileChange { FilePath = "test.cs", ChangeType = "edit" },
        };
        var reviewResult = MakeResult();

        var result = await fake.GenerateDeepAnalysisAsync(pr, null, reviewResult, files);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.VerdictConsistency.IsConsistent);
        Assert.AreEqual("Low", result.OverallRiskLevel);
        Assert.IsTrue(result.CrossFileIssues.Count > 0);
        Assert.IsTrue(result.Recommendations.Count > 0);
    }

    [TestMethod]
    public async Task FakeService_DeepAnalysisFactory_Override()
    {
        var fake = new FakeCodeReviewService();
        fake.DeepAnalysisFactory = (_, _, _, _) => new DeepAnalysisResult
        {
            ExecutiveSummary = "Custom deep analysis.",
            OverallRiskLevel = "Critical",
        };

        var pr = new PullRequestInfo { PullRequestId = 1, Title = "Test", CreatedBy = "test" };
        var result = await fake.GenerateDeepAnalysisAsync(pr, null, MakeResult(), new List<FileChange>());

        Assert.IsNotNull(result);
        Assert.AreEqual("Custom deep analysis.", result.ExecutiveSummary);
        Assert.AreEqual("Critical", result.OverallRiskLevel);
    }
}
