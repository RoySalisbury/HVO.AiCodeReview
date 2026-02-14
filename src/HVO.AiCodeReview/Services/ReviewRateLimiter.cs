using System.Collections.Concurrent;

namespace AiCodeReview.Services;

/// <summary>
/// In-memory rate limiter that prevents the same PR from being reviewed
/// more than once within a configurable time window.
/// Registered as a singleton so the cache survives across requests.
/// </summary>
public interface IReviewRateLimiter
{
    /// <summary>
    /// Check whether a review is allowed for the given PR right now.
    /// Returns (true, 0) if allowed, or (false, secondsRemaining) if rate-limited.
    /// </summary>
    (bool IsAllowed, int SecondsRemaining, DateTime? LastReviewedUtc) Check(
        string organization, string project, string repository, int pullRequestId,
        int intervalMinutes);

    /// <summary>
    /// Record that a review action (of any kind) was taken for this PR.
    /// Called after the action completes so the cooldown starts now.
    /// </summary>
    void Record(string organization, string project, string repository, int pullRequestId);
}

public class ReviewRateLimiter : IReviewRateLimiter
{
    private readonly ConcurrentDictionary<string, DateTime> _lastReviewTimes = new();
    private readonly ILogger<ReviewRateLimiter> _logger;
    private int _requestCounter;
    private const int CleanupInterval = 100; // Check for stale entries every N requests

    public ReviewRateLimiter(ILogger<ReviewRateLimiter> logger)
    {
        _logger = logger;
    }

    public (bool IsAllowed, int SecondsRemaining, DateTime? LastReviewedUtc) Check(
        string organization, string project, string repository, int pullRequestId,
        int intervalMinutes)
    {
        // Interval <= 0 means rate limiting is disabled
        if (intervalMinutes <= 0)
            return (true, 0, null);

        var key = BuildKey(organization, project, repository, pullRequestId);

        if (_lastReviewTimes.TryGetValue(key, out var lastReview))
        {
            var elapsed = DateTime.UtcNow - lastReview;
            var interval = TimeSpan.FromMinutes(intervalMinutes);

            if (elapsed < interval)
            {
                var remaining = (int)Math.Ceiling((interval - elapsed).TotalSeconds);
                _logger.LogInformation(
                    "Rate limited: {Org}/{Project}/{Repo} PR #{PrId} — last reviewed {Elapsed:F0}s ago, " +
                    "next allowed in {Remaining}s (interval: {Interval}m)",
                    organization, project, repository, pullRequestId,
                    elapsed.TotalSeconds, remaining, intervalMinutes);
                return (false, remaining, lastReview);
            }
        }

        // Periodically evict stale entries (older than 24 h)
        if (Interlocked.Increment(ref _requestCounter) % CleanupInterval == 0)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    CleanupStaleEntries();
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Rate limiter cleanup failed");
                }
            });
        }

        return (true, 0, null);
    }

    public void Record(string organization, string project, string repository, int pullRequestId)
    {
        var key = BuildKey(organization, project, repository, pullRequestId);
        _lastReviewTimes[key] = DateTime.UtcNow;
        _logger.LogDebug("Rate limiter recorded review for {Key}", key);
    }

    // ── Internals ───────────────────────────────────────────────────────

    private static string BuildKey(string organization, string project, string repository, int pullRequestId)
        => $"{organization.ToLowerInvariant()}:{project.ToLowerInvariant()}:{repository.ToLowerInvariant()}:{pullRequestId}";

    private void CleanupStaleEntries()
    {
        var cutoff = DateTime.UtcNow.AddHours(-24);
        var staleKeys = _lastReviewTimes
            .Where(kv => kv.Value < cutoff)
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in staleKeys)
        {
            _lastReviewTimes.TryRemove(key, out _);
        }

        if (staleKeys.Count > 0)
        {
            _logger.LogDebug("Rate limiter cleanup: evicted {Count} stale entries", staleKeys.Count);
        }
    }
}
