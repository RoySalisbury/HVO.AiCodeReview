using AiCodeReview.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for <see cref="ReviewRateLimiter"/> — the in-memory per-PR
/// cooldown that prevents duplicate reviews within a configurable window.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ReviewRateLimiterTests
{
    private readonly ILogger<ReviewRateLimiter> _logger = NullLogger<ReviewRateLimiter>.Instance;

    private ReviewRateLimiter CreateLimiter() => new(_logger);

    // ═══════════════════════════════════════════════════════════════════
    //  Check — interval disabled
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Check_IntervalZero_AlwaysAllowed()
    {
        var limiter = CreateLimiter();
        limiter.Record("org", "proj", "repo", 1);

        var (allowed, remaining, _) = limiter.Check("org", "proj", "repo", 1, intervalMinutes: 0);

        Assert.IsTrue(allowed, "Should be allowed when interval is 0 (disabled)");
        Assert.AreEqual(0, remaining);
    }

    [TestMethod]
    public void Check_IntervalNegative_AlwaysAllowed()
    {
        var limiter = CreateLimiter();
        limiter.Record("org", "proj", "repo", 1);

        var (allowed, remaining, _) = limiter.Check("org", "proj", "repo", 1, intervalMinutes: -5);

        Assert.IsTrue(allowed, "Should be allowed when interval is negative (disabled)");
        Assert.AreEqual(0, remaining);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Check — no prior record
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Check_NoPriorRecord_Allowed()
    {
        var limiter = CreateLimiter();

        var (allowed, remaining, lastReviewed) = limiter.Check("org", "proj", "repo", 42, intervalMinutes: 10);

        Assert.IsTrue(allowed);
        Assert.AreEqual(0, remaining);
        Assert.IsNull(lastReviewed, "LastReviewedUtc should be null when no prior review");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Record + Check — within cooldown
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Check_WithinCooldown_Blocked()
    {
        var limiter = CreateLimiter();
        limiter.Record("org", "proj", "repo", 1);

        var (allowed, remaining, lastReviewed) = limiter.Check("org", "proj", "repo", 1, intervalMinutes: 60);

        Assert.IsFalse(allowed, "Should be blocked within cooldown window");
        Assert.IsTrue(remaining > 0, "SecondsRemaining should be positive");
        Assert.IsTrue(remaining <= 3600, "SecondsRemaining should be at most 60 minutes");
        Assert.IsNotNull(lastReviewed, "LastReviewedUtc should be set");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Record + Check — different PRs are independent
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Check_DifferentPr_NotBlocked()
    {
        var limiter = CreateLimiter();
        limiter.Record("org", "proj", "repo", 1);

        var (allowed, _, _) = limiter.Check("org", "proj", "repo", 2, intervalMinutes: 60);

        Assert.IsTrue(allowed, "Different PR should not be rate-limited");
    }

    [TestMethod]
    public void Check_DifferentOrg_NotBlocked()
    {
        var limiter = CreateLimiter();
        limiter.Record("orgA", "proj", "repo", 1);

        var (allowed, _, _) = limiter.Check("orgB", "proj", "repo", 1, intervalMinutes: 60);

        Assert.IsTrue(allowed, "Different organization should not be rate-limited");
    }

    [TestMethod]
    public void Check_DifferentProject_NotBlocked()
    {
        var limiter = CreateLimiter();
        limiter.Record("org", "projA", "repo", 1);

        var (allowed, _, _) = limiter.Check("org", "projB", "repo", 1, intervalMinutes: 60);

        Assert.IsTrue(allowed, "Different project should not be rate-limited");
    }

    [TestMethod]
    public void Check_DifferentRepo_NotBlocked()
    {
        var limiter = CreateLimiter();
        limiter.Record("org", "proj", "repoA", 1);

        var (allowed, _, _) = limiter.Check("org", "proj", "repoB", 1, intervalMinutes: 60);

        Assert.IsTrue(allowed, "Different repository should not be rate-limited");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Key is case-insensitive
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Check_CaseInsensitiveKey_Blocked()
    {
        var limiter = CreateLimiter();
        limiter.Record("ORG", "PROJ", "REPO", 1);

        var (allowed, _, _) = limiter.Check("org", "proj", "repo", 1, intervalMinutes: 60);

        Assert.IsFalse(allowed, "Key lookup should be case-insensitive");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Record overwrites timestamp
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Record_OverwritesPriorTimestamp()
    {
        var limiter = CreateLimiter();

        // Record and check: allowed after short interval
        limiter.Record("org", "proj", "repo", 1);
        var (allowed1, _, _) = limiter.Check("org", "proj", "repo", 1, intervalMinutes: 60);
        Assert.IsFalse(allowed1, "Should be blocked after first record");

        // Record again — this resets the timer
        limiter.Record("org", "proj", "repo", 1);
        var (allowed2, _, _) = limiter.Check("org", "proj", "repo", 1, intervalMinutes: 60);
        Assert.IsFalse(allowed2, "Should still be blocked after re-record (timer reset)");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Multiple PRs tracked independently
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Record_MultiplePRs_TrackedIndependently()
    {
        var limiter = CreateLimiter();
        limiter.Record("org", "proj", "repo", 1);
        limiter.Record("org", "proj", "repo", 2);
        limiter.Record("org", "proj", "repo", 3);

        var (allowed1, _, _) = limiter.Check("org", "proj", "repo", 1, intervalMinutes: 60);
        var (allowed2, _, _) = limiter.Check("org", "proj", "repo", 2, intervalMinutes: 60);
        var (allowed3, _, _) = limiter.Check("org", "proj", "repo", 3, intervalMinutes: 60);
        var (allowed4, _, _) = limiter.Check("org", "proj", "repo", 4, intervalMinutes: 60);

        Assert.IsFalse(allowed1, "PR 1 should be blocked");
        Assert.IsFalse(allowed2, "PR 2 should be blocked");
        Assert.IsFalse(allowed3, "PR 3 should be blocked");
        Assert.IsTrue(allowed4, "PR 4 (not recorded) should be allowed");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Cleanup triggers on request count
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Check_TriggersCleanupAfterManyRequests()
    {
        var limiter = CreateLimiter();

        // Make 100+ Check calls to trigger the cleanup code path.
        // This exercises the cleanup interval + Task.Run branch.
        for (int i = 0; i < 105; i++)
        {
            limiter.Check("org", "proj", "repo", i, intervalMinutes: 1);
        }

        // Allow background cleanup task to run
        await Task.Delay(100);

        // Verify the limiter still works after cleanup
        limiter.Record("org", "proj", "repo", 999);
        var (allowed, _, _) = limiter.Check("org", "proj", "repo", 999, intervalMinutes: 60);
        Assert.IsFalse(allowed, "Limiter should still function after cleanup");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  IReviewRateLimiter interface
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ImplementsIReviewRateLimiter()
    {
        var limiter = CreateLimiter();
        Assert.IsInstanceOfType(limiter, typeof(IReviewRateLimiter));
    }
}
