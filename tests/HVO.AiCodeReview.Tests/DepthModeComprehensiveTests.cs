using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Comprehensive test coverage for Issue #33 — Review Depth Modes &amp; Configurations.
/// Covers Quick-mode verdict derivation, Standard/Deep pass orchestration,
/// BuildSummaryMarkdown edge cases, no-reviewable-files paths, and re-review × depth interactions.
/// </summary>
[TestClass]
public class DepthModeComprehensiveTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
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

    private static List<FileChange> MakeFileChanges(params (string path, string changeType)[] files)
        => files.Select(f => new FileChange { FilePath = f.path, ChangeType = f.changeType }).ToList();

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 1: Quick-Mode Verdict Derivation (DeriveQuick* unit tests)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void DeriveQuickVerdict_NullSummary_ReturnsApprovedWithSuggestions()
    {
        var result = CodeReviewOrchestrator.DeriveQuickVerdict(null);
        Assert.AreEqual("APPROVED WITH SUGGESTIONS", result);
    }

    [TestMethod]
    public void DeriveQuickVerdict_ZeroRisks_ReturnsApproved()
    {
        var summary = new PrSummaryResult { RiskAreas = new List<RiskArea>() };
        var result = CodeReviewOrchestrator.DeriveQuickVerdict(summary);
        Assert.AreEqual("APPROVED", result);
    }

    [TestMethod]
    public void DeriveQuickVerdict_OneRisk_ReturnsApprovedWithSuggestions()
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = new List<RiskArea> { new() { Area = "a.cs", Reason = "risk" } }
        };
        Assert.AreEqual("APPROVED WITH SUGGESTIONS", CodeReviewOrchestrator.DeriveQuickVerdict(summary));
    }

    [TestMethod]
    public void DeriveQuickVerdict_TwoRisks_ReturnsApprovedWithSuggestions()
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "a.cs", Reason = "r1" },
                new() { Area = "b.cs", Reason = "r2" },
            }
        };
        Assert.AreEqual("APPROVED WITH SUGGESTIONS", CodeReviewOrchestrator.DeriveQuickVerdict(summary));
    }

    [TestMethod]
    public void DeriveQuickVerdict_ThreeRisks_ReturnsNeedsWork()
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "a.cs", Reason = "r1" },
                new() { Area = "b.cs", Reason = "r2" },
                new() { Area = "c.cs", Reason = "r3" },
            }
        };
        Assert.AreEqual("NEEDS WORK", CodeReviewOrchestrator.DeriveQuickVerdict(summary));
    }

    [TestMethod]
    public void DeriveQuickVerdict_FiveRisks_ReturnsNeedsWork()
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = Enumerable.Range(1, 5)
                .Select(i => new RiskArea { Area = $"f{i}.cs", Reason = $"risk {i}" })
                .ToList()
        };
        Assert.AreEqual("NEEDS WORK", CodeReviewOrchestrator.DeriveQuickVerdict(summary));
    }

    // ── DeriveQuickVote ─────────────────────────────────────────────────

    [TestMethod]
    public void DeriveQuickVote_NullSummary_Returns5()
    {
        Assert.AreEqual(5, CodeReviewOrchestrator.DeriveQuickVote(null));
    }

    [TestMethod]
    public void DeriveQuickVote_ZeroRisks_Returns10()
    {
        var summary = new PrSummaryResult { RiskAreas = new List<RiskArea>() };
        Assert.AreEqual(10, CodeReviewOrchestrator.DeriveQuickVote(summary));
    }

    [TestMethod]
    public void DeriveQuickVote_TwoRisks_Returns5()
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "a.cs", Reason = "r1" },
                new() { Area = "b.cs", Reason = "r2" },
            }
        };
        Assert.AreEqual(5, CodeReviewOrchestrator.DeriveQuickVote(summary));
    }

    [TestMethod]
    public void DeriveQuickVote_ThreeRisks_ReturnsNeg5()
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "a.cs", Reason = "r1" },
                new() { Area = "b.cs", Reason = "r2" },
                new() { Area = "c.cs", Reason = "r3" },
            }
        };
        Assert.AreEqual(-5, CodeReviewOrchestrator.DeriveQuickVote(summary));
    }

    // ── DeriveQuickJustification ────────────────────────────────────────

    [TestMethod]
    public void DeriveQuickJustification_NullSummary_MentionsLimitedAnalysis()
    {
        var result = CodeReviewOrchestrator.DeriveQuickJustification(null);
        Assert.IsTrue(result.Contains("limited analysis"), $"Expected 'limited analysis' in: {result}");
        Assert.IsTrue(result.Contains("Quick review"), $"Expected 'Quick review' in: {result}");
    }

    [TestMethod]
    public void DeriveQuickJustification_ZeroRisks_MentionsNoRisks()
    {
        var summary = new PrSummaryResult
        {
            Intent = "Adds logging",
            RiskAreas = new List<RiskArea>()
        };
        var result = CodeReviewOrchestrator.DeriveQuickJustification(summary);
        Assert.IsTrue(result.Contains("no significant risks"), $"Expected 'no significant risks' in: {result}");
        Assert.IsTrue(result.Contains("Adds logging"), "Should include intent");
    }

    [TestMethod]
    public void DeriveQuickJustification_WithRisks_ListsRiskAreas()
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "auth.cs", Reason = "bypasses validation" },
                new() { Area = "db.cs", Reason = "raw SQL" },
            }
        };
        var result = CodeReviewOrchestrator.DeriveQuickJustification(summary);
        Assert.IsTrue(result.Contains("2 risk area(s)"), $"Expected '2 risk area(s)' in: {result}");
        Assert.IsTrue(result.Contains("auth.cs"), "Should list first risk area");
        Assert.IsTrue(result.Contains("raw SQL"), "Should list second risk reason");
    }

    // ── Verdict + Vote + Justification consistency ──────────────────────

    [TestMethod]
    [DataRow(0, "APPROVED", 10)]
    [DataRow(1, "APPROVED WITH SUGGESTIONS", 5)]
    [DataRow(2, "APPROVED WITH SUGGESTIONS", 5)]
    [DataRow(3, "NEEDS WORK", -5)]
    [DataRow(4, "NEEDS WORK", -5)]
    public void QuickDeriveTriple_VerdictVoteConsistency(int riskCount, string expectedVerdict, int expectedVote)
    {
        var summary = new PrSummaryResult
        {
            RiskAreas = Enumerable.Range(0, riskCount)
                .Select(i => new RiskArea { Area = $"f{i}.cs", Reason = $"r{i}" })
                .ToList()
        };

        Assert.AreEqual(expectedVerdict, CodeReviewOrchestrator.DeriveQuickVerdict(summary));
        Assert.AreEqual(expectedVote, CodeReviewOrchestrator.DeriveQuickVote(summary));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 2: BuildQuickModeResult unit tests
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildQuickModeResult_ZeroRisks_AllFilesSkipped()
    {
        var summary = new PrSummaryResult
        {
            Intent = "Adds logging",
            RiskAreas = new List<RiskArea>()
        };
        var files = MakeFileChanges(("a.cs", "edit"), ("b.cs", "add"));

        var result = CodeReviewOrchestrator.BuildQuickModeResult(summary, files, new List<FileChange>());

        Assert.AreEqual("APPROVED", result.Summary.Verdict);
        Assert.AreEqual(10, result.RecommendedVote);
        Assert.AreEqual(0, result.InlineComments.Count, "Quick mode should have no inline comments");
        Assert.AreEqual(2, result.FileReviews.Count);
        Assert.IsTrue(result.FileReviews.All(fr => fr.Verdict == "SKIPPED"), "All file reviews should be SKIPPED");
    }

    [TestMethod]
    public void BuildQuickModeResult_ChangeTypeCounts_AreCorrect()
    {
        var summary = new PrSummaryResult { RiskAreas = new List<RiskArea>() };
        var files = MakeFileChanges(
            ("a.cs", "edit"),
            ("b.cs", "add"),
            ("c.cs", "delete"),
            ("d.cs", "edit"),
            ("e.cs", "mystery")); // unknown type → treated as edit

        var result = CodeReviewOrchestrator.BuildQuickModeResult(summary, files, new List<FileChange>());

        Assert.AreEqual(5, result.Summary.FilesChanged);
        Assert.AreEqual(3, result.Summary.EditsCount, "2 edits + 1 unknown (mystery) → 3 edits");
        Assert.AreEqual(1, result.Summary.AddsCount);
        Assert.AreEqual(1, result.Summary.DeletesCount);
    }

    [TestMethod]
    public void BuildQuickModeResult_NullSummary_UsesDefaultDescription()
    {
        var files = MakeFileChanges(("x.cs", "add"));

        var result = CodeReviewOrchestrator.BuildQuickModeResult(null, files, new List<FileChange>());

        Assert.IsTrue(result.Summary.Description.Contains("Quick mode review"),
            "Null summary should produce default description");
        Assert.AreEqual("APPROVED WITH SUGGESTIONS", result.Summary.Verdict);
        Assert.AreEqual(5, result.RecommendedVote);
    }

    [TestMethod]
    public void BuildQuickModeResult_WithSkippedFiles_MentionsInDescription()
    {
        var summary = new PrSummaryResult { Intent = "Bug fix", RiskAreas = new List<RiskArea>() };
        var files = MakeFileChanges(("code.cs", "edit"));
        var skipped = MakeFileChanges(("logo.png", "add"), ("readme.md", "edit"));

        var result = CodeReviewOrchestrator.BuildQuickModeResult(summary, files, skipped);

        Assert.IsTrue(result.Summary.Description.Contains("2 non-reviewable file(s) excluded"),
            $"Description should mention skipped files: {result.Summary.Description}");
    }

    [TestMethod]
    public void BuildQuickModeResult_ManyRisks_VerdictIsNeedsWork()
    {
        var summary = new PrSummaryResult
        {
            Intent = "Major refactor",
            RiskAreas = Enumerable.Range(0, 4)
                .Select(i => new RiskArea { Area = $"f{i}.cs", Reason = $"risk{i}" })
                .ToList()
        };
        var files = MakeFileChanges(("f0.cs", "edit"), ("f1.cs", "edit"));

        var result = CodeReviewOrchestrator.BuildQuickModeResult(summary, files, new List<FileChange>());

        Assert.AreEqual("NEEDS WORK", result.Summary.Verdict);
        Assert.AreEqual(-5, result.RecommendedVote);
        Assert.IsTrue(result.Summary.VerdictJustification.Contains("4 risk area(s)"));
    }

    [TestMethod]
    public void BuildQuickModeResult_FileReviewPaths_MatchInputFiles()
    {
        var summary = new PrSummaryResult { RiskAreas = new List<RiskArea>() };
        var files = MakeFileChanges(("src/a.cs", "edit"), ("src/b.cs", "add"), ("tests/t.cs", "add"));

        var result = CodeReviewOrchestrator.BuildQuickModeResult(summary, files, new List<FileChange>());

        var paths = result.FileReviews.Select(fr => fr.FilePath).ToList();
        CollectionAssert.AreEqual(
            new[] { "src/a.cs", "src/b.cs", "tests/t.cs" }, paths,
            "FileReview paths should match input file paths");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 3: BuildSummaryMarkdown — depth-specific edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildSummary_DeepAnalysis_NoCrossFileIssues_OmitsCrossFileSection()
    {
        var result = MakeResult();
        var deep = new DeepAnalysisResult
        {
            ExecutiveSummary = "All good.",
            CrossFileIssues = new List<CrossFileIssue>(),
            VerdictConsistency = new VerdictConsistencyAssessment { IsConsistent = true },
            OverallRiskLevel = "Low",
            Recommendations = new List<string> { "Add more tests." },
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            1, result, reviewDepth: ReviewDepth.Deep, deepAnalysis: deep);

        Assert.IsTrue(md.Contains("Deep Analysis"), "Deep analysis section should be present");
        Assert.IsTrue(md.Contains("All good."), "Executive summary should appear");
        Assert.IsTrue(md.Contains("Low"), "Risk level should appear");
        Assert.IsTrue(md.Contains("Add more tests"), "Recommendations should appear");
        Assert.IsFalse(md.Contains("Cross-File Issues"),
            "No Cross-File Issues subsection when list is empty");
    }

    [TestMethod]
    public void BuildSummary_DeepAnalysis_NoRecommendations_StillRendersSection()
    {
        var result = MakeResult();
        var deep = new DeepAnalysisResult
        {
            ExecutiveSummary = "Minor changes.",
            CrossFileIssues = new List<CrossFileIssue>
            {
                new CrossFileIssue
                {
                    Files = new List<string> { "x.cs" },
                    Severity = "Info",
                    Description = "Observation only.",
                }
            },
            VerdictConsistency = new VerdictConsistencyAssessment { IsConsistent = true },
            OverallRiskLevel = "Low",
            Recommendations = new List<string>(),
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            1, result, reviewDepth: ReviewDepth.Deep, deepAnalysis: deep);

        Assert.IsTrue(md.Contains("Deep Analysis"), "Deep analysis section present");
        Assert.IsTrue(md.Contains("Observation only."), "Cross-file issues should appear");
    }

    [TestMethod]
    public void BuildSummary_DeepAnalysis_ConsistentVerdict_NoOverrideWarning()
    {
        var result = MakeResult();
        var deep = new DeepAnalysisResult
        {
            ExecutiveSummary = "Consistent review.",
            VerdictConsistency = new VerdictConsistencyAssessment
            {
                IsConsistent = true,
                Explanation = "All verdicts agree.",
            },
            OverallRiskLevel = "Low",
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            1, result, reviewDepth: ReviewDepth.Deep, deepAnalysis: deep);

        Assert.IsFalse(md.Contains("Verdict Override"),
            "Consistent verdict should not show override warning");
    }

    [TestMethod]
    public void BuildSummary_QuickMode_WithSkippedFiles_ShowsBadgeAndExcludedNote()
    {
        var result = MakeResult();
        var skipped = new List<FileChange>
        {
            new FileChange { FilePath = "image.png", ChangeType = "add" },
            new FileChange { FilePath = "data.bin", ChangeType = "add" },
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            1, result, reviewDepth: ReviewDepth.Quick, skippedFiles: skipped);

        Assert.IsTrue(md.Contains(":zap: Quick"), "Quick badge should be present");
        Assert.IsTrue(md.Contains("2 file(s) excluded"),
            "Skipped files count should appear in the summary");
    }

    [TestMethod]
    public void BuildSummary_ReReview_WithDeepMode_ShowsBothBadgeAndReReviewHeader()
    {
        var result = MakeResult();
        var priorMeta = new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow.AddHours(-1),
            LastReviewedSourceCommit = "abc123",
            LastReviewedIteration = 1,
            VoteSubmitted = true,
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, isReReview: true, reviewNumber: 2,
            priorMetadata: priorMeta, reviewDepth: ReviewDepth.Deep);

        Assert.IsTrue(md.Contains(":mag: Deep"), "Deep badge should be present");
        Assert.IsTrue(md.Contains("Re-Review"), "Re-review header should be present");
    }

    [TestMethod]
    public void BuildSummary_PrSummary_IncludesCrossFileAnalysis()
    {
        var result = MakeResult();
        var prSummary = new PrSummaryResult
        {
            Intent = "Adds rate limiting to the API",
            ArchitecturalImpact = "Introduces middleware layer",
            CrossFileRelationships = new List<string>
            {
                "RateLimiter.cs depends on Config.cs for threshold values"
            },
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "RateLimiter.cs", Reason = "Complex concurrency logic" },
            }
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            1, result, prSummary: prSummary);

        Assert.IsTrue(md.Contains("Cross-File Analysis"), "Should have Cross-File Analysis section");
        Assert.IsTrue(md.Contains("Introduces middleware layer"), "Architectural impact should appear");
        Assert.IsTrue(md.Contains("RateLimiter.cs depends on Config.cs"), "Cross-file relationships should appear");
        Assert.IsTrue(md.Contains("RateLimiter.cs"), "Risk area should appear");
        Assert.IsTrue(md.Contains("Complex concurrency logic"), "Risk reason should appear");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 4: Integration — Deep mode Pass 3 failure handling
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_Pass3ReturnsNull_FallsBackToStandard()
    {
        var fake = new FakeCodeReviewService();
        fake.DeepAnalysisFactory = (_, _, _, _) => null;

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Deep);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual("Deep", response.ReviewDepth);
        // Should still have standard inline comments from Pass 2
        Assert.IsTrue(response.IssueCount > 0, "Pass 2 inline comments should still be present");
        // Summary should NOT have Deep Analysis section
        Assert.IsFalse(response.Summary!.Contains("Deep Analysis"),
            "Null deep analysis should not render Deep Analysis section");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_Pass3Throws_FallsBackToStandard()
    {
        var fake = new FakeCodeReviewService();
        fake.DeepAnalysisFactory = (_, _, _, _) => throw new InvalidOperationException("AI exploded");

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Deep);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual("Deep", response.ReviewDepth);
        // Should still have standard inline comments from Pass 2
        Assert.IsTrue(response.IssueCount > 0, "Pass 2 inline comments should survive Pass 3 failure");
        // Summary should NOT have Deep Analysis section
        Assert.IsFalse(response.Summary!.Contains("Deep Analysis"),
            "Failed deep analysis should not render Deep Analysis section");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 5: Integration — Deep mode verdict override scenarios
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_VerdictOverride_ToRejected()
    {
        var fake = new FakeCodeReviewService();
        fake.DeepAnalysisFactory = (_, _, _, _) => new DeepAnalysisResult
        {
            ExecutiveSummary = "Critical security flaw found across files.",
            CrossFileIssues = new List<CrossFileIssue>
            {
                new() { Files = new List<string> { "Auth.cs", "Token.cs" }, Severity = "Critical", Description = "Bypass" }
            },
            VerdictConsistency = new VerdictConsistencyAssessment
            {
                IsConsistent = false,
                Explanation = "Per-file gave APPROVED but cross-file reveals auth bypass.",
                RecommendedVerdict = "REJECTED",
                RecommendedVote = -10,
            },
            OverallRiskLevel = "Critical",
        };

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Deep);

        Assert.AreEqual("Rejected", response.Recommendation,
            "Deep analysis should override verdict to Rejected");
        Assert.AreEqual(-10, response.Vote,
            "Vote should be overridden to -10");
        Assert.IsTrue(response.Summary!.Contains("Verdict Override"),
            "Summary should show verdict override warning");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_ConsistentVerdict_NoOverride()
    {
        var fake = new FakeCodeReviewService();
        fake.DeepAnalysisFactory = (_, _, _, _) => new DeepAnalysisResult
        {
            ExecutiveSummary = "All changes look good, consistent with per-file verdicts.",
            VerdictConsistency = new VerdictConsistencyAssessment
            {
                IsConsistent = true,
                Explanation = "All verdicts agree — APPROVED WITH SUGGESTIONS.",
            },
            OverallRiskLevel = "Low",
            Recommendations = new List<string> { "Consider adding unit tests." },
        };

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Deep);

        // Default fake service returns "APPROVED WITH SUGGESTIONS" / vote 5
        Assert.AreEqual("ApprovedWithSuggestions", response.Recommendation,
            "Consistent verdict should not be overridden");
        Assert.AreEqual(5, response.Vote, "Vote should remain the per-file vote");
        Assert.IsFalse(response.Summary!.Contains("Verdict Override"),
            "No override warning for consistent verdict");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 6: Integration — Quick mode end-to-end
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("QuickReview")]
    [Timeout(120_000)]
    public async Task QuickMode_ResponseHasNoInlineComments()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Quick);

        Assert.AreEqual(0, response.IssueCount, "Quick mode should report 0 issues");
        Assert.IsNotNull(response.InlineComments, "InlineComments list should be populated (empty)");
        Assert.AreEqual(0, response.InlineComments!.Count, "InlineComments list should be empty");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("QuickReview")]
    [Timeout(120_000)]
    public async Task QuickMode_FileReviewsAllSkipped()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Quick);

        Assert.IsNotNull(response.FileReviews, "FileReviews should be populated in simulation mode");
        Assert.IsTrue(response.FileReviews!.Count > 0, "Should have at least one file review");
        Assert.IsTrue(response.FileReviews.All(fr => fr.Verdict == "SKIPPED"),
            "All file reviews should have SKIPPED verdict in Quick mode");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("QuickReview")]
    [Timeout(120_000)]
    public async Task QuickMode_ZeroRisksPr_GetsFullApproval()
    {
        var fake = new FakeCodeReviewService();
        fake.PrSummaryFactory = (_, _, _) => new PrSummaryResult
        {
            Intent = "Documentation-only change",
            RiskAreas = new List<RiskArea>(), // zero risks
        };

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Quick);

        Assert.AreEqual("Approved", response.Recommendation,
            "Zero risks should yield full Approved");
        Assert.AreEqual(10, response.Vote, "Zero risks should yield vote 10");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("QuickReview")]
    [Timeout(120_000)]
    public async Task QuickMode_ManyRisksPr_GetsNeedsWork()
    {
        var fake = new FakeCodeReviewService();
        fake.PrSummaryFactory = (_, files, _) => new PrSummaryResult
        {
            Intent = "Major refactor",
            RiskAreas = Enumerable.Range(0, 5)
                .Select(i => new RiskArea { Area = $"risk{i}.cs", Reason = $"High risk area {i}" })
                .ToList()
        };

        await using var ctx = TestServiceBuilder.BuildWithFakeAi(fakeService: fake);
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Quick);

        Assert.AreEqual("NeedsWork", response.Recommendation,
            "5 risks should yield NeedsWork");
        Assert.AreEqual(-5, response.Vote, "5 risks should yield vote -5");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 7: Integration — Standard mode depth in response
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("StandardReview")]
    [Timeout(120_000)]
    public async Task StandardMode_ResponseIncludesInlineComments()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Standard);

        Assert.AreEqual("Standard", response.ReviewDepth);
        Assert.IsTrue(response.IssueCount > 0, "Standard mode should have inline comments from Pass 2");
        Assert.IsNotNull(response.InlineComments);
        Assert.IsTrue(response.InlineComments!.Count > 0, "Simulation should include inline comment DTOs");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("StandardReview")]
    [Timeout(120_000)]
    public async Task StandardMode_HasNoBadge()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Standard);

        Assert.IsFalse(response.Summary!.Contains(":zap:"), "Standard mode should not have Quick badge");
        Assert.IsFalse(response.Summary!.Contains(":mag:"), "Standard mode should not have Deep badge");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 8: Integration — Deep mode summary content verification
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_Summary_ContainsDeepAnalysisSection()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Deep);

        Assert.IsTrue(response.Summary!.Contains("Deep Analysis (Pass 3"),
            "Deep mode summary should have Deep Analysis section header");
        Assert.IsTrue(response.Summary!.Contains("Executive Summary"),
            "Should include Executive Summary subsection");
        Assert.IsTrue(response.Summary!.Contains("Overall Risk Level"),
            "Should include risk level");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [TestCategory("DeepReview")]
    [Timeout(120_000)]
    public async Task DeepMode_HasDeepBadge()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: ReviewDepth.Deep);

        Assert.IsTrue(response.Summary!.Contains(":mag: Deep"), "Deep mode should have Deep badge");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 9: ReviewProfile MaxOutputTokensDeepAnalysis
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewProfile_DeepAnalysisTokens_CanBeOverridden()
    {
        var profile = new ReviewProfile { MaxOutputTokensDeepAnalysis = 12000 };
        Assert.AreEqual(12000, profile.MaxOutputTokensDeepAnalysis);
    }

    [TestMethod]
    public void ReviewProfile_DeepAnalysisTokens_DefaultIs6000()
    {
        var profile = new ReviewProfile();
        Assert.AreEqual(6000, profile.MaxOutputTokensDeepAnalysis);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 10: ReviewResponse and ReviewHistoryEntry depth fields
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewResponse_DepthField_RoundTripsJson()
    {
        var response = new ReviewResponse
        {
            Status = "Reviewed",
            ReviewDepth = "Deep",
            Vote = 10,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(response);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ReviewResponse>(json)!;

        Assert.AreEqual("Deep", roundTripped.ReviewDepth);
    }

    [TestMethod]
    public void ReviewHistoryEntry_DepthField_RoundTripsJson()
    {
        var entry = new ReviewHistoryEntry
        {
            ReviewDepth = "Quick",
            ReviewNumber = 3,
            ReviewedAtUtc = DateTime.UtcNow,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(entry);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<ReviewHistoryEntry>(json)!;

        Assert.AreEqual("Quick", roundTripped.ReviewDepth);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Category 11: All three depths via DataRow
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [TestCategory("Integration")]
    [DataRow(ReviewDepth.Quick, "Quick")]
    [DataRow(ReviewDepth.Standard, "Standard")]
    [DataRow(ReviewDepth.Deep, "Deep")]
    [Timeout(120_000)]
    public async Task AllDepths_ResponseReportsCorrectDepth(ReviewDepth depth, string expectedLabel)
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, pr.RepositoryName!, prId,
            simulationOnly: true, reviewDepth: depth);

        Assert.AreEqual("Simulated", response.Status);
        Assert.AreEqual(expectedLabel, response.ReviewDepth,
            $"ReviewDepth should be {expectedLabel}");
    }
}
