using System.Text.Json.Serialization;

namespace AiCodeReview.Models;

/// <summary>
/// Controls the depth of AI code review analysis.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewDepth
{
    /// <summary>
    /// Pass 1 only — PR-level summary + verdict. No per-file inline comments.
    /// Fast and cheap. Good for small PRs or triage.
    /// </summary>
    Quick,

    /// <summary>
    /// Pass 1 (PR summary) + Pass 2 (per-file inline comments).
    /// The default full review pipeline.
    /// </summary>
    Standard,

    /// <summary>
    /// Standard + Pass 3 holistic re-evaluation.
    /// Catches cross-file issues that per-file reviews missed,
    /// validates verdict consistency, and produces an executive summary.
    /// </summary>
    Deep,
}
