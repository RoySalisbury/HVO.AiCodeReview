namespace AiCodeReview.Models;

/// <summary>
/// Configuration for the Azure OpenAI Assistants API (Vector Store review strategy).
/// Bound from the "Assistants" configuration section.
/// </summary>
public class AssistantsSettings
{
    public const string SectionName = "Assistants";

    /// <summary>
    /// File count threshold for Auto strategy: PRs with ≤ this many files use FileByFile,
    /// PRs with more use Vector. Default: 5.
    /// </summary>
    public int AutoThreshold { get; set; } = 5;

    /// <summary>
    /// Milliseconds between poll attempts when waiting for Vector Store indexing
    /// or Assistant Run completion. Default: 1000 (1 second).
    /// </summary>
    public int PollIntervalMs { get; set; } = 1000;

    /// <summary>
    /// Maximum number of poll attempts before timing out. Default: 120.
    /// Total timeout = PollIntervalMs × MaxPollAttempts.
    /// </summary>
    public int MaxPollAttempts { get; set; } = 120;

    /// <summary>
    /// Azure OpenAI API version to use for Assistants/Files/Vector Store endpoints.
    /// Default: "2024-05-01-preview" (validated working).
    /// </summary>
    public string ApiVersion { get; set; } = "2024-05-01-preview";

    /// <summary>
    /// Maximum number of concurrent file uploads. Default: 10.
    /// </summary>
    public int MaxParallelUploads { get; set; } = 10;
}
