using System.Text.Json.Serialization;

namespace AiCodeReview.Models;

/// <summary>
/// Identifies which review pass is executing, enabling per-pass model routing.
/// Each pass can target a different AI provider/model for cost and quality optimization.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ReviewPass
{
    /// <summary>
    /// Pass 1 — PR-level summary / cross-file context generation.
    /// Synthesis task: a fast/cheap model (GPT-4o-mini, Claude Haiku) handles it fine.
    /// </summary>
    PrSummary,

    /// <summary>
    /// Pass 2 — Per-file inline code review (Chat or Vector strategy).
    /// Code analysis: needs precision (GPT-4o, Claude Sonnet).
    /// </summary>
    PerFileReview,

    /// <summary>
    /// Pass 3 — Deep holistic re-evaluation of all Pass 2 results.
    /// Holistic reasoning: use the best available model (o1, GPT-4o).
    /// </summary>
    DeepReview,

    /// <summary>
    /// Dedicated security-focused review pass.
    /// Can use a security-tuned or premium model.
    /// </summary>
    SecurityPass,

    /// <summary>
    /// Thread resolution verification — checks if prior AI findings were addressed.
    /// Simple yes/no check: cheapest model suffices (GPT-4o-mini).
    /// </summary>
    ThreadVerification,
}
