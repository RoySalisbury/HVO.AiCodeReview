using System.Collections.Concurrent;
using System.Text.Json;
using AiCodeReview.Models;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AiCodeReview.Services;

/// <summary>
/// Fetches and parses <c>.ai-review.yaml</c> or <c>.ai-review.json</c> from the
/// reviewed repository's root (source branch).  Caches per project/repo to avoid
/// repeated API calls within the service lifetime.
/// Returns <c>null</c> when no config file exists (graceful degradation).
/// </summary>
public class ArchitectureContextProvider
{
    private readonly IDevOpsService _devOpsService;
    private readonly ILogger<ArchitectureContextProvider> _logger;

    /// <summary>Cache keyed by "project/repo" — stores the parsed context (or null).</summary>
    private readonly ConcurrentDictionary<string, ArchitectureContext?> _cache = new();

    private static readonly string[] ConfigFileNames = [".ai-review.yaml", ".ai-review.json"];

    public ArchitectureContextProvider(
        IDevOpsService devOpsService,
        ILogger<ArchitectureContextProvider> logger)
    {
        _devOpsService = devOpsService;
        _logger = logger;
    }

    /// <summary>
    /// Retrieves the architecture context for the given repository.
    /// Tries <c>.ai-review.yaml</c> first, falls back to <c>.ai-review.json</c>.
    /// Returns <c>null</c> if neither file exists or parsing fails.
    /// </summary>
    /// <param name="project">Azure DevOps project name.</param>
    /// <param name="repository">Repository name.</param>
    /// <param name="sourceCommitOrBranch">
    /// Commit SHA or branch name to fetch from (typically the PR source commit).
    /// </param>
    public async Task<ArchitectureContext?> GetContextAsync(
        string project, string repository, string? sourceCommitOrBranch)
    {
        var cacheKey = $"{project}/{repository}";

        if (_cache.TryGetValue(cacheKey, out var cached))
        {
            _logger.LogDebug("Architecture context cache hit for {Key}", cacheKey);
            return cached;
        }

        var context = await FetchAndParseAsync(project, repository, sourceCommitOrBranch);
        _cache.TryAdd(cacheKey, context);
        return context;
    }

    private async Task<ArchitectureContext?> FetchAndParseAsync(
        string project, string repository, string? sourceCommitOrBranch)
    {
        foreach (var fileName in ConfigFileNames)
        {
            try
            {
                var content = await _devOpsService.GetRepositoryFileContentAsync(
                    project, repository, fileName, sourceCommitOrBranch);

                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var context = fileName.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase)
                    ? ParseYaml(content)
                    : ParseJson(content);

                if (context != null)
                {
                    _logger.LogInformation(
                        "Loaded architecture context from {File} in {Project}/{Repo}: " +
                        "architecture={Architecture}, techStack=[{TechStack}], " +
                        "structure={StructureCount} mappings, focusPaths={FocusCount}",
                        fileName, project, repository,
                        context.Architecture ?? "(not set)",
                        string.Join(", ", context.TechStack),
                        context.Structure.Count,
                        context.FocusPaths.Count);

                    return context;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Failed to parse {File} from {Project}/{Repo} — skipping architecture context",
                    fileName, project, repository);
            }
        }

        _logger.LogDebug("No .ai-review.yaml or .ai-review.json found in {Project}/{Repo}", project, repository);
        return null;
    }

    internal static ArchitectureContext? ParseYaml(string content)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        var context = deserializer.Deserialize<ArchitectureContext>(content);
        return context;
    }

    internal static ArchitectureContext? ParseJson(string content)
    {
        var context = JsonSerializer.Deserialize<ArchitectureContext>(content, JsonSerializerOptions.Web);
        return context;
    }
}
