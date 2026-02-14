using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// AI-powered code review. Accepts structured file changes and returns
/// a structured review result.
/// </summary>
public interface ICodeReviewService
{
    /// <summary>
    /// Analyze the given file changes and produce a structured code review.
    /// </summary>
    /// <param name="pullRequest">PR metadata for context.</param>
    /// <param name="fileChanges">List of changed files with content.</param>
    /// <returns>Structured review result ready for posting.</returns>
    Task<CodeReviewResult> ReviewAsync(PullRequestInfo pullRequest, List<FileChange> fileChanges);

    /// <summary>
    /// Review a single file in isolation. The AI gets focused attention on ONE file,
    /// producing more accurate line-specific inline comments.
    /// </summary>
    /// <param name="pullRequest">PR metadata for context.</param>
    /// <param name="file">The single file to review.</param>
    /// <param name="totalFilesInPr">Total files changed in the PR (for context).</param>
    /// <returns>Review result scoped to this single file.</returns>
    Task<CodeReviewResult> ReviewFileAsync(PullRequestInfo pullRequest, FileChange file, int totalFilesInPr);

    /// <summary>
    /// Verify whether prior AI review comments have been addressed in the current code.
    /// Used during re-review to avoid auto-resolving threads whose lines changed
    /// for unrelated reasons.
    /// </summary>
    /// <param name="candidates">Candidate threads with original comment + current code context.</param>
    /// <returns>Verification results indicating which threads are truly fixed.</returns>
    Task<List<ThreadVerificationResult>> VerifyThreadResolutionsAsync(List<ThreadVerificationCandidate> candidates);
}
