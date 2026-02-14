using System.Text.Json.Serialization;

namespace AiCodeReview.Models;

/// <summary>
/// Tracks what was reviewed and when, stored as PR properties in Azure DevOps.
/// Used to decide whether a re-review is needed and to prevent duplicate comments.
/// </summary>
public class ReviewMetadata
{
    /// <summary>The source commit SHA that was last reviewed.</summary>
    public string? LastReviewedSourceCommit { get; set; }

    /// <summary>The target commit SHA that was last reviewed.</summary>
    public string? LastReviewedTargetCommit { get; set; }

    /// <summary>The iteration number that was last reviewed.</summary>
    public int LastReviewedIteration { get; set; }

    /// <summary>Whether the PR was in draft mode during the last review.</summary>
    public bool WasDraft { get; set; }

    /// <summary>UTC timestamp of the last review.</summary>
    public DateTime ReviewedAtUtc { get; set; }

    /// <summary>True if a reviewer vote was successfully submitted.</summary>
    public bool VoteSubmitted { get; set; }

    /// <summary>Total number of reviews performed on this PR (1-based).</summary>
    public int ReviewCount { get; set; }

    /// <summary>Returns true if we have any prior review data.</summary>
    public bool HasPreviousReview => !string.IsNullOrEmpty(LastReviewedSourceCommit);

    /// <summary>Check if code has changed since the last review.</summary>
    public bool HasCodeChanged(string currentSourceCommit) =>
        !string.Equals(LastReviewedSourceCommit, currentSourceCommit, StringComparison.OrdinalIgnoreCase);

    /// <summary>Check if this is a draft-to-active transition with no code changes.</summary>
    public bool IsDraftToActiveTransition(bool currentlyDraft, string currentSourceCommit) =>
        WasDraft && !currentlyDraft && !HasCodeChanged(currentSourceCommit);
}

/// <summary>
/// A single entry in the review history log, stored in PR properties and appended to PR description.
/// </summary>
public class ReviewHistoryEntry
{
    public int ReviewNumber { get; set; }
    public DateTime ReviewedAtUtc { get; set; }
    public string Action { get; set; } = string.Empty;       // "Full Review", "Re-Review", "Vote Only"
    public string Verdict { get; set; } = string.Empty;       // "Approved", "Needs Work", etc.
    public string SourceCommit { get; set; } = string.Empty;  // short SHA
    public int Iteration { get; set; }
    public bool IsDraft { get; set; }
    public int InlineComments { get; set; }
    public int FilesChanged { get; set; }
    public int? Vote { get; set; }

    // ── AI Metrics ──────────────────────────────────────────────────────
    public string? ModelName { get; set; }
    public int? PromptTokens { get; set; }
    public int? CompletionTokens { get; set; }
    public int? TotalTokens { get; set; }
    public long? AiDurationMs { get; set; }
    public long? TotalDurationMs { get; set; }
}

/// <summary>
/// Represents an existing comment thread on a PR, used for deduplication and re-review resolution.
/// </summary>
public class ExistingCommentThread
{
    /// <summary>ADO thread ID — needed to update thread status (e.g., resolve as Fixed).</summary>
    public int ThreadId { get; set; }

    public string? FilePath { get; set; }
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Content { get; set; } = string.Empty;

    /// <summary>Current thread status (1=Active, 2=Fixed, 3=WontFix, 4=Closed, etc.).</summary>
    public int Status { get; set; }

    /// <summary>True if this thread was posted by the AI reviewer (detected via attribution tag).</summary>
    public bool IsAiGenerated { get; set; }
}

/// <summary>
/// Result of AI verification for whether a prior review comment's issue was addressed.
/// </summary>
public class ThreadVerificationResult
{
    /// <summary>The ADO thread ID being verified.</summary>
    public int ThreadId { get; set; }

    /// <summary>True if the AI confirms the issue described in the original comment was addressed.</summary>
    public bool IsFixed { get; set; }

    /// <summary>Brief reasoning from the AI about why the issue is fixed or not.</summary>
    public string Reasoning { get; set; } = string.Empty;
}

/// <summary>
/// A candidate thread whose commented lines were modified, pending AI verification.
/// </summary>
public class ThreadVerificationCandidate
{
    /// <summary>The ADO thread ID.</summary>
    public int ThreadId { get; set; }

    /// <summary>Repo-relative file path.</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Start line of the original comment.</summary>
    public int StartLine { get; set; }

    /// <summary>End line of the original comment.</summary>
    public int EndLine { get; set; }

    /// <summary>The original AI review comment text.</summary>
    public string OriginalComment { get; set; } = string.Empty;

    /// <summary>The current code around the commented lines (context window).</summary>
    public string CurrentCode { get; set; } = string.Empty;
}
