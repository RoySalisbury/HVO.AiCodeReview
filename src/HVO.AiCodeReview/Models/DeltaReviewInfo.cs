namespace AiCodeReview.Models;

/// <summary>
/// Tracks delta (incremental) review information when a re-review only
/// processes files that changed since the last reviewed iteration.
/// </summary>
public class DeltaReviewInfo
{
    /// <summary>True if this review was performed as a delta/incremental review.</summary>
    public bool IsDeltaReview { get; set; }

    /// <summary>The iteration that was last reviewed (base for comparison).</summary>
    public int BaseIteration { get; set; }

    /// <summary>The current (target) iteration being reviewed.</summary>
    public int CurrentIteration { get; set; }

    /// <summary>Total file count in the PR (delta + unchanged + skipped).</summary>
    public int TotalFilesInPr { get; set; }

    /// <summary>Number of files that changed since the last review and were sent to AI.</summary>
    public int DeltaFilesReviewed { get; set; }

    /// <summary>Number of files that were unchanged and had results carried forward.</summary>
    public int CarriedForwardFiles { get; set; }

    /// <summary>File paths that changed since the last review.</summary>
    public List<string> ChangedFilePaths { get; set; } = [];

    /// <summary>File paths whose results were carried forward from the prior review.</summary>
    public List<string> CarriedForwardFilePaths { get; set; } = [];

    /// <summary>
    /// Estimated token savings from not re-reviewing unchanged files.
    /// Calculated as: (carriedForwardFiles / totalReviewableFiles) * priorTotalTokens
    /// </summary>
    public int EstimatedTokenSavings { get; set; }
}
