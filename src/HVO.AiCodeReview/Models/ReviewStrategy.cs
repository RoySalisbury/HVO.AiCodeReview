namespace AiCodeReview.Models;

/// <summary>
/// Controls how Pass 2 (detailed file review) is executed.
/// </summary>
public enum ReviewStrategy
{
    /// <summary>
    /// Current per-file Chat Completions flow. Each file is reviewed individually.
    /// Default — backward compatible with existing behavior.
    /// </summary>
    FileByFile,

    /// <summary>
    /// Smart selection: ≤N files → FileByFile, >N files → Vector.
    /// N is configurable via <see cref="AssistantsSettings.AutoThreshold"/>.
    /// </summary>
    Auto,

    /// <summary>
    /// Always use the Assistants API + Vector Store for Pass 2.
    /// All changed files are uploaded to a Vector Store and reviewed in a single run.
    /// Best for medium-to-large PRs with cross-file dependencies.
    /// </summary>
    Vector,
}
