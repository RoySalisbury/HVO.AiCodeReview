using System.Collections.Concurrent;
using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// In-memory store for review sessions.
/// Provides lookup by session ID and listing of queued/in-progress sessions.
/// Registered as a singleton.
/// </summary>
public interface IReviewSessionStore
{
    /// <summary>Adds a session to the store.</summary>
    void Add(ReviewSession session);

    /// <summary>Gets a session by its ID, or null if not found.</summary>
    ReviewSession? Get(Guid sessionId);

    /// <summary>Gets all sessions that are queued or in-progress.</summary>
    IReadOnlyList<ReviewSession> GetActive();

    /// <summary>
    /// Tries to cancel a queued (not yet started) session.
    /// Returns true if the session was found and cancelled.
    /// Uses per-session locking to ensure thread-safe status updates.
    /// </summary>
    bool TryCancelQueued(Guid sessionId);

    /// <summary>
    /// Atomically transitions a session from Queued to InProgress.
    /// Returns true if the transition succeeded. Returns false if the session
    /// was not found, or is not in Queued status (e.g., already cancelled).
    /// </summary>
    bool TryTransitionToInProgress(Guid sessionId);

    /// <summary>Returns the total number of sessions in the store.</summary>
    int Count { get; }

    /// <summary>Returns the number of queued sessions.</summary>
    int QueuedCount { get; }

    /// <summary>Returns the number of in-progress sessions.</summary>
    int InProgressCount { get; }
}

/// <summary>
/// Default in-memory implementation backed by <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Periodically evicts completed/failed sessions older than 1 hour to prevent unbounded growth.
/// </summary>
public class InMemoryReviewSessionStore : IReviewSessionStore
{
    private readonly ConcurrentDictionary<Guid, ReviewSession> _sessions = new();
    private readonly ConcurrentDictionary<Guid, object> _sessionLocks = new();
    private int _accessCounter;
    private const int EvictionInterval = 100;
    private static readonly TimeSpan CompletedRetention = TimeSpan.FromHours(1);

    /// <inheritdoc />
    public void Add(ReviewSession session)
    {
        _sessions[session.SessionId] = session;
        _sessionLocks[session.SessionId] = new object();
        MaybeEvict();
    }

    /// <inheritdoc />
    public ReviewSession? Get(Guid sessionId)
    {
        _sessions.TryGetValue(sessionId, out var session);
        return session;
    }

    /// <inheritdoc />
    public IReadOnlyList<ReviewSession> GetActive()
    {
        return _sessions.Values
            .Where(s => s.Status == ReviewSessionStatus.Queued || s.Status == ReviewSessionStatus.InProgress)
            .OrderBy(s => s.RequestedAtUtc)
            .ToList();
    }

    /// <inheritdoc />
    public bool TryCancelQueued(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;
        if (!_sessionLocks.TryGetValue(sessionId, out var lockObj))
            return false;

        lock (lockObj)
        {
            if (session.Status != ReviewSessionStatus.Queued)
                return false;

            session.Status = ReviewSessionStatus.Cancelled;
            session.CompletedAtUtc = DateTime.UtcNow;
            return true;
        }
    }

    /// <inheritdoc />
    /// <inheritdoc />
    public bool TryTransitionToInProgress(Guid sessionId)
    {
        if (!_sessions.TryGetValue(sessionId, out var session))
            return false;
        if (!_sessionLocks.TryGetValue(sessionId, out var lockObj))
            return false;

        lock (lockObj)
        {
            if (session.Status != ReviewSessionStatus.Queued)
                return false;

            session.Status = ReviewSessionStatus.InProgress;
            return true;
        }
    }

    /// <inheritdoc />
    public int Count => _sessions.Count;

    /// <inheritdoc />
    public int QueuedCount => _sessions.Values.Count(s => s.Status == ReviewSessionStatus.Queued);

    /// <inheritdoc />
    public int InProgressCount => _sessions.Values.Count(s => s.Status == ReviewSessionStatus.InProgress);

    private void MaybeEvict()
    {
        if (Interlocked.Increment(ref _accessCounter) % EvictionInterval != 0) return;

        var cutoff = DateTime.UtcNow - CompletedRetention;
        var toRemove = _sessions
            .Where(kvp => kvp.Value.Status is ReviewSessionStatus.Completed or ReviewSessionStatus.Failed or ReviewSessionStatus.Cancelled
                          && kvp.Value.CompletedAtUtc < cutoff)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in toRemove)
        {
            _sessions.TryRemove(key, out _);
            _sessionLocks.TryRemove(key, out _);
        }
    }
}
