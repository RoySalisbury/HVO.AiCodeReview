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

    /// <summary>
    /// Review depth level: Quick (Pass 1 only), Standard (Pass 1+2), Deep (Pass 1+2+3).
    /// Default: Standard (backward compatible — the current full pipeline).
    /// </summary>
    public ReviewDepth ReviewDepth { get; set; } = ReviewDepth.Standard;

    /// <summary>
    /// Pass 2 review strategy: FileByFile (default per-file), Auto (smart selection),
    /// or Vector (Assistants API + Vector Store for cross-file awareness).
    /// Only applies when ReviewDepth is Standard or Deep.
    /// </summary>
    public ReviewStrategy ReviewStrategy { get; set; } = ReviewStrategy.FileByFile;

    /// <summary>
    /// When true, runs an additional security-focused review pass that analyzes
    /// PRs for OWASP Top 10 vulnerabilities, hardcoded secrets, injection risks,
    /// auth/authz issues, and insecure defaults.
    /// When null, falls back to the global <c>AiProvider:SecurityPassEnabled</c> setting.
    /// </summary>
    public bool? EnableSecurityPass { get; set; }
}
