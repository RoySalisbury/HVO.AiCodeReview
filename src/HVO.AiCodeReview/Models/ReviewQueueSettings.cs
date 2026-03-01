namespace AiCodeReview.Models;

/// <summary>
/// Configuration for the review queue and worker pool.
/// Bound from the "ReviewQueue" section in appsettings.json.
/// When <see cref="Enabled"/> is false, reviews execute synchronously
/// on the request thread (original behavior).
/// </summary>
public class ReviewQueueSettings
{
    public const string SectionName = "ReviewQueue";

    /// <summary>
    /// Whether the review queue is enabled.
    /// When false, POST /api/review runs synchronously (backward compatible).
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maximum number of reviews that can run concurrently.
    /// Each review occupies one worker slot.
    /// </summary>
    public int MaxConcurrentReviews { get; set; } = 3;

    /// <summary>
    /// Maximum number of reviews that can be queued.
    /// Returns 503 Service Unavailable when the queue is full.
    /// </summary>
    public int MaxQueueDepth { get; set; } = 50;

    /// <summary>
    /// System-wide limit on concurrent AI inference calls across all active reviews.
    /// Prevents 429 rate-limit cascades from the Azure OpenAI deployment.
    /// </summary>
    public int MaxConcurrentAiCalls { get; set; } = 8;

    /// <summary>
    /// Maximum time (in minutes) a review session can run before being cancelled.
    /// </summary>
    public int SessionTimeoutMinutes { get; set; } = 30;
}
