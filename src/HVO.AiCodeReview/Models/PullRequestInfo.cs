namespace AiCodeReview.Models;

/// <summary>
/// Internal model representing PR metadata fetched from Azure DevOps.
/// </summary>
public class PullRequestInfo
{
    public int PullRequestId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceBranch { get; set; } = string.Empty;
    public string TargetBranch { get; set; } = string.Empty;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedDate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string LastMergeSourceCommit { get; set; } = string.Empty;
    public string LastMergeTargetCommit { get; set; } = string.Empty;
    public bool IsDraft { get; set; }
    public List<PullRequestReviewer> Reviewers { get; set; } = [];

    /// <summary>
    /// Cross-file summary produced by Pass 1 of the two-pass review.
    /// Injected by the orchestrator before dispatching per-file (Pass 2) reviews.
    /// Null when Pass 1 was skipped or failed.
    /// </summary>
    public PrSummaryResult? CrossFileSummary { get; set; }

    /// <summary>
    /// Architecture and convention context parsed from <c>.ai-review.yaml</c> or
    /// <c>.ai-review.json</c> in the reviewed repository's root.
    /// Injected by the orchestrator so prompts reference the repo's architecture.
    /// Null when no config file exists.
    /// </summary>
    public ArchitectureContext? ArchitectureContext { get; set; }
}

public class PullRequestReviewer
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Vote { get; set; }
}
