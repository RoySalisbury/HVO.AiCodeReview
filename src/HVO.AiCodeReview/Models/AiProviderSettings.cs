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
    /// Default maximum number of source lines sent to AI per file.
    /// Files longer than this are truncated with a marker.
    /// Can be overridden per-provider via <see cref="ProviderConfig.MaxInputLinesPerFile"/>.
    /// </summary>
    public int MaxInputLinesPerFile { get; set; } = 5000;

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
    public Dictionary<string, ProviderConfig> Providers { get; set; } = [];

    /// <summary>
    /// Maps <see cref="ReviewDepth"/> names to provider keys in <see cref="Providers"/>.
    /// When configured, each review depth uses a different AI model/deployment.
    /// Example: <c>{ "Quick": "azure-openai-mini", "Standard": "azure-openai", "Deep": "azure-openai-o1" }</c>.
    /// When not configured (empty), all depths use <see cref="ActiveProvider"/>.
    /// </summary>
    public Dictionary<string, string> DepthModels { get; set; } = [];

    /// <summary>
    /// Maps <see cref="ReviewPass"/> names to provider keys in <see cref="Providers"/>.
    /// When configured, each review pass uses a different AI model/deployment,
    /// enabling task-specific model routing — cheaper models for simple tasks,
    /// premium models for deep analysis, and separate deployments to avoid
    /// rate-limit contention between passes.
    /// Example:
    /// <code>
    /// {
    ///   "PrSummary": "azure-openai-mini",
    ///   "PerFileReview": "azure-openai",
    ///   "DeepReview": "azure-openai-o1",
    ///   "SecurityPass": "azure-openai-mini",
    ///   "ThreadVerification": "azure-openai-mini"
    /// }
    /// </code>
    /// When not configured (empty), falls back to <see cref="DepthModels"/> then <see cref="ActiveProvider"/>.
    /// Takes priority over <see cref="DepthModels"/> for any pass that has a mapping.
    /// </summary>
    public Dictionary<string, string> PassRouting { get; set; } = [];

    /// <summary>
    /// Global default for the security-focused review pass.
    /// When true, every review request includes a dedicated security pass unless
    /// the per-request <see cref="ReviewRequest.EnableSecurityPass"/> overrides it.
    /// Default: false (security pass is opt-in).
    /// </summary>
    public bool SecurityPassEnabled { get; set; }
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

    /// <summary>
    /// Per-provider override for the maximum source lines sent to AI per file.
    /// When null, the global <see cref="AiProviderSettings.MaxInputLinesPerFile"/> is used.
    /// </summary>
    public int? MaxInputLinesPerFile { get; set; }
}
