namespace AiCodeReview.Models;

/// <summary>
/// Response DTO for GET /api/review/metrics — returns review history and aggregated metrics.
/// All data comes from PR properties, making it resilient to tag deletion.
/// </summary>
public class ReviewMetricsResponse
{
    public int PullRequestId { get; set; }
    public int ReviewCount { get; set; }
    public DateTime LastReviewedAtUtc { get; set; }
    public string? LastSourceCommit { get; set; }
    public int LastIteration { get; set; }
    public bool WasDraft { get; set; }
    public bool VoteSubmitted { get; set; }

    /// <summary>Full history of every review action taken on this PR.</summary>
    public List<ReviewHistoryEntry> History { get; set; } = new();

    // ── Aggregated Metrics ──────────────────────────────────────────────
    public int TotalPromptTokens { get; set; }
    public int TotalCompletionTokens { get; set; }
    public int TotalTokensUsed { get; set; }
    public long TotalAiDurationMs { get; set; }
    public long TotalDurationMs { get; set; }
}
