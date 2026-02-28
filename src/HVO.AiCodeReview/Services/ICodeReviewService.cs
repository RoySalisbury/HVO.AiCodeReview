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
    /// <param name="workItems">Linked work items with AC/DoD context (optional).</param>
    /// <returns>Structured review result ready for posting.</returns>
    Task<CodeReviewResult> ReviewAsync(PullRequestInfo pullRequest, List<FileChange> fileChanges, List<WorkItemInfo>? workItems = null);

    /// <summary>
    /// Review a single file in isolation. The AI gets focused attention on ONE file,
    /// producing more accurate line-specific inline comments.
    /// </summary>
    /// <param name="pullRequest">PR metadata for context.</param>
    /// <param name="file">The single file to review.</param>
    /// <param name="totalFilesInPr">Total files changed in the PR (for context).</param>
    /// <param name="workItems">Linked work items with AC/DoD context (optional).</param>
    /// <returns>Review result scoped to this single file.</returns>
    Task<CodeReviewResult> ReviewFileAsync(PullRequestInfo pullRequest, FileChange file, int totalFilesInPr, List<WorkItemInfo>? workItems = null);

    /// <summary>
    /// Verify whether prior AI review comments have been addressed in the current code.
    /// Used during re-review to avoid auto-resolving threads whose lines changed
    /// for unrelated reasons.
    /// </summary>
    /// <param name="candidates">Candidate threads with original comment + current code context.</param>
    /// <returns>Verification results indicating which threads are truly fixed.</returns>
    Task<List<ThreadVerificationResult>> VerifyThreadResolutionsAsync(List<ThreadVerificationCandidate> candidates);

    /// <summary>
    /// Pass 1 of the two-pass review: generate a PR-level summary that captures
    /// cross-file relationships, architectural impact, and risk areas.
    /// The result is injected into each Pass 2 (per-file) review as context.
    /// </summary>
    /// <param name="pullRequest">PR metadata.</param>
    /// <param name="fileChanges">All changed files in the PR.</param>
    /// <param name="workItems">Linked work items (optional).</param>
    /// <returns>PR-level summary, or null if summary generation is unsupported.</returns>
    Task<PrSummaryResult?> GeneratePrSummaryAsync(PullRequestInfo pullRequest, List<FileChange> fileChanges, List<WorkItemInfo>? workItems = null);

    /// <summary>
    /// Pass 3 of the deep review: holistically re-evaluate the merged review results.
    /// Receives the PR summary, all per-file review results, and inline comments,
    /// then identifies cross-file issues, validates verdict consistency, and produces
    /// an executive summary.
    /// </summary>
    /// <param name="pullRequest">PR metadata.</param>
    /// <param name="prSummary">Pass 1 PR-level summary (may be null if Pass 1 failed).</param>
    /// <param name="reviewResult">Merged review result from Pass 2.</param>
    /// <param name="fileChanges">All reviewed file changes.</param>
    /// <returns>Deep analysis result, or null if generation fails.</returns>
    Task<DeepAnalysisResult?> GenerateDeepAnalysisAsync(
        PullRequestInfo pullRequest,
        PrSummaryResult? prSummary,
        CodeReviewResult reviewResult,
        List<FileChange> fileChanges);
}
