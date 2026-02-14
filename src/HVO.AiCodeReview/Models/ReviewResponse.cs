namespace AiCodeReview.Models;

/// <summary>
/// Response DTO returned by the POST /api/review endpoint.
/// </summary>
public class ReviewResponse
{
    /// <summary>Outcome of the review request: Reviewed, Skipped, Error.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Overall recommendation: Approved, ApprovedWithSuggestions, NeedsWork, Rejected. Null if skipped/error.</summary>
    public string? Recommendation { get; set; }

    /// <summary>The full summary comment that was posted to the PR.</summary>
    public string? Summary { get; set; }

    /// <summary>Total number of issues found.</summary>
    public int IssueCount { get; set; }

    /// <summary>Number of error-severity issues.</summary>
    public int ErrorCount { get; set; }

    /// <summary>Number of warning-severity issues.</summary>
    public int WarningCount { get; set; }

    /// <summary>Number of info/observation issues.</summary>
    public int InfoCount { get; set; }

    /// <summary>Error message if Status is "Error".</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The vote that was cast (10, 5, -5, -10).</summary>
    public int? Vote { get; set; }
}
