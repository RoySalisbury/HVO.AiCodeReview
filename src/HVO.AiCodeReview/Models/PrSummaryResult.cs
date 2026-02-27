using System.Text.Json.Serialization;

namespace AiCodeReview.Models;

/// <summary>
/// Result of Pass 1 (PR-level summary). The AI generates a cross-file
/// understanding of the PR so that Pass 2 (per-file reviews) can reference it.
/// </summary>
public class PrSummaryResult
{
    /// <summary>One-paragraph description of what the PR accomplishes.</summary>
    [JsonPropertyName("intent")]
    public string Intent { get; set; } = string.Empty;

    /// <summary>How the changes affect the overall architecture (if at all).</summary>
    [JsonPropertyName("architecturalImpact")]
    public string ArchitecturalImpact { get; set; } = string.Empty;

    /// <summary>
    /// Cross-file relationships the AI identified.
    /// Example: "FileA.cs renames method Foo → Bar; FileB.cs and FileC.cs are callers."
    /// </summary>
    [JsonPropertyName("crossFileRelationships")]
    public List<string> CrossFileRelationships { get; set; } = new();

    /// <summary>
    /// High-risk areas the per-file reviews should pay special attention to.
    /// </summary>
    [JsonPropertyName("riskAreas")]
    public List<RiskArea> RiskAreas { get; set; } = new();

    /// <summary>
    /// Logical groupings of related files in the PR.
    /// Helps reviewers understand which files form a cohesive change.
    /// </summary>
    [JsonPropertyName("fileGroupings")]
    public List<FileGrouping> FileGroupings { get; set; } = new();

    // ── AI Usage Metrics (populated by service, not from AI JSON) ──

    [JsonIgnore] public string? ModelName { get; set; }
    [JsonIgnore] public int? PromptTokens { get; set; }
    [JsonIgnore] public int? CompletionTokens { get; set; }
    [JsonIgnore] public int? TotalTokens { get; set; }
    [JsonIgnore] public long? AiDurationMs { get; set; }
}

/// <summary>
/// A high-risk area that warrants extra scrutiny in per-file review.
/// </summary>
public class RiskArea
{
    /// <summary>File path or area name.</summary>
    [JsonPropertyName("area")]
    public string Area { get; set; } = string.Empty;

    /// <summary>Why this area is risky.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; set; } = string.Empty;
}

/// <summary>
/// A logical grouping of related files in the PR.
/// </summary>
public class FileGrouping
{
    /// <summary>Name of the logical group (e.g., "Model changes", "API endpoints").</summary>
    [JsonPropertyName("groupName")]
    public string GroupName { get; set; } = string.Empty;

    /// <summary>File paths in this group.</summary>
    [JsonPropertyName("files")]
    public List<string> Files { get; set; } = new();

    /// <summary>Brief description of why these files are grouped together.</summary>
    [JsonPropertyName("description")]
    public string Description { get; set; } = string.Empty;
}
