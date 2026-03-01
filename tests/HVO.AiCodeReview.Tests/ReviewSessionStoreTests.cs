using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for <see cref="InMemoryReviewSessionStore"/> — verifies session
/// lifecycle, querying, cancellation, and eviction behavior.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ReviewSessionStoreTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Add / Get
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Add_Get_RoundTrips()
    {
        var store = new InMemoryReviewSessionStore();
        var session = MakeSession();

        store.Add(session);
        var found = store.Get(session.SessionId);

        Assert.IsNotNull(found);
        Assert.AreEqual(session.SessionId, found.SessionId);
    }

    [TestMethod]
    public void Get_UnknownId_ReturnsNull()
    {
        var store = new InMemoryReviewSessionStore();
        Assert.IsNull(store.Get(Guid.NewGuid()));
    }

    [TestMethod]
    public void Count_ReflectsAddedSessions()
    {
        var store = new InMemoryReviewSessionStore();
        Assert.AreEqual(0, store.Count);

        store.Add(MakeSession());
        Assert.AreEqual(1, store.Count);

        store.Add(MakeSession());
        Assert.AreEqual(2, store.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetActive — Queued / InProgress only
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GetActive_ReturnsOnlyQueuedAndInProgress()
    {
        var store = new InMemoryReviewSessionStore();

        var queued = MakeSession(ReviewSessionStatus.Queued);
        var inProgress = MakeSession(ReviewSessionStatus.InProgress);
        var completed = MakeSession(ReviewSessionStatus.Completed);
        var failed = MakeSession(ReviewSessionStatus.Failed);

        store.Add(queued);
        store.Add(inProgress);
        store.Add(completed);
        store.Add(failed);

        var active = store.GetActive();
        Assert.AreEqual(2, active.Count);
        Assert.IsTrue(active.Any(s => s.SessionId == queued.SessionId));
        Assert.IsTrue(active.Any(s => s.SessionId == inProgress.SessionId));
    }

    [TestMethod]
    public void GetActive_OrderedByRequestTime()
    {
        var store = new InMemoryReviewSessionStore();

        var first = MakeSession();
        first.RequestedAtUtc = DateTime.UtcNow.AddMinutes(-10);

        var second = MakeSession();
        second.RequestedAtUtc = DateTime.UtcNow.AddMinutes(-5);

        var third = MakeSession();
        third.RequestedAtUtc = DateTime.UtcNow;

        store.Add(third);
        store.Add(first);
        store.Add(second);

        var active = store.GetActive();
        Assert.AreEqual(3, active.Count);
        Assert.AreEqual(first.SessionId, active[0].SessionId);
        Assert.AreEqual(second.SessionId, active[1].SessionId);
        Assert.AreEqual(third.SessionId, active[2].SessionId);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  QueuedCount / InProgressCount
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void QueuedCount_CountsOnlyQueued()
    {
        var store = new InMemoryReviewSessionStore();

        store.Add(MakeSession(ReviewSessionStatus.Queued));
        store.Add(MakeSession(ReviewSessionStatus.Queued));
        store.Add(MakeSession(ReviewSessionStatus.InProgress));
        store.Add(MakeSession(ReviewSessionStatus.Completed));

        Assert.AreEqual(2, store.QueuedCount);
    }

    [TestMethod]
    public void InProgressCount_CountsOnlyInProgress()
    {
        var store = new InMemoryReviewSessionStore();

        store.Add(MakeSession(ReviewSessionStatus.Queued));
        store.Add(MakeSession(ReviewSessionStatus.InProgress));
        store.Add(MakeSession(ReviewSessionStatus.InProgress));
        store.Add(MakeSession(ReviewSessionStatus.Completed));

        Assert.AreEqual(2, store.InProgressCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  TryCancelQueued
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void TryCancelQueued_QueuedSession_Succeeds()
    {
        var store = new InMemoryReviewSessionStore();
        var session = MakeSession(ReviewSessionStatus.Queued);
        store.Add(session);

        Assert.IsTrue(store.TryCancelQueued(session.SessionId));
        Assert.AreEqual(ReviewSessionStatus.Cancelled, session.Status);
        Assert.IsNotNull(session.CompletedAtUtc);
    }

    [TestMethod]
    public void TryCancelQueued_InProgressSession_ReturnsFalse()
    {
        var store = new InMemoryReviewSessionStore();
        var session = MakeSession(ReviewSessionStatus.InProgress);
        store.Add(session);

        Assert.IsFalse(store.TryCancelQueued(session.SessionId));
        Assert.AreEqual(ReviewSessionStatus.InProgress, session.Status);
    }

    [TestMethod]
    public void TryCancelQueued_UnknownId_ReturnsFalse()
    {
        var store = new InMemoryReviewSessionStore();
        Assert.IsFalse(store.TryCancelQueued(Guid.NewGuid()));
    }

    [TestMethod]
    public void TryCancelQueued_CompletedSession_ReturnsFalse()
    {
        var store = new InMemoryReviewSessionStore();
        var session = MakeSession(ReviewSessionStatus.Completed);
        store.Add(session);

        Assert.IsFalse(store.TryCancelQueued(session.SessionId));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Cancelled status enum
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void CancelledStatus_Exists()
    {
        Assert.AreEqual("Cancelled", ReviewSessionStatus.Cancelled.ToString());
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Eviction
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Eviction_RemovesExpiredCompletedSessions_After100Adds()
    {
        var store = new InMemoryReviewSessionStore();

        // Add an old completed session (completed > 1 hour ago)
        var oldCompleted = MakeSession(ReviewSessionStatus.Completed);
        oldCompleted.CompletedAtUtc = DateTime.UtcNow.AddHours(-2);
        store.Add(oldCompleted);

        // Add an old failed session (failed > 1 hour ago)
        var oldFailed = MakeSession(ReviewSessionStatus.Failed);
        oldFailed.CompletedAtUtc = DateTime.UtcNow.AddHours(-2);
        store.Add(oldFailed);

        // Add an old cancelled session (cancelled > 1 hour ago)
        var oldCancelled = MakeSession(ReviewSessionStatus.Cancelled);
        oldCancelled.CompletedAtUtc = DateTime.UtcNow.AddHours(-2);
        store.Add(oldCancelled);

        // Add an active session that should NOT be evicted
        var activeSession = MakeSession(ReviewSessionStatus.InProgress);
        store.Add(activeSession);

        // Add a recently completed session (within retention window)
        var recentCompleted = MakeSession(ReviewSessionStatus.Completed);
        recentCompleted.CompletedAtUtc = DateTime.UtcNow.AddMinutes(-5);
        store.Add(recentCompleted);

        // We've done 5 adds so far. Need 95 more to trigger eviction at 100.
        for (int i = 0; i < 95; i++)
            store.Add(MakeSession());

        // Eviction should have fired — old completed/failed/cancelled sessions removed
        Assert.IsNull(store.Get(oldCompleted.SessionId),
            "Old completed session should be evicted");
        Assert.IsNull(store.Get(oldFailed.SessionId),
            "Old failed session should be evicted");
        Assert.IsNull(store.Get(oldCancelled.SessionId),
            "Old cancelled session should be evicted");

        // Active and recent sessions should survive
        Assert.IsNotNull(store.Get(activeSession.SessionId),
            "Active session should not be evicted");
        Assert.IsNotNull(store.Get(recentCompleted.SessionId),
            "Recently completed session should not be evicted");
    }

    [TestMethod]
    public void Eviction_DoesNotRemove_BeforeInterval()
    {
        var store = new InMemoryReviewSessionStore();

        // Add an old completed session
        var oldCompleted = MakeSession(ReviewSessionStatus.Completed);
        oldCompleted.CompletedAtUtc = DateTime.UtcNow.AddHours(-2);
        store.Add(oldCompleted);

        // Add 98 more (total 99 — eviction interval is 100, won't trigger yet)
        for (int i = 0; i < 98; i++)
            store.Add(MakeSession());

        // Eviction should NOT have run — old session still present
        Assert.IsNotNull(store.Get(oldCompleted.SessionId),
            "Old session should still exist before eviction interval");
    }

    [TestMethod]
    public void Eviction_DoesNotRemove_CompletedWithinRetention()
    {
        var store = new InMemoryReviewSessionStore();

        // Add a completed session within the 1-hour retention
        var recent = MakeSession(ReviewSessionStatus.Completed);
        recent.CompletedAtUtc = DateTime.UtcNow.AddMinutes(-30);
        store.Add(recent);

        // Trigger eviction by hitting 100 adds
        for (int i = 0; i < 99; i++)
            store.Add(MakeSession());

        // Recent completed session should survive eviction
        Assert.IsNotNull(store.Get(recent.SessionId),
            "Recent completed session should not be evicted");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Interface
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ImplementsIReviewSessionStore()
    {
        var store = new InMemoryReviewSessionStore();
        Assert.IsInstanceOfType(store, typeof(IReviewSessionStore));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static ReviewSession MakeSession(
        ReviewSessionStatus status = ReviewSessionStatus.Queued)
    {
        var session = new ReviewSession
        {
            Project = "TestProject",
            Repository = "TestRepo",
            PullRequestId = 42,
        };
        session.Status = status;
        if (status is ReviewSessionStatus.Completed or ReviewSessionStatus.Failed)
            session.CompletedAtUtc = DateTime.UtcNow;
        return session;
    }
}
