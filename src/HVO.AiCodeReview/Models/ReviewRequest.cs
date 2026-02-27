using System.ComponentModel.DataAnnotations;

namespace AiCodeReview.Models;

/// <summary>
/// Request DTO for the POST /api/review endpoint.
/// </summary>
public class ReviewRequest
{
    /// <summary>Azure DevOps project name (e.g., "Dynamit").</summary>
    [Required]
    public string ProjectName { get; set; } = string.Empty;

    /// <summary>Repository name (e.g., "Dynamit") or repository GUID.</summary>
    [Required]
    public string RepositoryName { get; set; } = string.Empty;

    /// <summary>Pull request ID.</summary>
    [Required]
    [Range(1, int.MaxValue)]
    public int PullRequestId { get; set; }

    /// <summary>
    /// When true, bypasses the skip/dedup logic and forces a full re-review
    /// even if the code hasn't changed since the last review.
    /// </summary>
    public bool ForceReview { get; set; }

    /// <summary>
    /// When true, runs the full AI review but does NOT post anything to the PR
    /// (no comments, no summary, no vote, no metadata updates).
    /// The review result is returned in the API response only.
    /// </summary>
    public bool SimulationOnly { get; set; }
}
