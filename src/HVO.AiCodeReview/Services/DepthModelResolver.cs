namespace AiCodeReview.Services;

using AiCodeReview.Models;

/// <summary>
/// Resolves the correct <see cref="ICodeReviewService"/> for a given
/// <see cref="ReviewDepth"/>. When depth-specific models are configured
/// (via <c>AiProvider:DepthModels</c>), each depth can target a different
/// AI deployment (e.g., gpt-4o-mini for Quick, gpt-4o for Standard, o1 for Deep).
/// Falls back to the default (active provider) service when no mapping exists.
/// </summary>
public sealed class DepthModelResolver
{
    private readonly Dictionary<ReviewDepth, ICodeReviewService> _services;
    private readonly ICodeReviewService _default;
    private readonly ILogger<DepthModelResolver> _logger;

    public DepthModelResolver(
        Dictionary<ReviewDepth, ICodeReviewService> services,
        ICodeReviewService defaultService,
        ILogger<DepthModelResolver> logger)
    {
        _services = services;
        _default = defaultService;
        _logger = logger;
    }

    /// <summary>
    /// Returns the <see cref="ICodeReviewService"/> configured for the given depth.
    /// Falls back to the default service when no depth-specific mapping exists.
    /// </summary>
    public ICodeReviewService Resolve(ReviewDepth depth)
    {
        if (_services.TryGetValue(depth, out var service))
        {
            _logger.LogDebug("Resolved depth-specific model for {Depth}", depth);
            return service;
        }

        _logger.LogDebug("No depth-specific model for {Depth} — using default", depth);
        return _default;
    }

    /// <summary>
    /// Returns all configured depth → model mappings (for diagnostics).
    /// </summary>
    public IReadOnlyDictionary<ReviewDepth, ICodeReviewService> ConfiguredDepths => _services;
}
