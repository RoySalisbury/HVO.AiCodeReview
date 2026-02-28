using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Resolves the correct <see cref="ICodeReviewService"/> for a given
/// <see cref="ReviewPass"/> and <see cref="ReviewDepth"/>.
/// When per-pass routing is configured (via <c>AiProvider:PassRouting</c>),
/// each review pass can target a different AI deployment — cheaper/faster
/// models for simple tasks, premium models for deep analysis — and separate
/// deployments avoid rate-limit contention.
///
/// Resolution order:
/// <list type="number">
/// <item><description><c>PassRouting[pass]</c> → provider (if mapped)</description></item>
/// <item><description><c>DepthModels[depth]</c> → provider (if mapped)</description></item>
/// <item><description><c>ActiveProvider</c> → provider (default)</description></item>
/// </list>
/// </summary>
public sealed class PassModelResolver : ICodeReviewServiceResolver
{
    private readonly Dictionary<ReviewPass, ICodeReviewService> _passServices;
    private readonly DepthModelResolver _depthResolver;
    private readonly ILogger<PassModelResolver> _logger;

    public PassModelResolver(
        Dictionary<ReviewPass, ICodeReviewService> passServices,
        DepthModelResolver depthResolver,
        ILogger<PassModelResolver> logger)
    {
        _passServices = passServices;
        _depthResolver = depthResolver;
        _logger = logger;
    }

    /// <inheritdoc />
    public ICodeReviewService GetService(ReviewPass pass, ReviewDepth depth = ReviewDepth.Standard)
    {
        // 1. Pass-specific routing takes priority
        if (_passServices.TryGetValue(pass, out var passService))
        {
            _logger.LogInformation(
                "PassRouting: {Pass} → '{Model}' (pass-specific)",
                pass, passService.ModelName);
            return passService;
        }

        // 2. Fall back to depth-based resolution (which itself falls back to ActiveProvider)
        var depthService = _depthResolver.Resolve(depth);
        _logger.LogDebug(
            "PassRouting: {Pass} → '{Model}' (depth fallback: {Depth})",
            pass, depthService.ModelName, depth);
        return depthService;
    }

    /// <summary>
    /// Returns all configured pass → model mappings (for diagnostics and logging).
    /// </summary>
    public IReadOnlyDictionary<ReviewPass, ICodeReviewService> ConfiguredPasses => _passServices;

    /// <summary>
    /// Returns true when at least one pass has an explicit routing configured.
    /// </summary>
    public bool HasPassRouting => _passServices.Count > 0;
}
