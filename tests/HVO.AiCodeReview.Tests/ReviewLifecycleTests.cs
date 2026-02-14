using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Parallelized review lifecycle tests.  Each test creates its own disposable
/// repo so they can run concurrently without sharing state.
///
/// These replace the monolithic FullReviewLifecycle_AllScenarios test which
/// forced 7 sequential scenarios in a single repo.  The new structure:
///
///   Test                                    What it covers
///   ─────────────────────────────────────   ───────────────────────────────────
///   FirstReview_DraftPr_ReviewsWithoutVote  First review on draft: FullReview, no vote,
///                                           metadata stored, tag added, inline comments.
///   Skip_NoChanges_ReturnsSkipped           Second call with no changes → Skip.
///   ReReview_NewCommit_DeduplicatesComments  Push commit → Re-Review, dedup verified.
///   DraftToActive_VoteOnlyFlow              Draft → Active (no code change) → VoteOnly.
///   ResetAndReReview_FullCycle              Clear metadata + push → FullReview with vote,
///                                           then push again → Re-Review with dedup.
///
/// All use FakeCodeReviewService — no real AI calls.
/// </summary>
[TestClass]
public class ReviewLifecycleTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  1. First Review (Draft PR)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(120_000)]
    public async Task FirstReview_DraftPr_ReviewsWithoutVote()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [FirstReview] PR #{prId} in {repo}");

        var result = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);

        // Status & summary
        Assert.AreEqual("Reviewed", result.Status, "Should be Reviewed.");
        Assert.IsNotNull(result.Summary);
        Assert.IsTrue(result.Summary!.Contains("Code Review"), "Summary should have 'Code Review' header.");

        // No vote on draft
        Assert.IsNull(result.Vote, "Draft PR should not get a vote.");

        // Metadata stored
        var props = await pr.GetReviewPropertiesAsync();
        Assert.IsTrue(props.ContainsKey("AiCodeReview.LastSourceCommit"), "Metadata stored.");
        Assert.AreEqual("True", props["AiCodeReview.WasDraft"]);
        Assert.AreEqual("False", props["AiCodeReview.VoteSubmitted"]);

        // Tag applied
        var labels = await pr.GetLabelsAsync();
        Assert.IsTrue(labels.Contains("ai-code-review"), "Review tag present.");

        // Inline comments posted
        var threads = await pr.GetThreadsAsync();
        int inlineCount = CountInlineThreads(threads);
        Assert.IsTrue(inlineCount > 0, "Inline comments should be posted.");

        Console.WriteLine($"  ✓ Status=Reviewed, Vote=null, {inlineCount} inline, tag ✓, metadata ✓");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. Skip — no changes since last review
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(120_000)]
    public async Task Skip_NoChanges_ReturnsSkipped()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [Skip] PR #{prId} in {repo}");

        // First review — sets metadata
        var r1 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Reviewed", r1.Status);

        // Same PR, no changes → Skip
        var r2 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Skipped", r2.Status, "Should Skip when nothing changed.");
        Assert.IsTrue(r2.Summary!.Contains("already been reviewed"), "Skip message expected.");

        Console.WriteLine($"  ✓ Skip after no changes.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. Re-Review — push new commit, verify deduplication
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(120_000)]
    public async Task ReReview_NewCommit_DeduplicatesComments()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [ReReview] PR #{prId} in {repo}");

        // First review
        await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        var threadsBefore = await pr.GetThreadsAsync();
        int inlineBefore = CountInlineThreads(threadsBefore);

        // Push a new file to trigger re-review
        await pr.PushNewCommitAsync(
            $"re-review-test-{Guid.NewGuid():N}.txt",
            $"// Added at {DateTime.UtcNow:O} to trigger re-review\n");
        await Task.Delay(3000);

        // Re-review
        var r2 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Reviewed", r2.Status);
        Assert.IsTrue(r2.Summary!.Contains("Re-Review"), "Should be a Re-Review.");
        Assert.IsNull(r2.Vote, "Still draft — no vote.");

        // Dedup check: the new file gets inline comments, old file's duplicates are suppressed
        var threadsAfter = await pr.GetThreadsAsync();
        int inlineAfter = CountInlineThreads(threadsAfter);
        Assert.IsTrue(inlineAfter > inlineBefore,
            $"New inline comments expected for the new file ({inlineBefore} → {inlineAfter}).");

        Console.WriteLine($"  ✓ Re-Review with dedup: {inlineBefore} → {inlineAfter} inline threads.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. Draft → Active — VoteOnly flow
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(120_000)]
    public async Task DraftToActive_VoteOnlyFlow()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [VoteOnly] PR #{prId} in {repo}");

        // Review while draft
        await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);

        // Verify pre-conditions: WasDraft=True, VoteSubmitted=False
        var props = await pr.GetReviewPropertiesAsync();
        Assert.AreEqual("True", props.GetValueOrDefault("AiCodeReview.WasDraft"));
        Assert.AreEqual("False", props.GetValueOrDefault("AiCodeReview.VoteSubmitted"));

        // Publish the PR (draft → active)
        await pr.SetDraftStatusAsync(false);
        await Task.Delay(2000);

        // Review again — should be VoteOnly (no code changes, just status change)
        var r2 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Reviewed", r2.Status);
        Assert.IsTrue(r2.Summary!.Contains("Draft-to-active"), "Should mention draft-to-active.");
        Assert.IsNotNull(r2.Vote, "Vote should be submitted.");

        // Post-conditions
        var propsAfter = await pr.GetReviewPropertiesAsync();
        Assert.AreEqual("True", propsAfter.GetValueOrDefault("AiCodeReview.VoteSubmitted"));
        Assert.AreEqual("False", propsAfter.GetValueOrDefault("AiCodeReview.WasDraft"));

        // No changes on active → Skip
        var r3 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Skipped", r3.Status, "Active + no changes → Skip.");

        Console.WriteLine($"  ✓ VoteOnly flow: Vote={r2.Vote}, then Skip.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. Reset + Full Review on Active + Re-Review dedup
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(120_000)]
    public async Task ResetAndReReview_FullCycle()
    {
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [ResetCycle] PR #{prId} in {repo}");

        // Review → then publish (so we test active-PR full review)
        await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        await pr.SetDraftStatusAsync(false);
        await Task.Delay(2000);

        // Clear all metadata + tag
        await pr.ClearReviewMetadataAsync();
        await pr.RemoveReviewTagAsync();

        // Push a new file + review — should be a fresh FullReview with vote
        await pr.PushNewCommitAsync(
            $"reset-test-{Guid.NewGuid():N}.txt",
            $"// Fresh file after metadata reset at {DateTime.UtcNow:O}\n");
        await Task.Delay(3000);

        var r1 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Reviewed", r1.Status);
        Assert.IsTrue(r1.Summary!.Contains("Code Review"), "Should be full Code Review (not Re-Review).");
        Assert.IsNotNull(r1.Vote, "Vote submitted on active PR.");
        Assert.AreEqual(5, r1.Vote);

        var labels = await pr.GetLabelsAsync();
        Assert.IsTrue(labels.Contains("ai-code-review"), "Tag re-added.");

        // Push another file → Re-Review with dedup
        var threadsBefore = await pr.GetThreadsAsync();
        int beforeCount = threadsBefore.Count;

        await pr.PushNewCommitAsync(
            $"dedup-verify-{Guid.NewGuid():N}.txt",
            $"// Dedup verification at {DateTime.UtcNow:O}\n");
        await Task.Delay(3000);

        var r2 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Reviewed", r2.Status);
        Assert.IsTrue(r2.Summary!.Contains("Re-Review"));

        var threadsAfter = await pr.GetThreadsAsync();
        int newThreads = threadsAfter.Count - beforeCount;
        Assert.IsTrue(newThreads <= 5,
            $"Expected ≤5 new threads (dedup should suppress old-file duplicates), got {newThreads}.");

        Console.WriteLine($"  ✓ Reset→FullReview→ReReview cycle. New threads: {newThreads}.");
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static int CountInlineThreads(List<JsonElement> threads) =>
        threads.Count(t =>
            t.TryGetProperty("threadContext", out var ctx)
            && ctx.ValueKind != JsonValueKind.Null);
}
