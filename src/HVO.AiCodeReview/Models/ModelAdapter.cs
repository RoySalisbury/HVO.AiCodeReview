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

    // ─── Model capabilities ─────────────────────────────────────────────

    /// <summary>
    /// When <c>true</c>, the model is an o-series reasoning model (o1, o3, etc.).
    /// Reasoning models do not support <c>Temperature</c> or
    /// <c>ResponseFormat = JSON</c> at the API level and require the
    /// <c>SetNewMaxCompletionTokensPropertyEnabled</c> opt-in.
    /// </summary>
    public bool IsReasoningModel { get; set; }

    /// <summary>Maximum context window size (input + output tokens combined).</summary>
    public int? ContextWindowSize { get; set; }

    /// <summary>Model-level hard limit on output tokens (distinct from per-call overrides above).</summary>
    public int? MaxOutputTokensModel { get; set; }

    // ─── Pricing ────────────────────────────────────────────────────────

    /// <summary>Cost in USD per 1 million input (prompt) tokens.</summary>
    public decimal? InputCostPer1MTokens { get; set; }

    /// <summary>Cost in USD per 1 million output (completion) tokens.</summary>
    public decimal? OutputCostPer1MTokens { get; set; }

    // ─── Rate limits ────────────────────────────────────────────────────

    /// <summary>Requests per minute (RPM) rate limit for this deployment.</summary>
    public int? RequestsPerMinute { get; set; }

    /// <summary>Tokens per minute (TPM) rate limit for this deployment.</summary>
    public int? TokensPerMinute { get; set; }

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

    // ─── Cost helpers ───────────────────────────────────────────────────

    /// <summary>
    /// Calculates the estimated cost in USD for a given number of prompt and
    /// completion tokens based on the configured pricing.
    /// Returns <c>null</c> if pricing data is not configured.
    /// </summary>
    public decimal? CalculateCost(int promptTokens, int completionTokens)
    {
        if (InputCostPer1MTokens is null || OutputCostPer1MTokens is null)
            return null;

        return (promptTokens / 1_000_000m * InputCostPer1MTokens.Value)
             + (completionTokens / 1_000_000m * OutputCostPer1MTokens.Value);
    }
}
