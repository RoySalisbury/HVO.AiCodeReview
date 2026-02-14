namespace AiCodeReview.Models;

/// <summary>
/// Configuration for Azure DevOps API access. Organization-level only;
/// project and repository are passed per-request.
/// </summary>
public class AzureDevOpsSettings
{
    public const string SectionName = "AzureDevOps";

    /// <summary>Azure DevOps organization name (from URL: https://dev.azure.com/{Organization}).</summary>
    public string Organization { get; set; } = string.Empty;

    /// <summary>Personal Access Token with Code (Read) and Pull Request Threads (Read &amp; Write) permissions.</summary>
    public string PersonalAccessToken { get; set; } = string.Empty;

    /// <summary>
    /// The identity GUID of the service account that owns the PAT.
    /// Used when adding self as a reviewer on PRs.
    /// If left empty, the service will auto-discover it from the PAT
    /// via GET https://dev.azure.com/{org}/_apis/connectionData at startup.
    /// </summary>
    public string ServiceAccountIdentityId { get; set; } = string.Empty;

    /// <summary>
    /// Label applied to a PR after an AI review is completed.
    /// Purely decorative — used for visual filtering in the PR list.
    /// All review state decisions use PR properties (metadata), not tags.
    /// </summary>
    public string ReviewTagName { get; set; } = "ai-code-review";

    /// <summary>
    /// When true, the service adds itself as a reviewer with a vote on non-draft PRs.
    /// When false, only the tag and comments are posted (no reviewer vote).
    /// </summary>
    public bool AddReviewerVote { get; set; } = true;

    /// <summary>
    /// Minimum number of minutes between review attempts on the same PR.
    /// If a review is requested within this window, the request is rejected
    /// immediately without making any API calls. Set to 0 to disable.
    /// </summary>
    public int MinReviewIntervalMinutes { get; set; } = 5;

    /// <summary>
    /// Attribution tag appended to every inline comment posted by the AI reviewer.
    /// Set to empty string to disable. Uses markdown italic formatting.
    /// Example: "AiCodeReview" produces comments ending with
    /// <c>_[AiCodeReview]_</c>.
    /// </summary>
    public string CommentAttributionTag { get; set; } = "AiCodeReview";

    /// <summary>
    /// When true, on re-reviews the service will check existing AI-generated
    /// inline comment threads and resolve (mark as Fixed) any whose underlying
    /// issue has been addressed in the current code. Only threads tagged with
    /// <see cref="CommentAttributionTag"/> are considered; human reviewer
    /// comments are left untouched.
    /// </summary>
    public bool ResolveFixedThreadsOnReReview { get; set; } = true;
}
