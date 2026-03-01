namespace AiCodeReview.Models;

/// <summary>
/// Represents a linked Azure DevOps work item with fields relevant to code review.
/// </summary>
public class WorkItemInfo
{
    public int Id { get; set; }

    /// <summary>e.g. "User Story", "Bug", "Task"</summary>
    public string WorkItemType { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string State { get; set; } = string.Empty;

    /// <summary>HTML-stripped description of the work item.</summary>
    public string Description { get; set; } = string.Empty;

    /// <summary>HTML-stripped acceptance criteria (may be empty for non-story types).</summary>
    public string AcceptanceCriteria { get; set; } = string.Empty;

    /// <summary>Discussion comments on the work item (may contain AC modifications or decisions).</summary>
    public List<WorkItemComment> Comments { get; set; } = [];
}

/// <summary>
/// A single discussion comment from a work item.
/// </summary>
public class WorkItemComment
{
    public string Author { get; set; } = string.Empty;

    public DateTime CreatedDate { get; set; }

    /// <summary>HTML-stripped comment text.</summary>
    public string Text { get; set; } = string.Empty;
}
