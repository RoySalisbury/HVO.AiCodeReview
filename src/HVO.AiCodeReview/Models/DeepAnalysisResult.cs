using System.Text.Json.Serialization;

namespace AiCodeReview.Models;

/// <summary>
/// Result of Pass 3 (Deep holistic re-evaluation). The AI receives the Pass 1 summary,
/// all per-file review results, the merged verdict, and inline comments, then re-evaluates
/// the entire PR holistically to catch cross-file issues and validate consistency.
/// </summary>
public class DeepAnalysisResult
{
    /// <summary>Executive summary of the entire PR from a holistic perspective.</summary>
    [JsonPropertyName("executiveSummary")]
    public string ExecutiveSummary { get; set; } = string.Empty;

    /// <summary>
    /// Cross-file issues that per-file reviews missed.
    /// These are issues only visible when considering multiple files together.
    /// </summary>
    [JsonPropertyName("crossFileIssues")]
    public List<CrossFileIssue> CrossFileIssues { get; set; } = new();

    /// <summary>
    /// Assessment of whether the per-file verdicts are consistent with the overall PR quality.
    /// </summary>
    [JsonPropertyName("verdictConsistency")]
    public VerdictConsistencyAssessment VerdictConsistency { get; set; } = new();

    /// <summary>
    /// Overall risk assessment considering all files together.
    /// </summary>
    [JsonPropertyName("overallRiskLevel")]
    public string OverallRiskLevel { get; set; } = "Low";

    /// <summary>
    /// Key recommendations that span multiple files.
    /// </summary>
    [JsonPropertyName("recommendations")]
    public List<string> Recommendations { get; set; } = new();

    // ── AI Usage Metrics (populated by service, not from AI JSON) ──

    [JsonIgnore] public string? ModelName { get; set; }
    [JsonIgnore] public int? PromptTokens { get; set; }
    [JsonIgnore] public int? CompletionTokens { get; set; }
    [JsonIgnore] public int? TotalTokens { get; set; }
    [JsonIgnore] public long? AiDurationMs { get; set; }
}

/// <summary>
/// A cross-file issue that was only visible when reviewing the PR holistically.
/// </summary>
public class CrossFileIssue
{
    /// <summary>Files involved in this cross-file issue.</summary>
    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = new();

    /// <summary>Severity: "Error", "Warning", or "Info".</summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Info";

    /// <summary>Description of the cross-file issue.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}

/// <summary>
/// Assessment of whether per-file verdicts are consistent with the overall review.
/// </summary>
public class VerdictConsistencyAssessment
{
    /// <summary>Whether the merged verdict is consistent with the per-file verdicts.</summary>
    [JsonPropertyName("isConsistent")]
    public bool IsConsistent { get; set; } = true;

    /// <summary>Explanation of any inconsistencies found.</summary>
    [JsonPropertyName("explanation")]
    public string Explanation { get; set; } = string.Empty;

    /// <summary>Recommended verdict override, if the deep analysis disagrees with the merged result. Null if consistent.</summary>
    [JsonPropertyName("recommendedVerdict")]
    public string? RecommendedVerdict { get; set; }

    /// <summary>Recommended vote override. Null if consistent.</summary>
    [JsonPropertyName("recommendedVote")]
    public int? RecommendedVote { get; set; }
}
