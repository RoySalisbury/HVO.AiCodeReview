using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using AiCodeReview.Models;

namespace AiCodeReview.Services;

/// <summary>
/// Assembles AI review prompts from the layered rule catalog.
///
/// Layers (in order):
///   1. Identity preamble (shared, when scope opts in)
///   2. Custom instructions (per-provider, when scope opts in)
///   3. Scope preamble (context + JSON schema)
///   4. Numbered rules (filtered by scope, sorted by priority, enabled only)
///
/// Supports hot-reload: when the catalog file changes on disk, it is re-read
/// and the prompt cache is invalidated.
/// </summary>
public sealed class PromptAssemblyPipeline : IDisposable
{
    private readonly ILogger<PromptAssemblyPipeline> _logger;
    private readonly string? _catalogPath;
    private ReviewRuleCatalog? _catalog;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, string> _cache = new();
    private readonly object _reloadLock = new();

    /// <summary>
    /// Initializes the pipeline using a catalog file.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="catalogPath">
    /// Path to <c>review-rules.json</c>. If null or the file does not exist,
    /// the pipeline operates in fallback mode (returns null from AssemblePrompt).
    /// </param>
    public PromptAssemblyPipeline(ILogger<PromptAssemblyPipeline> logger, string? catalogPath = null)
    {
        _logger = logger;
        _catalogPath = ResolvePath(catalogPath);

        if (_catalogPath != null)
        {
            LoadCatalog();
            SetupWatcher();
        }
        else
        {
            _logger.LogInformation("No review-rules.json catalog found — prompt assembly will use hardcoded fallback");
        }
    }

    /// <summary>
    /// Whether a valid rule catalog is loaded.
    /// </summary>
    public bool HasCatalog => _catalog != null;

    /// <summary>
    /// The loaded catalog (for testing / inspection). Null when no catalog is loaded.
    /// </summary>
    internal ReviewRuleCatalog? Catalog => _catalog;

    /// <summary>
    /// Assembles the full system prompt for the given scope.
    /// Returns null if no catalog is loaded (caller should use hardcoded fallback).
    /// </summary>
    /// <param name="scope">Prompt scope: "batch", "single-file", "thread-verification", "pass-1".</param>
    /// <param name="customInstructions">Optional custom instructions text to inject.</param>
    /// <returns>Assembled prompt string, or null if no catalog.</returns>
    public string? AssemblePrompt(string scope, string? customInstructions = null)
    {
        if (_catalog == null)
            return null;

        var cacheKey = $"{scope}|{customInstructions?.GetHashCode() ?? 0}";
        return _cache.GetOrAdd(cacheKey, _ => BuildPrompt(scope, customInstructions));
    }

    /// <summary>
    /// Returns the list of rule IDs included in the assembled prompt for the given scope.
    /// Useful for diagnostics and testing.
    /// </summary>
    public List<string> GetActiveRuleIds(string scope)
    {
        if (_catalog == null)
            return new List<string>();

        return _catalog.Rules
            .Where(r => r.Scope == scope && r.Enabled)
            .OrderBy(r => r.Priority)
            .Select(r => r.Id)
            .ToList();
    }

    /// <summary>
    /// Returns all distinct categories present in the catalog.
    /// </summary>
    public List<string> GetCategories()
    {
        if (_catalog == null)
            return new List<string>();

        return _catalog.Rules
            .Select(r => r.Category)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
    }

    /// <summary>
    /// Returns all distinct scopes present in the catalog.
    /// </summary>
    public List<string> GetScopes()
    {
        if (_catalog == null)
            return new List<string>();

        return _catalog.Scopes.Keys.OrderBy(s => s).ToList();
    }

    // ─── Assembly ────────────────────────────────────────────────────────

    private string BuildPrompt(string scope, string? customInstructions)
    {
        if (_catalog == null)
            throw new InvalidOperationException("No catalog loaded");

        if (!_catalog.Scopes.TryGetValue(scope, out var scopeConfig))
            throw new ArgumentException($"Unknown prompt scope '{scope}'. Valid scopes: {string.Join(", ", _catalog.Scopes.Keys)}", nameof(scope));

        var sb = new StringBuilder();

        // Layer 1: Identity
        if (scopeConfig.IncludeIdentity && !string.IsNullOrWhiteSpace(_catalog.Identity))
        {
            sb.Append(_catalog.Identity);
        }

        // Layer 2: Custom instructions
        if (scopeConfig.IncludeCustomInstructions && !string.IsNullOrWhiteSpace(customInstructions))
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(customInstructions);
        }

        // Layer 3: Scope preamble (context + schema)
        if (!string.IsNullOrWhiteSpace(scopeConfig.Preamble))
        {
            if (sb.Length > 0)
            {
                sb.AppendLine();
                sb.AppendLine();
            }
            sb.Append(scopeConfig.Preamble);
        }

        // Layer 4: Numbered rules
        var rules = _catalog.Rules
            .Where(r => r.Scope == scope && r.Enabled)
            .OrderBy(r => r.Priority)
            .ToList();

        if (rules.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine();
            sb.AppendLine(scopeConfig.RulesHeader);

            for (int i = 0; i < rules.Count; i++)
            {
                sb.AppendLine($"{i + 1}. {rules[i].Text}");
            }
        }

        var ruleIds = rules.Select(r => r.Id).ToList();
        _logger.LogDebug(
            "Assembled prompt for scope '{Scope}': {RuleCount} rules [{RuleIds}], {Length} chars",
            scope, rules.Count, string.Join(", ", ruleIds), sb.Length);

        return sb.ToString();
    }

    // ─── Catalog loading ─────────────────────────────────────────────────

    private void LoadCatalog()
    {
        if (_catalogPath == null || !File.Exists(_catalogPath))
        {
            _catalog = null;
            return;
        }

        try
        {
            var json = File.ReadAllText(_catalogPath);
            _catalog = JsonSerializer.Deserialize<ReviewRuleCatalog>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            if (_catalog != null)
            {
                var ruleCount = _catalog.Rules.Count;
                var scopeCount = _catalog.Scopes.Count;
                var enabledCount = _catalog.Rules.Count(r => r.Enabled);

                _logger.LogInformation(
                    "Loaded review rule catalog v{Version}: {ScopeCount} scopes, {RuleCount} rules ({EnabledCount} enabled)",
                    _catalog.Version, scopeCount, ruleCount, enabledCount);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load review rule catalog from '{Path}' — using hardcoded fallback", _catalogPath);
            _catalog = null;
        }
    }

    // ─── Hot-reload via FileSystemWatcher ────────────────────────────────

    private void SetupWatcher()
    {
        if (_catalogPath == null)
            return;

        var dir = Path.GetDirectoryName(_catalogPath);
        var fileName = Path.GetFileName(_catalogPath);

        if (dir == null || fileName == null)
            return;

        try
        {
            _watcher = new FileSystemWatcher(dir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };

            _watcher.Changed += OnCatalogFileChanged;
            _logger.LogInformation("Watching for changes to '{Path}'", _catalogPath);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not set up FileSystemWatcher for '{Path}' — hot-reload disabled", _catalogPath);
        }
    }

    private void OnCatalogFileChanged(object sender, FileSystemEventArgs e)
    {
        // FileSystemWatcher can fire multiple events for a single change; debounce with a lock
        lock (_reloadLock)
        {
            try
            {
                // Small delay to let writes complete
                Thread.Sleep(100);

                _logger.LogInformation("Review rule catalog changed on disk — reloading");
                LoadCatalog();
                _cache.Clear();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error reloading review rule catalog after file change");
            }
        }
    }

    // ─── Helpers ─────────────────────────────────────────────────────────

    private static string? ResolvePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            // Default: look for review-rules.json next to the app
            var defaultPath = Path.Combine(AppContext.BaseDirectory, "review-rules.json");
            return File.Exists(defaultPath) ? defaultPath : null;
        }

        var resolved = Path.IsPathRooted(path)
            ? path
            : Path.Combine(AppContext.BaseDirectory, path);

        return File.Exists(resolved) ? resolved : null;
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
