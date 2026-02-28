namespace AiCodeReview.Models;

/// <summary>
/// Configuration for PR size guardrails and file prioritization.
/// Warns when a PR exceeds file or line thresholds and optionally
/// limits review to the highest-priority files (focus mode).
/// </summary>
public class SizeGuardrailsSettings
{
    public const string SectionName = "SizeGuardrails";

    /// <summary>
    /// Warn when a PR has more than this many reviewable files.
    /// Set to 0 to disable the file count warning.
    /// </summary>
    public int WarnFileCount { get; set; } = 30;

    /// <summary>
    /// Warn when total changed lines across all reviewable files exceeds this number.
    /// Changed lines = sum of lines in all ChangedLineRanges per file.
    /// Set to 0 to disable the changed-lines warning.
    /// </summary>
    public int WarnChangedLines { get; set; } = 2000;

    /// <summary>
    /// When focus mode is enabled and the PR exceeds <see cref="WarnFileCount"/>,
    /// only the top N highest-priority files are reviewed. The remaining files
    /// are listed as "deferred — focus mode" in the summary.
    /// Set to 0 to disable focus mode (all files are reviewed regardless of count).
    /// </summary>
    public int FocusModeMaxFiles { get; set; } = 20;

    /// <summary>
    /// Whether focus mode is enabled. When false, all files are reviewed even
    /// if the PR exceeds the warning thresholds; the warning is still displayed.
    /// </summary>
    public bool FocusModeEnabled { get; set; } = false;
}
