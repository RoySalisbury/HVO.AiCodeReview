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
    public List<PullRequestReviewer> Reviewers { get; set; } = new();
}

public class PullRequestReviewer
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int Vote { get; set; }
}
