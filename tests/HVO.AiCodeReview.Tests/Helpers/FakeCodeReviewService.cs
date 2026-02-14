using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests.Helpers;

/// <summary>
/// Fake code review service that returns deterministic, controlled review
/// results so we can test the orchestrator / dedup / metadata logic without
/// calling Azure OpenAI.  The inline comments returned are stable across
/// calls (same file, line, content) which lets us verify deduplication.
/// </summary>
public class FakeCodeReviewService : ICodeReviewService
{
    /// <summary>
    /// Override this to return custom results in a specific test.
    /// When null the default fake result is used.
    /// </summary>
    public Func<PullRequestInfo, List<FileChange>, CodeReviewResult>? ResultFactory { get; set; }

    public Task<CodeReviewResult> ReviewAsync(PullRequestInfo pullRequest, List<FileChange> fileChanges)
    {
        if (ResultFactory is not null)
            return Task.FromResult(ResultFactory(pullRequest, fileChanges));

        // Build a deterministic review result keyed off the actual file list
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = fileChanges.Count,
                EditsCount = 0,
                AddsCount = fileChanges.Count,
                DeletesCount = 0,
                CommitsCount = 1,
                Description = "Test review — fake analysis.",
                Verdict = "APPROVED WITH SUGGESTIONS",
                VerdictJustification = "Automated test review.",
            },
            FileReviews = fileChanges.Select(f => new FileReview
            {
                FilePath = f.FilePath,
                Verdict = "APPROVED",
                ReviewText = $"Fake review of {f.FilePath}.",
            }).ToList(),
            InlineComments = BuildDeterministicComments(fileChanges),
            Observations = new List<string> { "This is a fake observation for testing." },
            RecommendedVote = 5, // Approved with suggestions
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Review a single file — used by the parallel per-file orchestration.
    /// Returns a deterministic result for the single file.
    /// </summary>
    public Task<CodeReviewResult> ReviewFileAsync(PullRequestInfo pullRequest, FileChange file, int totalFilesInPr)
    {
        ArgumentNullException.ThrowIfNull(file);

        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 1,
                AddsCount = file.ChangeType == "add" ? 1 : 0,
                EditsCount = file.ChangeType == "edit" ? 1 : 0,
                DeletesCount = file.ChangeType == "delete" ? 1 : 0,
                CommitsCount = 1,
                Description = $"Single-file review of {file.FilePath}.",
                Verdict = "APPROVED WITH SUGGESTIONS",
                VerdictJustification = $"Fake single-file review of {file.FilePath}.",
            },
            FileReviews = new List<FileReview>
            {
                new FileReview
                {
                    FilePath = file.FilePath,
                    Verdict = "APPROVED",
                    ReviewText = $"Fake single-file review of {file.FilePath}.",
                }
            },
            InlineComments = BuildDeterministicComments(new List<FileChange> { file }),
            Observations = new List<string> { $"Single-file observation for {file.FilePath}." },
            RecommendedVote = 5,
        };

        return Task.FromResult(result);
    }

    /// <summary>
    /// Fake thread verification — by default marks all candidates as fixed.
    /// Override via VerificationResultFactory for custom behavior.
    /// </summary>
    public Func<List<ThreadVerificationCandidate>, List<ThreadVerificationResult>>? VerificationResultFactory { get; set; }

    public Task<List<ThreadVerificationResult>> VerifyThreadResolutionsAsync(List<ThreadVerificationCandidate> candidates)
    {
        if (VerificationResultFactory is not null)
            return Task.FromResult(VerificationResultFactory(candidates));

        // Default: mark all candidates as fixed (optimistic for most tests)
        var results = candidates.Select(c => new ThreadVerificationResult
        {
            ThreadId = c.ThreadId,
            IsFixed = true,
            Reasoning = "Fake verification — marked as fixed.",
        }).ToList();

        return Task.FromResult(results);
    }

    /// <summary>
    /// Returns exactly 2 inline comments per file — these are deterministic
    /// so the dedup logic can match them on subsequent calls.
    /// </summary>
    private static List<InlineComment> BuildDeterministicComments(List<FileChange> files)
    {
        var comments = new List<InlineComment>();

        foreach (var file in files)
        {
            comments.Add(new InlineComment
            {
                FilePath = file.FilePath,
                StartLine = 5,
                EndLine = 10,
                LeadIn = "Suggestion",
                Comment = $"Consider adding a file header to {file.FilePath}.",
                Status = "closed",
            });

            comments.Add(new InlineComment
            {
                FilePath = file.FilePath,
                StartLine = 2,
                EndLine = 3,
                LeadIn = "Concern",
                Comment = $"Review the content of {file.FilePath} for consistency.",
                Status = "active",
            });
        }

        return comments;
    }
}
