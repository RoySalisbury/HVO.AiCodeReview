namespace AiCodeReview.Models;

/// <summary>
/// Response DTO returned by the POST /api/review endpoint.
/// </summary>
public class ReviewResponse
{
    /// <summary>Outcome of the review request: Reviewed, Skipped, Error.</summary>
    public string Status { get; set; } = string.Empty;

    /// <summary>Overall recommendation: Approved, ApprovedWithSuggestions, NeedsWork, Rejected. Null if skipped/error.</summary>
    public string? Recommendation { get; set; }

    /// <summary>The full summary comment that was posted to the PR.</summary>
    public string? Summary { get; set; }

    /// <summary>Total number of issues found.</summary>
    public int IssueCount { get; set; }

    /// <summary>Number of error-severity issues.</summary>
    public int ErrorCount { get; set; }

    /// <summary>Number of warning-severity issues.</summary>
    public int WarningCount { get; set; }

    /// <summary>Number of info/observation issues.</summary>
    public int InfoCount { get; set; }

    /// <summary>Error message if Status is "Error".</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>The vote that was cast (10, 5, -5, -10).</summary>
    public int? Vote { get; set; }

    /// <summary>Which review depth mode was used (Quick, Standard, Deep).</summary>
    public string? ReviewDepth { get; set; }

    // ── Simulation-mode detail fields (populated when SimulationOnly=true) ──

    /// <summary>Individual inline comments the AI produced (only in simulation mode).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<InlineCommentDto>? InlineComments { get; set; }

    /// <summary>Per-file review verdicts and commentary (only in simulation mode).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<FileReviewDto>? FileReviews { get; set; }

    /// <summary>Files that were excluded from AI review (only in simulation mode).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<SkippedFileDto>? SkippedFiles { get; set; }

    /// <summary>Overall verdict from the merged review (e.g. APPROVED, NEEDS WORK).</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? Verdict { get; set; }

    /// <summary>Justification for the verdict.</summary>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? VerdictJustification { get; set; }
}

/// <summary>Inline comment DTO for simulation responses.</summary>
public class InlineCommentDto
{
    public string FilePath { get; set; } = string.Empty;
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    public string Severity { get; set; } = string.Empty;
    public string Comment { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
}

/// <summary>Per-file review DTO for simulation responses.</summary>
public class FileReviewDto
{
    public string FilePath { get; set; } = string.Empty;
    public string Verdict { get; set; } = string.Empty;
    public string ReviewText { get; set; } = string.Empty;
}

/// <summary>Skipped file DTO for simulation responses.</summary>
public class SkippedFileDto
{
    public string FilePath { get; set; } = string.Empty;
    public string SkipReason { get; set; } = string.Empty;
}
