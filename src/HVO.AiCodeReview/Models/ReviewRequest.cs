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
}
