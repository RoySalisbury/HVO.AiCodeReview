namespace AiCodeReview.Models;

/// <summary>
/// Represents a single file changed in a pull request, with diff content
/// suitable for AI review.
/// </summary>
public class FileChange
{
    /// <summary>Repo-relative file path (e.g., "/src/MyService/Startup.cs").</summary>
    public string FilePath { get; set; } = string.Empty;

    /// <summary>Change type: edit, add, delete, rename.</summary>
    public string ChangeType { get; set; } = string.Empty;

    /// <summary>The file content in the target (base) branch. Null for new files.</summary>
    public string? OriginalContent { get; set; }

    /// <summary>The file content in the source (PR) branch. Null for deleted files.</summary>
    public string? ModifiedContent { get; set; }

    /// <summary>
    /// Unified diff between OriginalContent and ModifiedContent.
    /// Populated for edits only. Null for adds/deletes.
    /// </summary>
    public string? UnifiedDiff { get; set; }

    /// <summary>
    /// Line ranges in the MODIFIED file that were added or changed.
    /// For "add" files, this covers all lines. For "delete" files, this is empty.
    /// Used to filter inline comments to only target changed code.
    /// </summary>
    public List<(int Start, int End)> ChangedLineRanges { get; set; } = new();
}
