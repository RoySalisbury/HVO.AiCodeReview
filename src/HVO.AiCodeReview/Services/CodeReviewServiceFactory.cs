using AiCodeReview.Models;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Services;

/// <summary>
/// Factory that reads <see cref="AiProviderSettings"/> and constructs
/// the correct <see cref="ICodeReviewService"/> — either a single provider
/// or a <see cref="ConsensusReviewService"/> wrapping several.
///
/// Adding a new provider type:
///   1. Create a class implementing <see cref="ICodeReviewService"/>
///   2. Add a case to <see cref="CreateProvider"/> below
///   3. Add a config block under <c>AiProvider:Providers</c> in appsettings.json
/// </summary>
public static class CodeReviewServiceFactory
{
    /// <summary>
    /// Build and register the <see cref="ICodeReviewService"/> in the DI container.
    /// Call this from <c>Program.cs</c> instead of directly registering a service.
    /// </summary>
    public static IServiceCollection AddCodeReviewService(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind the new provider-agnostic settings
        services.Configure<AiProviderSettings>(
            configuration.GetSection(AiProviderSettings.SectionName));

        // Keep legacy AzureOpenAISettings binding for backward compatibility
        // (used by the legacy AzureOpenAiReviewService constructor)
        services.Configure<AzureOpenAISettings>(
            configuration.GetSection(AzureOpenAISettings.SectionName));

        services.AddSingleton<ICodeReviewService>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderSettings>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();

            // ── Fallback: if no AiProvider section exists, use legacy AzureOpenAI ──
            if (settings.Providers.Count == 0)
            {
                var legacySettings = sp.GetRequiredService<IOptions<AzureOpenAISettings>>().Value;
                var logger = loggerFactory.CreateLogger<AzureOpenAiReviewService>();

                logger.LogInformation(
                    "No AiProvider:Providers config found — falling back to legacy AzureOpenAI settings (model: {Model})",
                    legacySettings.DeploymentName);

                return new AzureOpenAiReviewService(
                    legacySettings.Endpoint,
                    legacySettings.ApiKey,
                    legacySettings.DeploymentName,
                    legacySettings.CustomInstructionsPath,
                    logger);
            }

            // Build all enabled providers
            var providers = settings.Providers
                .Where(kv => kv.Value.Enabled)
                .Select(kv => (
                    Name: kv.Value.DisplayName.Length > 0 ? kv.Value.DisplayName : kv.Key,
                    Service: CreateProvider(kv.Key, kv.Value, loggerFactory)))
                .ToList();

            if (providers.Count == 0)
                throw new InvalidOperationException(
                    "No enabled AI providers found in AiProvider:Providers configuration.");

            // Single-provider mode
            if (settings.Mode.Equals("single", StringComparison.OrdinalIgnoreCase))
            {
                // Look up from the already-created providers list by config key or display name
                var activeKey = settings.ActiveProvider;
                var providerKeys = settings.Providers
                    .Where(kv => kv.Value.Enabled)
                    .Select(kv => kv.Key)
                    .ToList();

                // Find the index of the matching provider in the enabled list
                var matchIndex = providerKeys.FindIndex(k =>
                    k.Equals(activeKey, StringComparison.OrdinalIgnoreCase));

                if (matchIndex < 0)
                {
                    // Try matching by display name
                    matchIndex = providers.FindIndex(p =>
                        p.Name.Equals(activeKey, StringComparison.OrdinalIgnoreCase));
                }

                if (matchIndex >= 0 && matchIndex < providers.Count)
                {
                    loggerFactory.CreateLogger("CodeReviewServiceFactory")
                        .LogInformation("Single-provider mode: using '{Provider}'", providers[matchIndex].Name);
                    return providers[matchIndex].Service;
                }

                // Fallback to first enabled provider
                loggerFactory.CreateLogger("CodeReviewServiceFactory")
                    .LogWarning("ActiveProvider '{Active}' not found — falling back to '{First}'",
                        settings.ActiveProvider, providers[0].Name);
                return providers[0].Service;
            }

            // Consensus mode
            var consensusLogger = loggerFactory.CreateLogger<ConsensusReviewService>();
            return new ConsensusReviewService(providers, settings.ConsensusThreshold, consensusLogger);
        });

        return services;
    }

    /// <summary>
    /// Construct a single provider instance from its configuration.
    /// Extend this method when adding new provider types.
    /// </summary>
    private static ICodeReviewService CreateProvider(
        string key, ProviderConfig config, ILoggerFactory loggerFactory)
    {
        var type = config.Type.ToLowerInvariant();

        return type switch
        {
            "azure-openai" => new AzureOpenAiReviewService(
                config.Endpoint,
                config.ApiKey,
                config.Model,
                config.CustomInstructionsPath,
                loggerFactory.CreateLogger<AzureOpenAiReviewService>()),

            // ── Add new provider types here ──────────────────────────────
            // "github-copilot" => new GitHubCopilotReviewService(config, loggerFactory.CreateLogger<GitHubCopilotReviewService>()),
            // "local" => new LocalAiReviewService(config.Endpoint, config.Model, loggerFactory.CreateLogger<LocalAiReviewService>()),
            // "openai" => new OpenAiReviewService(config.ApiKey, config.Model, loggerFactory.CreateLogger<OpenAiReviewService>()),

            _ => throw new InvalidOperationException(
                $"Unknown AI provider type '{config.Type}' for provider '{key}'. " +
                $"Supported types: azure-openai. " +
                $"To add a new type, implement ICodeReviewService and register it in CodeReviewServiceFactory.CreateProvider()."),
        };
    }
}
