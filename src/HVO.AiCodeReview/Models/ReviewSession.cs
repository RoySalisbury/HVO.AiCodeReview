namespace AiCodeReview.Models;

/// <summary>
/// Tracks a single review execution from request to completion.
/// The <see cref="SessionId"/> is a unique GUID assigned at controller entry
/// and flows through the orchestrator, AI service calls, DevOps operations,
/// and structured logging scopes. It is returned in the API response, stored
/// in review history, and displayed in the PR summary markdown.
/// </summary>
public class ReviewSession
{
    /// <summary>Unique identifier for this review execution.</summary>
    public Guid SessionId { get; private set; } = Guid.NewGuid();

    /// <summary>UTC timestamp when the review request was received.</summary>
    public DateTime RequestedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>UTC timestamp when the review completed (success or failure). Null while in progress.</summary>
    public DateTime? CompletedAtUtc { get; set; }

    /// <summary>Current status of the session: Queued, InProgress, Completed, Failed.</summary>
    public ReviewSessionStatus Status { get; set; } = ReviewSessionStatus.Queued;

    // ── PR Identity ─────────────────────────────────────────────────────

    /// <summary>Azure DevOps project name.</summary>
    public string Project { get; set; } = string.Empty;

    /// <summary>Repository name.</summary>
    public string Repository { get; set; } = string.Empty;

    /// <summary>Pull request ID.</summary>
    public int PullRequestId { get; set; }

    // ── Request Context ─────────────────────────────────────────────────

    /// <summary>Whether the review was forced (bypass skip/dedup logic).</summary>
    public bool ForceReview { get; set; }

    /// <summary>Whether this is a simulation-only run (no PR modifications).</summary>
    public bool SimulationOnly { get; set; }

    /// <summary>Review depth requested: Quick, Standard, Deep.</summary>
    public ReviewDepth ReviewDepth { get; set; } = ReviewDepth.Standard;

    /// <summary>Review strategy requested: FileByFile, Vector, Auto.</summary>
    public ReviewStrategy ReviewStrategy { get; set; } = ReviewStrategy.FileByFile;

    // ── Results (populated on completion) ────────────────────────────────

    /// <summary>Review verdict (Approved, NeedsWork, etc.). Null until completed.</summary>
    public string? Verdict { get; set; }

    /// <summary>Vote cast on the PR. Null if not applicable or failed.</summary>
    public int? Vote { get; set; }

    /// <summary>Number of files reviewed.</summary>
    public int FilesReviewed { get; set; }

    /// <summary>Number of inline comments posted.</summary>
    public int InlineCommentsPosted { get; set; }

    // ── AI Metrics ──────────────────────────────────────────────────────

    /// <summary>Total prompt (input) tokens consumed.</summary>
    public int? PromptTokens { get; set; }

    /// <summary>Total completion (output) tokens consumed.</summary>
    public int? CompletionTokens { get; set; }

    /// <summary>Total tokens consumed.</summary>
    public int? TotalTokens { get; set; }

    /// <summary>AI inference duration in milliseconds.</summary>
    public long? AiDurationMs { get; set; }

    /// <summary>Total wall-clock duration in milliseconds.</summary>
    public long? TotalDurationMs { get; set; }

    /// <summary>AI model used for the review.</summary>
    public string? ModelName { get; set; }

    /// <summary>Estimated cost in USD.</summary>
    public decimal? EstimatedCost { get; set; }

    // ── Error Tracking ──────────────────────────────────────────────────

    /// <summary>Error message if the review failed. Null on success.</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>Exception type if the review failed. Null on success.</summary>
    public string? ErrorType { get; set; }

    // ── Lifecycle Helpers ───────────────────────────────────────────────

    /// <summary>Marks the session as in-progress.</summary>
    public void Start()
    {
        Status = ReviewSessionStatus.InProgress;
    }

    /// <summary>Marks the session as completed successfully.</summary>
    public void Complete(string? verdict = null, int? vote = null)
    {
        Status = ReviewSessionStatus.Completed;
        CompletedAtUtc = DateTime.UtcNow;
        TotalDurationMs = (long)(CompletedAtUtc.Value - RequestedAtUtc).TotalMilliseconds;
        Verdict = verdict;
        Vote = vote;
    }

    /// <summary>Marks the session as failed with an error.</summary>
    public void Fail(Exception ex)
    {
        Status = ReviewSessionStatus.Failed;
        CompletedAtUtc = DateTime.UtcNow;
        TotalDurationMs = (long)(CompletedAtUtc.Value - RequestedAtUtc).TotalMilliseconds;
        ErrorMessage = ex.Message;
        ErrorType = ex.GetType().Name;
    }
}

/// <summary>
/// Lifecycle status of a review session.
/// </summary>
public enum ReviewSessionStatus
{
    /// <summary>Review request received but not yet started.</summary>
    Queued,

    /// <summary>Review is actively executing.</summary>
    InProgress,

    /// <summary>Review completed successfully.</summary>
    Completed,

    /// <summary>Review failed with an error.</summary>
    Failed,
}
