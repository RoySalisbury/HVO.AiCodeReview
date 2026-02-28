using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Resolves the correct <see cref="ICodeReviewService"/> for a given review pass.
/// Enables per-pass model routing so each task (summary, per-file, deep, thread verification)
/// can target a different AI provider/deployment for cost, latency, and quality optimization.
/// </summary>
public interface ICodeReviewServiceResolver
{
    /// <summary>
    /// Returns the <see cref="ICodeReviewService"/> configured for the given pass and depth.
    /// Resolution order:
    /// <list type="number">
    /// <item><description>PassRouting mapping (if configured for this pass)</description></item>
    /// <item><description>DepthModels mapping (if configured for this depth)</description></item>
    /// <item><description>ActiveProvider (default)</description></item>
    /// </list>
    /// </summary>
    /// <param name="pass">Which review pass is executing.</param>
    /// <param name="depth">Current review depth (for DepthModels fallback).</param>
    ICodeReviewService GetService(ReviewPass pass, ReviewDepth depth = ReviewDepth.Standard);
}
