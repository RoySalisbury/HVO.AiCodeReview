namespace AiCodeReview.Models;

/// <summary>
/// Progress event emitted by the orchestrator at each step.
/// In V1, these are logged server-side.
/// In V2+, they can be streamed to the client via SSE.
/// </summary>
public class ReviewStatusUpdate
{
    public ReviewStep Step { get; set; }
    public string Message { get; set; } = string.Empty;
    public int PercentComplete { get; set; }
}

public enum ReviewStep
{
    CheckingReviewStatus,
    FetchingPullRequest,
    RetrievingChanges,
    AnalyzingCode,
    PostingInlineComments,
    PostingSummary,
    SubmittingVote,
    Complete
}
