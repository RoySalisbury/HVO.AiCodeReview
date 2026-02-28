using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Live integration test for the Vector Store review strategy.
/// Pushes multi-file known-bad code to a disposable Azure DevOps repo
/// and runs a real AI review using the Assistants API + Vector Store.
/// </summary>
[TestClass]
[TestCategory("LiveAI")]
public class VectorStoreIntegrationTest
{
    [TestMethod]
    [Timeout(600_000)] // 10 min — Vector Store lifecycle is slower
    public async Task VectorStrategy_MultiFileSecurity_AiFlagsCrossFileIssues()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [Vector-CrossFile] PR #{prId} in {repo}");

        // Push multi-file scenario with cross-file security issues
        await pr.PushMultipleFilesAsync(KnownBadCode.MultiFileSecurityIssues);
        await Task.Delay(3000);

        Console.WriteLine("  Executing review with ReviewStrategy.Vector...");
        var result = await ctx.Orchestrator.ExecuteReviewAsync(
            ctx.Project,
            repo,
            prId,
            reviewStrategy: ReviewStrategy.Vector);

        Console.WriteLine($"  Status: {result.Status}");
        Console.WriteLine($"  Summary: {result.Summary?[..Math.Min(300, result.Summary?.Length ?? 0)]}...");

        Assert.AreEqual("Reviewed", result.Status, "Vector review should complete successfully.");
        Assert.IsNotNull(result.Summary, "Summary should exist.");

        // Check inline comments were posted
        var threads = await pr.GetThreadsAsync();
        int inlineCount = threads.Count(t =>
            t.TryGetProperty("threadContext", out var ctx)
            && ctx.ValueKind != JsonValueKind.Null);

        Console.WriteLine($"  Inline comments: {inlineCount}");

        Assert.IsTrue(inlineCount > 0,
            "Vector review should post at least 1 inline comment for multi-file security issues.");

        // Verify the AI caught cross-file or security concerns
        var allText = GetAllThreadText(threads);
        var combined = (result.Summary ?? "").ToLowerInvariant() + " " + allText.ToLowerInvariant();

        var securityTerms = new[] {
            "secret", "key", "credential", "injection", "sql", "security",
            "hardcoded", "password", "md5", "null", "middleware", "token"
        };
        var foundTerms = securityTerms.Where(t => combined.Contains(t)).ToList();

        Console.WriteLine($"  Security terms found: {string.Join(", ", foundTerms)}");
        Assert.IsTrue(foundTerms.Count > 0,
            "Vector review should mention at least one security-related concern.");

        Console.WriteLine($"  ✓ Vector strategy completed. {inlineCount} inline comments, terms: [{string.Join(", ", foundTerms)}]");
    }

    private static string GetAllThreadText(List<JsonElement> threads)
    {
        var texts = new List<string>();
        foreach (var thread in threads)
        {
            if (thread.TryGetProperty("comments", out var comments))
            {
                foreach (var comment in comments.EnumerateArray())
                {
                    if (comment.TryGetProperty("content", out var content))
                        texts.Add(content.GetString() ?? "");
                }
            }
        }
        return string.Join(" ", texts);
    }
}
