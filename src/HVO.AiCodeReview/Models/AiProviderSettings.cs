namespace AiCodeReview.Models;

/// <summary>
/// Provider-agnostic AI settings consumed by the orchestrator.
/// Decouples the orchestrator from any specific AI provider (Azure OpenAI,
/// GitHub Copilot, Cursor, local models, etc.).
/// </summary>
public class AiProviderSettings
{
    public const string SectionName = "AiProvider";

    /// <summary>
    /// Maximum number of concurrent per-file AI review calls.
    /// Controls the SemaphoreSlim in the orchestrator's parallel review fan-out.
    /// </summary>
    public int MaxParallelReviews { get; set; } = 5;

    /// <summary>
    /// The review mode: "single" uses one provider; "consensus" fans out to
    /// multiple providers and merges results.
    /// </summary>
    public string Mode { get; set; } = "single";

    /// <summary>
    /// Which provider is active (when Mode = "single").
    /// Must match a key in <see cref="Providers"/>.
    /// </summary>
    public string ActiveProvider { get; set; } = "azure-openai";

    /// <summary>
    /// Minimum number of providers that must flag an inline comment for it
    /// to be included in the consensus result. Only used when Mode = "consensus".
    /// Default 2 means at least 2 providers must agree.
    /// </summary>
    public int ConsensusThreshold { get; set; } = 2;

    /// <summary>
    /// Named provider configurations. Each key (e.g., "azure-openai", "local",
    /// "github-copilot") maps to a <see cref="ProviderConfig"/>.
    /// In consensus mode, ALL configured providers are called.
    /// In single mode, only <see cref="ActiveProvider"/> is used.
    /// </summary>
    public Dictionary<string, ProviderConfig> Providers { get; set; } = new();
}

/// <summary>
/// Configuration for a single AI provider. Only the fields relevant to
/// that provider type need to be populated.
/// </summary>
public class ProviderConfig
{
    /// <summary>
    /// Provider type identifier: "azure-openai", "github-copilot", "local", etc.
    /// Used by the factory to construct the correct ICodeReviewService implementation.
    /// </summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Display name for logging and attribution.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>API endpoint (Azure OpenAI endpoint, local server URL, etc.).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>API key or token.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model / deployment name (e.g., "gpt-4o", "llama-3.1-70b").</summary>
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// Custom instructions file path. If empty, the default system prompts are used.
    /// </summary>
    public string? CustomInstructionsPath { get; set; }

    /// <summary>
    /// Whether this provider is enabled. Set to false to temporarily disable
    /// a provider without removing its config.
    /// </summary>
    public bool Enabled { get; set; } = true;
}
