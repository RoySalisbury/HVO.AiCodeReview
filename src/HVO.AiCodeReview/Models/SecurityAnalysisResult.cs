using System.Text.Json.Serialization;

namespace AiCodeReview.Models;

/// <summary>
/// Result of the dedicated security-focused review pass. The AI receives all
/// file diffs and evaluates them against OWASP Top 10, hardcoded secrets,
/// injection risks, auth/authz patterns, and insecure defaults.
/// </summary>
public class SecurityAnalysisResult
{
    /// <summary>One-paragraph executive summary of the PR's security posture.</summary>
    [JsonPropertyName("executiveSummary")]
    public string ExecutiveSummary { get; set; } = string.Empty;

    /// <summary>
    /// Overall security risk level for the PR.
    /// "None" = no security concerns; "Critical" = must-fix before merge.
    /// </summary>
    [JsonPropertyName("overallRiskLevel")]
    public string OverallRiskLevel { get; set; } = "None";

    /// <summary>Individual security findings.</summary>
    [JsonPropertyName("findings")]
    public List<SecurityFinding> Findings { get; set; } = [];

    // ── AI Usage Metrics (populated by service, not from AI JSON) ──

    [JsonIgnore] public string? ModelName { get; set; }
    [JsonIgnore] public int? PromptTokens { get; set; }
    [JsonIgnore] public int? CompletionTokens { get; set; }
    [JsonIgnore] public int? TotalTokens { get; set; }
    [JsonIgnore] public long? AiDurationMs { get; set; }
}

/// <summary>
/// A single security vulnerability or concern found during the security review pass.
/// All security findings are treated as critical severity.
/// </summary>
public class SecurityFinding
{
    /// <summary>
    /// Severity is always "Critical" for security findings per AC requirement.
    /// Retained as a property for JSON schema consistency with consumers.
    /// </summary>
    [JsonPropertyName("severity")]
    public string Severity { get; set; } = "Critical";

    /// <summary>
    /// CWE identifier (e.g., "CWE-79", "CWE-89", "CWE-798"). Null when no standard CWE applies.
    /// </summary>
    [JsonPropertyName("cweId")]
    public string? CweId { get; set; }

    /// <summary>
    /// OWASP Top 10 category (e.g., "A01:2021 — Broken Access Control").
    /// Null when the finding doesn't map to a specific OWASP category.
    /// </summary>
    [JsonPropertyName("owaspCategory")]
    public string? OwaspCategory { get; set; }

    /// <summary>Description of the security vulnerability.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;

    /// <summary>File path where the vulnerability was found.</summary>
    [JsonPropertyName("filePath")]
    public string FilePath { get; set; } = string.Empty;

    /// <summary>
    /// Line number (approximate) where the vulnerability exists.
    /// Zero when the finding is file-level rather than line-specific.
    /// </summary>
    [JsonPropertyName("lineNumber")]
    public int LineNumber { get; set; }

    /// <summary>Suggested remediation for the vulnerability.</summary>
    [JsonPropertyName("remediation")]
    public string Remediation { get; set; } = string.Empty;
}
