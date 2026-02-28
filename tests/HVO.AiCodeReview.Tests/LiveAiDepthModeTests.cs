using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// LiveAI integration tests for Review Depth Modes (Quick / Standard / Deep).
///
/// These tests use the REAL Azure OpenAI service and real Azure DevOps
/// disposable repos. They incur API cost and latency. Run selectively:
///
///   dotnet test --filter "TestCategory=LiveAI&amp;FullyQualifiedName~LiveAiDepthMode"
///
/// Each test creates its own disposable test repo via <see cref="TestPullRequestHelper"/>,
/// pushes multi-file known-bad code via <see cref="TestPullRequestHelper.PushMultipleFilesAsync"/>,
/// then runs a review at the specified depth and validates the AI output.
///
/// These tests validate that:
///   - Quick mode produces a summary with risk areas but no inline comments
///   - Standard mode produces inline comments and per-file verdicts
///   - Deep mode produces all of Standard plus cross-file analysis (Pass 3)
/// </summary>
[TestClass]
[TestCategory("LiveAI")]
public class LiveAiDepthModeTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Quick Mode — Real AI, Single File
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(300_000)] // 5 min
    public async Task QuickMode_RealAi_ProducesSummaryWithNoInlineComments()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [Quick-LiveAI] PR #{prId} in {repo}");

        // Push known-bad code (single file — Quick mode doesn't need multi-file)
        await pr.PushNewCommitAsync("SecurityFlaws.cs", KnownBadCode.SecurityIssues);
        await Task.Delay(3000);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            reviewDepth: ReviewDepth.Quick);

        Assert.AreEqual("Reviewed", result.Status, "Quick review should complete.");
        Assert.AreEqual("Quick", result.ReviewDepth, "Response should report Quick depth.");
        Assert.IsNotNull(result.Summary, "Should have a summary.");
        Assert.IsTrue(result.Summary!.Contains(":zap: Quick"), "Summary should have Quick badge.");

        // Quick mode: NO inline comments (Pass 2 skipped)
        Assert.AreEqual(0, result.IssueCount,
            "Quick mode should not post inline comments (Pass 2 is skipped).");

        // Should still have a recommendation derived from risk areas
        Assert.IsNotNull(result.Recommendation,
            "Quick mode should derive a recommendation from Pass 1 risk areas.");

        Console.WriteLine($"  Quick mode result: Recommendation={result.Recommendation}, " +
                          $"IssueCount={result.IssueCount}, Vote={result.Vote}");
        Console.WriteLine($"  Summary excerpt: {result.Summary[..Math.Min(300, result.Summary.Length)]}...");
        Console.WriteLine($"  ✓ Quick mode LiveAI test passed.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Standard Mode — Real AI, Multi-File
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(300_000)]
    public async Task StandardMode_RealAi_ProducesInlineCommentsAndVerdicts()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [Standard-LiveAI] PR #{prId} in {repo}");

        // Push multi-file known-bad code
        await pr.PushMultipleFilesAsync(KnownBadCode.MultiFileSecurityIssues);
        await Task.Delay(3000);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            reviewDepth: ReviewDepth.Standard);

        Assert.AreEqual("Reviewed", result.Status, "Standard review should complete.");
        Assert.AreEqual("Standard", result.ReviewDepth, "Response should report Standard depth.");
        Assert.IsNotNull(result.Summary, "Should have a summary.");

        // Standard mode: should have inline comments (Pass 2 runs)
        Assert.IsTrue(result.IssueCount > 0,
            "Standard mode should post inline comments for code with obvious issues.");

        // Should NOT have deep analysis section
        Assert.IsFalse(result.Summary!.Contains("Deep Analysis (Pass 3"),
            "Standard mode should not have a Deep Analysis section.");
        Assert.IsFalse(result.Summary.Contains(":mag: Deep"),
            "Standard mode should not have Deep badge.");

        // Verify AI found at least one security-related issue
        var summaryLower = result.Summary.ToLowerInvariant();
        var securityTerms = new[] { "secret", "key", "credential", "injection", "sql", "security", "hardcoded", "password", "md5" };
        bool mentionsSecurity = securityTerms.Any(t => summaryLower.Contains(t));
        Assert.IsTrue(mentionsSecurity,
            "Standard mode should mention at least one security concern for multi-file known-bad code.");

        Console.WriteLine($"  Standard mode result: Recommendation={result.Recommendation}, " +
                          $"IssueCount={result.IssueCount}, ErrorCount={result.ErrorCount}, Vote={result.Vote}");
        Console.WriteLine($"  ✓ Standard mode LiveAI test passed.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Deep Mode — Real AI, Multi-File, Cross-File Analysis
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(600_000)] // 10 min — Deep mode runs 3 passes
    public async Task DeepMode_RealAi_ProducesCrossFileAnalysis()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [Deep-LiveAI] PR #{prId} in {repo}");

        // Push multi-file known-bad code (cross-file issues)
        await pr.PushMultipleFilesAsync(KnownBadCode.MultiFileSecurityIssues);
        await Task.Delay(3000);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project, repo, prId,
            reviewDepth: ReviewDepth.Deep);

        Assert.AreEqual("Reviewed", result.Status, "Deep review should complete.");
        Assert.AreEqual("Deep", result.ReviewDepth, "Response should report Deep depth.");
        Assert.IsNotNull(result.Summary, "Should have a summary.");

        // Deep mode: should have inline comments (Pass 2 runs)
        Assert.IsTrue(result.IssueCount > 0,
            "Deep mode should post inline comments for code with obvious issues.");

        // Deep mode: should have Deep badge and analysis section
        Assert.IsTrue(result.Summary!.Contains(":mag: Deep"),
            "Deep mode summary should have Deep badge.");
        Assert.IsTrue(result.Summary.Contains("Deep Analysis (Pass 3"),
            "Deep mode summary should have Deep Analysis section header.");

        // Deep Analysis content checks
        var summaryLower = result.Summary.ToLowerInvariant();
        Assert.IsTrue(summaryLower.Contains("executive summary"),
            "Deep analysis should contain Executive Summary.");
        Assert.IsTrue(summaryLower.Contains("risk level"),
            "Deep analysis should contain Overall Risk Level.");

        // Cross-file issues should be detected — the known-bad code has issues
        // that span UserService ↔ OrderController ↔ AuthMiddleware
        bool hasCrossFileIssues = summaryLower.Contains("cross-file issue");
        Console.WriteLine($"  Cross-file issues detected: {hasCrossFileIssues}");

        // Recommendations should be present
        bool hasRecommendations = summaryLower.Contains("recommendation");
        Console.WriteLine($"  Recommendations present: {hasRecommendations}");

        // Verify AI found security-related concerns across files
        var securityTerms = new[] { "secret", "key", "injection", "sql", "hardcoded", "md5", "security" };
        bool mentionsSecurity = securityTerms.Any(t => summaryLower.Contains(t));
        Assert.IsTrue(mentionsSecurity,
            "Deep mode should mention security concerns for multi-file known-bad code.");

        // Verify the PR comment threads were actually posted
        var threads = await pr.GetThreadsAsync();
        int inlineCount = threads.Count(t =>
            t.TryGetProperty("threadContext", out var tc) && tc.ValueKind != JsonValueKind.Null);
        Console.WriteLine($"  Inline comment threads on PR: {inlineCount}");

        Console.WriteLine($"  Deep mode result: Recommendation={result.Recommendation}, " +
                          $"IssueCount={result.IssueCount}, ErrorCount={result.ErrorCount}, " +
                          $"WarningCount={result.WarningCount}, Vote={result.Vote}");
        Console.WriteLine($"  Summary length: {result.Summary.Length} chars");
        Console.WriteLine($"  ✓ Deep mode LiveAI test passed.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Depth Comparison — Run all 3 depths on the same code, compare output
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(900_000)] // 15 min — runs 3 reviews sequentially
    public async Task DepthComparison_AllModes_ProduceDifferentOutputs()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();

        var results = new Dictionary<string, ReviewResponse>();

        foreach (var depth in new[] { ReviewDepth.Quick, ReviewDepth.Standard, ReviewDepth.Deep })
        {
            await using var pr = new TestPullRequestHelper(
                ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

            var prId = await pr.CreateDraftPullRequestAsync();
            var repo = pr.RepositoryName!;
            Console.WriteLine($"  [{depth}-Comparison] PR #{prId} in {repo}");

            await pr.PushMultipleFilesAsync(KnownBadCode.MultiFileSecurityIssues);
            await Task.Delay(3000);

            var result = await ctx.Orchestrator.ExecuteReviewAsync(
                ctx.Project, repo, prId,
                reviewDepth: depth);

            results[depth.ToString()] = result;

            Console.WriteLine($"    {depth}: Status={result.Status}, Recommendation={result.Recommendation}, " +
                              $"Issues={result.IssueCount}, Vote={result.Vote}, " +
                              $"SummaryLen={result.Summary?.Length ?? 0}");
        }

        // Quick should have 0 inline comments
        Assert.AreEqual(0, results["Quick"].IssueCount,
            "Quick mode should have 0 inline comments.");

        // Standard should have inline comments
        Assert.IsTrue(results["Standard"].IssueCount > 0,
            "Standard mode should have inline comments.");

        // Deep should have inline comments AND deep analysis section
        Assert.IsTrue(results["Deep"].IssueCount > 0,
            "Deep mode should have inline comments.");
        Assert.IsTrue(results["Deep"].Summary!.Contains("Deep Analysis"),
            "Deep mode should have Deep Analysis section.");

        // Deep summary should be longer than Standard (has extra Pass 3 content)
        Assert.IsTrue(results["Deep"].Summary!.Length > results["Standard"].Summary!.Length,
            "Deep mode summary should be longer than Standard (includes Pass 3 analysis).");

        // Quick summary should be shorter than Standard (no per-file reviews)
        Assert.IsTrue(results["Quick"].Summary!.Length < results["Standard"].Summary!.Length,
            "Quick mode summary should be shorter than Standard (no per-file detail).");

        Console.WriteLine($"  ✓ Depth comparison test passed.");
        Console.WriteLine($"    Quick:    {results["Quick"].Summary!.Length} chars, {results["Quick"].IssueCount} issues");
        Console.WriteLine($"    Standard: {results["Standard"].Summary!.Length} chars, {results["Standard"].IssueCount} issues");
        Console.WriteLine($"    Deep:     {results["Deep"].Summary!.Length} chars, {results["Deep"].IssueCount} issues");
    }
}
