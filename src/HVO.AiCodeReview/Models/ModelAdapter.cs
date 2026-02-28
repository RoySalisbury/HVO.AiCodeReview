namespace AiCodeReview.Models;

/// <summary>
/// Root config for the model adapter catalog (<c>model-adapters.json</c>).
/// Contains an ordered list of adapters; the first matching adapter wins.
/// </summary>
public class ModelAdapterConfig
{
    /// <summary>
    /// Ordered list of model adapters. Evaluated top-to-bottom; the first
    /// adapter whose <see cref="ModelAdapter.ModelPattern"/> matches the
    /// active model name is selected.
    /// </summary>
    public List<ModelAdapter> Adapters { get; set; } = new();
}

/// <summary>
/// Per-model tuning parameters and prompt guidance.
/// All numeric fields are nullable — when null the value from <see cref="ReviewProfile"/>
/// or <see cref="AiProviderSettings"/> is used unchanged.
/// </summary>
public class ModelAdapter
{
    /// <summary>Human-readable adapter name (for logging).</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Regex pattern matched (case-insensitive) against the deployment / model name.
    /// Use <c>.*</c> for a catch-all default adapter. First match wins.
    /// </summary>
    public string ModelPattern { get; set; } = string.Empty;

    // ─── Parameter overrides ────────────────────────────────────────────

    /// <summary>Override the sampling temperature for this model.</summary>
    public float? Temperature { get; set; }

    /// <summary>Override max output tokens for batch (multi-file) reviews.</summary>
    public int? MaxOutputTokensBatch { get; set; }

    /// <summary>Override max output tokens for single-file reviews.</summary>
    public int? MaxOutputTokensSingleFile { get; set; }

    /// <summary>Override max output tokens for thread verification calls.</summary>
    public int? MaxOutputTokensVerification { get; set; }

    /// <summary>Override max output tokens for PR-summary (Pass 1) calls.</summary>
    public int? MaxOutputTokensPrSummary { get; set; }

    /// <summary>Override the per-file input line truncation limit.</summary>
    public int? MaxInputLinesPerFile { get; set; }

    // ─── Prompt tuning ──────────────────────────────────────────────────

    /// <summary>
    /// Prompt style hint: <c>"imperative"</c> (direct commands) or
    /// <c>"conversational"</c> (softer phrasing). Currently informational.
    /// </summary>
    public string PromptStyle { get; set; } = "imperative";

    /// <summary>
    /// Model-specific format instructions injected into the prompt pipeline
    /// between the Identity layer and Custom Instructions layer.
    /// Use this for model-specific JSON reinforcement, output constraints, etc.
    /// </summary>
    public string? Preamble { get; set; }

    /// <summary>
    /// Documented model quirks for debugging / reference.
    /// These are logged but not injected into prompts.
    /// </summary>
    public List<string> Quirks { get; set; } = new();
}
