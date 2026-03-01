namespace AiCodeReview.Models;

/// <summary>
/// Root catalog containing all configurable review rules and scope definitions.
/// Loaded from <c>review-rules.json</c> at startup and optionally hot-reloaded.
/// </summary>
public class ReviewRuleCatalog
{
    /// <summary>Schema version for forward compatibility.</summary>
    public string Version { get; set; } = "1.0";

    /// <summary>
    /// Identity preamble shared across scopes that set <c>IncludeIdentity = true</c>.
    /// Establishes who the AI is and its quality bar.
    /// </summary>
    public string Identity { get; set; } = string.Empty;

    /// <summary>
    /// Scope definitions keyed by scope name (e.g., "batch", "single-file",
    /// "thread-verification", "pass-1").
    /// </summary>
    public Dictionary<string, PromptScope> Scopes { get; set; } = [];

    /// <summary>
    /// All configurable review rules. Each rule belongs to a scope and can be
    /// enabled/disabled, re-prioritized, or edited without a code change.
    /// </summary>
    public List<ReviewRule> Rules { get; set; } = [];
}

/// <summary>
/// Defines a prompt scope — the structural context that surrounds the rules.
/// </summary>
public class PromptScope
{
    /// <summary>
    /// When true, the shared Identity preamble is prepended to this scope's prompt.
    /// </summary>
    public bool IncludeIdentity { get; set; }

    /// <summary>
    /// When true, custom instructions (from <c>custom-instructions.json</c>) are
    /// injected between the identity and the scope preamble.
    /// </summary>
    public bool IncludeCustomInstructions { get; set; }

    /// <summary>
    /// Scope-specific context text emitted after identity/custom-instructions
    /// and before the numbered rules. Typically includes the JSON response
    /// schema and any scope-specific behavioral context.
    /// </summary>
    public string Preamble { get; set; } = string.Empty;

    /// <summary>
    /// Header line emitted before the numbered rule list (e.g., "Rules for the review:").
    /// </summary>
    public string RulesHeader { get; set; } = "Rules:";
}

/// <summary>
/// A single configurable review rule within the prompt.
/// </summary>
public class ReviewRule
{
    /// <summary>
    /// Unique identifier for the rule (e.g., "batch-r01", "single-r05").
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>
    /// Which scope this rule applies to: "batch", "single-file",
    /// "thread-verification", "pass-1".
    /// </summary>
    public string Scope { get; set; } = string.Empty;

    /// <summary>
    /// Functional category for grouping (e.g., "format", "review-quality",
    /// "verdict", "security", "line-numbers", "ac-dod", "file-handling",
    /// "scope-behavior").
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Ordering priority within the scope — lower numbers appear first.
    /// </summary>
    public int Priority { get; set; }

    /// <summary>
    /// When false, the rule is excluded from prompt assembly.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The full rule text emitted in the prompt (without the leading number).
    /// May span multiple lines and contain sub-items.
    /// </summary>
    public string Text { get; set; } = string.Empty;
}
