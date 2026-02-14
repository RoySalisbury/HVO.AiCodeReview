using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Orchestrates the end-to-end code review flow: check → fetch → analyze → post → vote.
/// Reports progress via IProgress for future SSE streaming support.
/// </summary>
public interface ICodeReviewOrchestrator
{
    /// <summary>
    /// Execute a full code review for the given PR.
    /// </summary>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="repository">Repository name or ID.</param>
    /// <param name="pullRequestId">Pull request ID.</param>
    /// <param name="progress">Optional progress reporter for status updates.</param>
    /// <returns>Review response suitable for the API response body.</returns>
    Task<ReviewResponse> ExecuteReviewAsync(
        string project,
        string repository,
        int pullRequestId,
        IProgress<ReviewStatusUpdate>? progress = null);
}
