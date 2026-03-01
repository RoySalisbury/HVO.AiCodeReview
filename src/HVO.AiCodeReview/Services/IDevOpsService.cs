using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Provider-agnostic interface for DevOps platform interactions (pull requests,
/// comments, work items, etc.).  All methods accept project and repository
/// per-request so the same service instance can target multiple repos.
/// Implementations exist for Azure DevOps today; GitHub can be added later.
/// </summary>
public interface IDevOpsService : IDisposable
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

    /// <summary>Reply to an existing comment thread (e.g., follow-up on a prior review comment).</summary>
    Task ReplyToThreadAsync(string project, string repository, int pullRequestId, int threadId, string content);

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

    /// <summary>Get the IDs of work items linked to a pull request.</summary>
    Task<List<int>> GetLinkedWorkItemIdsAsync(string project, string repository, int pullRequestId);

    /// <summary>Fetch work item details (title, type, description, acceptance criteria).</summary>
    Task<WorkItemInfo?> GetWorkItemAsync(string project, int workItemId);

    /// <summary>Fetch discussion comments for a work item.</summary>
    Task<List<WorkItemComment>> GetWorkItemCommentsAsync(string project, int workItemId);

    /// <summary>
    /// Get the set of file paths that changed between two iterations.
    /// Used for incremental (delta) reviews to identify which files need re-review.
    /// Returns an empty set if the iteration range is invalid or the API call fails.
    /// </summary>
    Task<HashSet<string>> GetIterationChangesAsync(string project, string repository, int pullRequestId,
        int baseIteration, int targetIteration);

    /// <summary>
    /// Resolve the service account identity ID (from config or auto-discovery).
    /// Useful for health checks to verify Azure DevOps connectivity.
    /// </summary>
    Task<string?> ResolveServiceIdentityAsync();
}
