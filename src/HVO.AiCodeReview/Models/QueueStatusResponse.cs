namespace AiCodeReview.Models;

/// <summary>
/// Response for GET /api/review/queue — shows queue status and active sessions.
/// </summary>
public class QueueStatusResponse
{
    /// <summary>Whether the review queue is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Max concurrent reviews allowed.</summary>
    public int MaxConcurrentReviews { get; set; }

    /// <summary>Max queue depth.</summary>
    public int MaxQueueDepth { get; set; }

    /// <summary>Number of sessions currently queued.</summary>
    public int QueuedCount { get; set; }

    /// <summary>Number of sessions currently in progress.</summary>
    public int InProgressCount { get; set; }

    /// <summary>Active (queued + in-progress) sessions.</summary>
    public IReadOnlyList<QueuedSessionInfo> Sessions { get; set; } = [];
}

/// <summary>
/// Summary of a single queued or in-progress session.
/// </summary>
public class QueuedSessionInfo
{
    public Guid SessionId { get; set; }
    public int PullRequestId { get; set; }
    public string Project { get; set; } = string.Empty;
    public string Repository { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime RequestedAtUtc { get; set; }
}
