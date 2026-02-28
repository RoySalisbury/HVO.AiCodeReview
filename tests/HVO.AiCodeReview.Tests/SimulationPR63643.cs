using System.Diagnostics;
using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Simulation run for PR 63643 in Dynamit repo — Vector strategy.
/// Runs AI review without posting comments to the PR.
/// </summary>
[TestClass]
[TestCategory("Manual")]
public class SimulationPR63643
{
    [TestMethod]
    [Timeout(600_000)]
    public async Task Simulate_PR63643_VectorStrategy()
    {
        var overall = Stopwatch.StartNew();

        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  Simulation: PR #63643  (Dynamit)");
        Console.WriteLine("  Strategy:   Vector (Assistants API + Vector Store)");
        Console.WriteLine("  Mode:       Simulation (no comments posted)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine();

        await using var ctx = TestServiceBuilder.BuildWithRealAi();

        Console.WriteLine($"  [{overall.Elapsed:mm\\:ss\\.ff}] DI container built, calling orchestrator...");

        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            project: "Dynamit",
            repository: "Dynamit",
            pullRequestId: 63643,
            simulationOnly: true,
            reviewStrategy: ReviewStrategy.Vector);

        overall.Stop();

        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  RESULTS");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine($"  Status:          {result.Status}");
        Console.WriteLine($"  Recommendation:  {result.Recommendation}");
        Console.WriteLine($"  Verdict:         {result.Verdict}");
        Console.WriteLine($"  Vote:            {result.Vote}");
        Console.WriteLine($"  Review Depth:    {result.ReviewDepth}");
        Console.WriteLine($"  Total Time:      {overall.Elapsed:mm\\:ss\\.ff}");
        Console.WriteLine();
        Console.WriteLine($"  Issues:  {result.IssueCount} total");
        Console.WriteLine($"    Errors:    {result.ErrorCount}");
        Console.WriteLine($"    Warnings:  {result.WarningCount}");
        Console.WriteLine($"    Info:      {result.InfoCount}");
        Console.WriteLine();

        // Inline comments
        if (result.InlineComments?.Count > 0)
        {
            Console.WriteLine($"  Inline Comments ({result.InlineComments.Count}):");
            Console.WriteLine("  ─────────────────────────────────────────────────────────");
            foreach (var c in result.InlineComments)
            {
                Console.WriteLine($"    [{c.Severity.ToUpper()}] {c.FilePath} L{c.StartLine}-{c.EndLine} ({c.Status})");
                Console.WriteLine($"      {c.Comment[..Math.Min(c.Comment.Length, 200)]}");
                Console.WriteLine();
            }
        }

        // File reviews
        if (result.FileReviews?.Count > 0)
        {
            Console.WriteLine($"  File Reviews ({result.FileReviews.Count}):");
            Console.WriteLine("  ─────────────────────────────────────────────────────────");
            foreach (var f in result.FileReviews)
            {
                Console.WriteLine($"    {f.FilePath}: {f.Verdict}");
                if (!string.IsNullOrEmpty(f.ReviewText))
                    Console.WriteLine($"      {f.ReviewText[..Math.Min(f.ReviewText.Length, 200)]}");
                Console.WriteLine();
            }
        }

        // Skipped files
        if (result.SkippedFiles?.Count > 0)
        {
            Console.WriteLine($"  Skipped Files ({result.SkippedFiles.Count}):");
            Console.WriteLine("  ─────────────────────────────────────────────────────────");
            foreach (var s in result.SkippedFiles)
            {
                Console.WriteLine($"    {s.FilePath}: {s.SkipReason}");
            }
            Console.WriteLine();
        }

        // Summary
        if (!string.IsNullOrEmpty(result.Summary))
        {
            Console.WriteLine("  Summary:");
            Console.WriteLine("  ─────────────────────────────────────────────────────────");
            Console.WriteLine(result.Summary);
            Console.WriteLine();
        }

        // Verdict justification
        if (!string.IsNullOrEmpty(result.VerdictJustification))
        {
            Console.WriteLine("  Verdict Justification:");
            Console.WriteLine("  ─────────────────────────────────────────────────────────");
            Console.WriteLine($"  {result.VerdictJustification}");
            Console.WriteLine();
        }

        // Also dump full JSON for the markdown report
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  FULL JSON RESPONSE");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine(json);

        // Basic assertions 
        Assert.AreEqual("Simulated", result.Status, "Should be simulation mode.");
        Assert.IsNotNull(result.Summary, "Summary should be populated.");
    }
}
