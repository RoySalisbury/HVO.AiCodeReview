using System.Text.Json;
using System.Text.RegularExpressions;
using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Loads model adapter definitions from <c>model-adapters.json</c> and resolves
/// the best-matching adapter for a given model/deployment name.
///
/// Adapters are evaluated in order; the first adapter whose
/// <see cref="ModelAdapter.ModelPattern"/> regex matches (case-insensitive) wins.
/// If no adapter matches, a built-in default is returned.
/// </summary>
public sealed class ModelAdapterResolver
{
    private readonly ILogger<ModelAdapterResolver> _logger;
    private readonly List<ModelAdapter> _adapters;

    /// <summary>
    /// Built-in fallback adapter used when no adapter file exists or no pattern matches.
    /// All numeric overrides are null so <see cref="ReviewProfile"/> defaults apply.
    /// </summary>
    private static readonly ModelAdapter DefaultAdapter = new()
    {
        Name = "built-in-default",
        ModelPattern = ".*",
        PromptStyle = "imperative",
        Preamble = null,
        Quirks = new List<string> { "No model-specific adapter configured — using defaults" },
    };

    public ModelAdapterResolver(ILogger<ModelAdapterResolver> logger, string? adapterPath = null)
    {
        _logger = logger;
        _adapters = LoadAdapters(ResolvePath(adapterPath));
    }

    /// <summary>
    /// All loaded adapters (for testing / inspection).
    /// </summary>
    internal IReadOnlyList<ModelAdapter> Adapters => _adapters;

    /// <summary>
    /// Resolves the best-matching adapter for the given model name.
    /// First match wins; falls back to <see cref="DefaultAdapter"/> if none match.
    /// </summary>
    public ModelAdapter Resolve(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            _logger.LogWarning("Model name is empty — using built-in default adapter");
            return DefaultAdapter;
        }

        foreach (var adapter in _adapters)
        {
            try
            {
                if (Regex.IsMatch(modelName, adapter.ModelPattern, RegexOptions.IgnoreCase))
                {
                    _logger.LogInformation(
                        "Model '{Model}' matched adapter '{Adapter}' (pattern: {Pattern}, style: {Style}, quirks: {QuirkCount})",
                        modelName, adapter.Name, adapter.ModelPattern, adapter.PromptStyle, adapter.Quirks.Count);

                    if (adapter.Quirks.Count > 0)
                    {
                        _logger.LogDebug(
                            "Adapter '{Adapter}' quirks: {Quirks}",
                            adapter.Name, string.Join("; ", adapter.Quirks));
                    }

                    return adapter;
                }
            }
            catch (RegexParseException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Invalid regex pattern '{Pattern}' in adapter '{Adapter}' — skipping",
                    adapter.ModelPattern, adapter.Name);
            }
        }

        _logger.LogWarning(
            "No adapter matched model '{Model}' — using built-in default",
            modelName);
        return DefaultAdapter;
    }

    /// <summary>
    /// Applies adapter overrides to a <see cref="ReviewProfile"/>, returning a new
    /// profile with adapter values merged. Null adapter fields leave the profile unchanged.
    /// </summary>
    public static ReviewProfile ApplyOverrides(ReviewProfile baseProfile, ModelAdapter adapter)
    {
        return new ReviewProfile
        {
            Temperature = adapter.Temperature ?? baseProfile.Temperature,
            MaxOutputTokensBatch = adapter.MaxOutputTokensBatch ?? baseProfile.MaxOutputTokensBatch,
            MaxOutputTokensSingleFile = adapter.MaxOutputTokensSingleFile ?? baseProfile.MaxOutputTokensSingleFile,
            MaxOutputTokensVerification = adapter.MaxOutputTokensVerification ?? baseProfile.MaxOutputTokensVerification,
            MaxOutputTokensPrSummary = adapter.MaxOutputTokensPrSummary ?? baseProfile.MaxOutputTokensPrSummary,
        };
    }

    /// <summary>
    /// Applies the adapter's <see cref="ModelAdapter.MaxInputLinesPerFile"/> override,
    /// falling back to the provided default when the adapter doesn't specify one.
    /// </summary>
    public static int GetEffectiveMaxInputLines(int currentMaxLines, ModelAdapter adapter)
    {
        return adapter.MaxInputLinesPerFile ?? currentMaxLines;
    }

    // ─── Loading ─────────────────────────────────────────────────────────

    private List<ModelAdapter> LoadAdapters(string? path)
    {
        if (path == null || !File.Exists(path))
        {
            _logger.LogInformation(
                "No model-adapters.json found — all models will use built-in defaults");
            return new List<ModelAdapter>();
        }

        try
        {
            var json = File.ReadAllText(path);
            var config = JsonSerializer.Deserialize<ModelAdapterConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            var adapters = config?.Adapters ?? new List<ModelAdapter>();
            _logger.LogInformation(
                "Loaded {Count} model adapters from '{Path}'",
                adapters.Count, path);

            foreach (var a in adapters)
            {
                _logger.LogDebug(
                    "  Adapter '{Name}': pattern={Pattern}, style={Style}, preamble={HasPreamble}, quirks={QuirkCount}",
                    a.Name, a.ModelPattern, a.PromptStyle,
                    !string.IsNullOrWhiteSpace(a.Preamble), a.Quirks.Count);
            }

            return adapters;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load model adapters from '{Path}' — using built-in defaults",
                path);
            return new List<ModelAdapter>();
        }
    }

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "model-adapters.json");
            return File.Exists(defaultPath) ? defaultPath : null;
        }

        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        return File.Exists(resolved) ? resolved : null;
    }
}
