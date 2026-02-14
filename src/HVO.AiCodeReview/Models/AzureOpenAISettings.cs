namespace AiCodeReview.Models;

/// <summary>
/// Configuration for Azure OpenAI API access.
/// </summary>
public class AzureOpenAISettings
{
    public const string SectionName = "AzureOpenAI";

    /// <summary>Azure OpenAI resource endpoint (e.g., https://your-resource.openai.azure.com/).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Azure OpenAI API key.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Deployed model name (e.g., "gpt-4", "gpt-4o").</summary>
    public string DeploymentName { get; set; } = string.Empty;

    /// <summary>
    /// Path to a JSON file containing optional custom review instructions.
    /// These are injected between the identity preamble and the mandatory response
    /// format rules. Use this to add domain-specific guidance (e.g. "also check
    /// cyclomatic complexity"). Relative paths resolve from the app base directory.
    /// If empty or the file doesn't exist, no custom instructions are added.
    /// </summary>
    public string? CustomInstructionsPath { get; set; } = "custom-instructions.json";

    /// <summary>
    /// Maximum number of concurrent per-file AI review calls.
    /// Each file in a PR is reviewed in its own dedicated AI call for maximum
    /// line-number accuracy. This controls how many run in parallel.
    /// Higher values speed up large PRs but increase API load. Default is 5.
    /// </summary>
    public int MaxParallelReviews { get; set; } = 5;
}
