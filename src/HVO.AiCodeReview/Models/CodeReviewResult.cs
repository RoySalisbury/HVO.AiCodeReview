using System.Text.Json.Serialization;

namespace AiCodeReview.Models;

/// <summary>
/// Structured AI review output. Deserialized from the AI's JSON response.
/// </summary>
public class CodeReviewResult
{
    [JsonPropertyName("summary")]
    public ReviewSummary Summary { get; set; } = new();

    [JsonPropertyName("fileReviews")]
    public List<FileReview> FileReviews { get; set; } = new();

    [JsonPropertyName("inlineComments")]
    public List<InlineComment> InlineComments { get; set; } = new();

    [JsonPropertyName("observations")]
    public List<string> Observations { get; set; } = new();

    [JsonPropertyName("recommendedVote")]
    public int RecommendedVote { get; set; } = 10;

    // ── AI Usage Metrics (populated by CodeReviewService, not from AI JSON) ──

    /// <summary>Model deployment/name used for the review.</summary>
    [JsonIgnore]
    public string? ModelName { get; set; }

    /// <summary>Number of prompt (input) tokens used.</summary>
    [JsonIgnore]
    public int? PromptTokens { get; set; }

    /// <summary>Number of completion (output) tokens used.</summary>
    [JsonIgnore]
    public int? CompletionTokens { get; set; }

    /// <summary>Total tokens (prompt + completion).</summary>
    [JsonIgnore]
    public int? TotalTokens { get; set; }

    /// <summary>Time spent waiting for the AI response, in milliseconds.</summary>
    [JsonIgnore]
    public long? AiDurationMs { get; set; }
}

public class ReviewSummary
{
    [JsonPropertyName("filesChanged")]
    public int FilesChanged { get; set; }

    [JsonPropertyName("editsCount")]
    public int EditsCount { get; set; }

    [JsonPropertyName("addsCount")]
    public int AddsCount { get; set; }

    [JsonPropertyName("deletesCount")]
    public int DeletesCount { get; set; }

    [JsonPropertyName("commitsCount")]
    public int CommitsCount { get; set; }

    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    [JsonPropertyName("fileInventory")]
    public List<string> FileInventory { get; set; } = new();

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = "APPROVED";

    [JsonPropertyName("verdictJustification")]
    public string VerdictJustification { get; set; } = string.Empty;
}

public class FileReview
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("verdict")]
    public string Verdict { get; set; } = string.Empty;

    [JsonPropertyName("reviewText")]
    public string ReviewText { get; set; } = string.Empty;
}

public class InlineComment
{
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    [JsonPropertyName("startLine")]
    public int StartLine { get; set; }

    [JsonPropertyName("endLine")]
    public int EndLine { get; set; }

    [JsonPropertyName("leadIn")]
    public string LeadIn { get; set; } = string.Empty;

    [JsonPropertyName("comment")]
    public string Comment { get; set; } = string.Empty;

    /// <summary>
    /// A short snippet (1-3 lines) of the actual code being commented on.
    /// Used for programmatic line-number resolution when AI-provided line numbers are inaccurate.
    /// </summary>
    [JsonPropertyName("codeSnippet")]
    public string? CodeSnippet { get; set; }

    /// <summary>"closed" for approved items, "active" for items needing attention.</summary>
    [JsonPropertyName("status")]
    public string Status { get; set; } = "closed";
}
