namespace AiCodeReview.Models;

/// <summary>
/// Configurable review parameters that tune AI review behaviour.
/// Bind to the <c>ReviewProfile</c> section in appsettings.json.
/// All values have backward-compatible defaults matching the previous hardcoded values.
/// </summary>
public class ReviewProfile
{
    public const string SectionName = "ReviewProfile";

    /// <summary>
    /// AI temperature (0.0 = deterministic, 1.0 = creative).
    /// Lower values produce more consistent, less creative reviews.
    /// Default: 0.1 (highly deterministic).
    /// </summary>
    public float Temperature { get; set; } = 0.1f;

    /// <summary>
    /// Maximum output tokens for batch (multi-file) review calls.
    /// Default: 16000.
    /// </summary>
    public int MaxOutputTokensBatch { get; set; } = 16000;

    /// <summary>
    /// Maximum output tokens for single-file review calls.
    /// Default: 4000.
    /// </summary>
    public int MaxOutputTokensSingleFile { get; set; } = 4000;

    /// <summary>
    /// Maximum output tokens for thread verification calls.
    /// Default: 2000.
    /// </summary>
    public int MaxOutputTokensVerification { get; set; } = 2000;

    /// <summary>
    /// Maximum output tokens for Pass 1 (PR summary) calls.
    /// Default: 4000.
    /// </summary>
    public int MaxOutputTokensPrSummary { get; set; } = 4000;

    /// <summary>
    /// Verdict threshold configuration for future system-level verdict overrides.
    /// Currently aspirational — the AI determines the verdict in its JSON response.
    /// </summary>
    public VerdictThresholds VerdictThresholds { get; set; } = new();
}

/// <summary>
/// Thresholds for system-level verdict override (aspirational).
/// When the system counts enough critical or warning issues, it can
/// override the AI's verdict. Currently informational only.
/// </summary>
public class VerdictThresholds
{
    /// <summary>
    /// Number of critical-severity inline comments that triggers a REJECTED verdict.
    /// Default: 1 (one critical issue → reject).
    /// </summary>
    public int RejectOnCriticalCount { get; set; } = 1;

    /// <summary>
    /// Number of warning-severity inline comments that triggers a NEEDS WORK verdict.
    /// Default: 3.
    /// </summary>
    public int NeedsWorkOnWarningCount { get; set; } = 3;
}
