using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Abstracts Azure DevOps REST API interactions. All methods accept project
/// and repository per-request; only Organization and PAT come from config.
/// </summary>
public interface IAzureDevOpsService
{
    /// <summary>Fetch PR metadata including reviewers.</summary>
    Task<PullRequestInfo> GetPullRequestAsync(string project, string repository, int pullRequestId);

    /// <summary>Get the current iteration count for the PR.</summary>
    Task<int> GetIterationCountAsync(string project, string repository, int pullRequestId);

    /// <summary>Check if the AI review tag is present on this PR.</summary>
    Task<bool> HasReviewTagAsync(string project, string repository, int pullRequestId);

    /// <summary>Add the AI review tag to the PR.</summary>
    Task AddReviewTagAsync(string project, string repository, int pullRequestId);

    /// <summary>Get stored review metadata from PR properties.</summary>
    Task<ReviewMetadata> GetReviewMetadataAsync(string project, string repository, int pullRequestId);

    /// <summary>Store review metadata as PR properties.</summary>
    Task SetReviewMetadataAsync(string project, string repository, int pullRequestId, ReviewMetadata metadata);

    /// <summary>Get the full review history from PR properties.</summary>
    Task<List<ReviewHistoryEntry>> GetReviewHistoryAsync(string project, string repository, int pullRequestId);

    /// <summary>Append a review history entry to PR properties.</summary>
    Task AppendReviewHistoryAsync(string project, string repository, int pullRequestId, ReviewHistoryEntry entry);

    /// <summary>Get existing AI-posted comment threads for deduplication.</summary>
    Task<List<ExistingCommentThread>> GetExistingReviewThreadsAsync(string project, string repository, int pullRequestId, string? attributionTag = null);

    /// <summary>Update a thread's status (e.g., mark as Fixed, Closed, Active).</summary>
    Task UpdateThreadStatusAsync(string project, string repository, int pullRequestId, int threadId, string status);

    /// <summary>Count existing AI review summary comments (survives metadata clears).</summary>
    Task<int> CountReviewSummaryCommentsAsync(string project, string repository, int pullRequestId);

    /// <summary>Retrieve file-level changes between source and target commits.</summary>
    Task<List<FileChange>> GetPullRequestChangesAsync(string project, string repository, int pullRequestId, PullRequestInfo prInfo);

    /// <summary>Post a general (non-file-specific) comment thread to the PR.</summary>
    Task PostCommentThreadAsync(string project, string repository, int pullRequestId, string content, string status = "closed");

    /// <summary>Post an inline comment thread on a specific file and line range.</summary>
    Task PostInlineCommentThreadAsync(
        string project, string repository, int pullRequestId,
        string filePath, int startLine, int endLine,
        string content, string status = "closed");

    /// <summary>Add the service identity as a reviewer and cast a vote.</summary>
    Task AddReviewerAsync(string project, string repository, int pullRequestId, int vote);

    /// <summary>Update the PR description (appends or replaces content).</summary>
    Task UpdatePrDescriptionAsync(string project, string repository, int pullRequestId, string newDescription);
}
