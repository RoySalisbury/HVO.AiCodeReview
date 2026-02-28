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

        // Bind review profile (temperature, tokens, thresholds)
        services.Configure<ReviewProfile>(
            configuration.GetSection(ReviewProfile.SectionName));

        // Keep legacy AzureOpenAISettings binding for backward compatibility
        // (used by the legacy AzureOpenAiReviewService constructor)
        services.Configure<AzureOpenAISettings>(
            configuration.GetSection(AzureOpenAISettings.SectionName));

        // Provider instance cache — ensures the same provider key reuses a single
        // ICodeReviewService instance across DepthModelResolver, PassModelResolver,
        // and the active-provider registration. Avoids duplicate clients, prompt
        // loading, and startup overhead.
        var providerCache = new Dictionary<string, ICodeReviewService>(StringComparer.OrdinalIgnoreCase);

        ICodeReviewService GetOrCreateProvider(
            string providerKey,
            ProviderConfig providerConfig,
            ILoggerFactory loggerFactory,
            int maxInputLinesPerFile,
            ReviewProfile reviewProfile,
            PromptAssemblyPipeline? pipeline,
            ModelAdapterResolver? adapterResolver,
            IGlobalRateLimitSignal? rateLimitSignal)
        {
            if (providerCache.TryGetValue(providerKey, out var cached))
                return cached;

            var service = CreateProvider(
                providerKey, providerConfig, loggerFactory,
                maxInputLinesPerFile, reviewProfile, pipeline, adapterResolver, rateLimitSignal);

            providerCache[providerKey] = service;
            return service;
        }

        // Register depth-model resolver (per-depth model selection)
        services.AddSingleton<DepthModelResolver>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderSettings>>().Value;
            var reviewProfile = sp.GetRequiredService<IOptions<ReviewProfile>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var pipeline = sp.GetService<PromptAssemblyPipeline>();
            var adapterResolver = sp.GetService<ModelAdapterResolver>();
            var rateLimitSignal = sp.GetService<IGlobalRateLimitSignal>();
            var defaultService = sp.GetRequiredService<ICodeReviewService>();
            var logger = loggerFactory.CreateLogger<DepthModelResolver>();

            var depthServices = new Dictionary<ReviewDepth, ICodeReviewService>();

            if (settings.DepthModels.Count > 0)
            {
                foreach (var (depthName, providerKey) in settings.DepthModels)
                {
                    if (!Enum.TryParse<ReviewDepth>(depthName, ignoreCase: true, out var depth))
                    {
                        logger.LogWarning(
                            "DepthModels: unknown depth '{Depth}' — skipping (valid: Quick, Standard, Deep)",
                            depthName);
                        continue;
                    }

                    if (!settings.Providers.TryGetValue(providerKey, out var providerConfig))
                    {
                        logger.LogWarning(
                            "DepthModels: provider '{Provider}' for depth {Depth} not found in Providers — using default",
                            providerKey, depth);
                        continue;
                    }

                    if (!providerConfig.Enabled)
                    {
                        logger.LogWarning(
                            "DepthModels: provider '{Provider}' for depth {Depth} is disabled — using default",
                            providerKey, depth);
                        continue;
                    }

                    var service = GetOrCreateProvider(
                        providerKey, providerConfig, loggerFactory,
                        settings.MaxInputLinesPerFile, reviewProfile, pipeline, adapterResolver, rateLimitSignal);

                    depthServices[depth] = service;

                    var displayName = providerConfig.DisplayName.Length > 0 ? providerConfig.DisplayName : providerKey;
                    logger.LogInformation(
                        "DepthModels: {Depth} → '{Provider}' (model: {Model})",
                        depth, displayName, providerConfig.Model);
                }
            }

            return new DepthModelResolver(depthServices, defaultService, logger);
        });

        // Register pass-model resolver (per-pass model routing)
        services.AddSingleton<PassModelResolver>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderSettings>>().Value;
            var reviewProfile = sp.GetRequiredService<IOptions<ReviewProfile>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var pipeline = sp.GetService<PromptAssemblyPipeline>();
            var adapterResolver = sp.GetService<ModelAdapterResolver>();
            var rateLimitSignal = sp.GetService<IGlobalRateLimitSignal>();
            var depthResolver = sp.GetRequiredService<DepthModelResolver>();
            var logger = loggerFactory.CreateLogger<PassModelResolver>();

            var passServices = new Dictionary<ReviewPass, ICodeReviewService>();

            if (settings.PassRouting.Count > 0)
            {
                foreach (var (passName, providerKey) in settings.PassRouting)
                {
                    if (!Enum.TryParse<ReviewPass>(passName, ignoreCase: true, out var pass))
                    {
                        logger.LogWarning(
                            "PassRouting: unknown pass '{Pass}' — skipping (valid: {Valid})",
                            passName, string.Join(", ", Enum.GetNames<ReviewPass>()));
                        continue;
                    }

                    if (!settings.Providers.TryGetValue(providerKey, out var providerConfig))
                    {
                        logger.LogWarning(
                            "PassRouting: provider '{Provider}' for pass {Pass} not found in Providers — using fallback",
                            providerKey, pass);
                        continue;
                    }

                    if (!providerConfig.Enabled)
                    {
                        logger.LogWarning(
                            "PassRouting: provider '{Provider}' for pass {Pass} is disabled — using fallback",
                            providerKey, pass);
                        continue;
                    }

                    var service = GetOrCreateProvider(
                        providerKey, providerConfig, loggerFactory,
                        settings.MaxInputLinesPerFile, reviewProfile, pipeline, adapterResolver, rateLimitSignal);

                    passServices[pass] = service;

                    var displayName = providerConfig.DisplayName.Length > 0 ? providerConfig.DisplayName : providerKey;
                    logger.LogInformation(
                        "PassRouting: {Pass} → '{Provider}' (model: {Model})",
                        pass, displayName, providerConfig.Model);
                }
            }

            return new PassModelResolver(passServices, depthResolver, logger);
        });
        services.AddSingleton<ICodeReviewServiceResolver>(sp => sp.GetRequiredService<PassModelResolver>());

        services.AddSingleton<ICodeReviewService>(sp =>
        {
            var settings = sp.GetRequiredService<IOptions<AiProviderSettings>>().Value;
            var reviewProfile = sp.GetRequiredService<IOptions<ReviewProfile>>().Value;
            var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
            var pipeline = sp.GetService<PromptAssemblyPipeline>();
            var adapterResolver = sp.GetService<ModelAdapterResolver>();
            var rateLimitSignal = sp.GetService<IGlobalRateLimitSignal>();

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
                    logger,
                    maxInputLinesPerFile: ValidateMaxInputLines(settings.MaxInputLinesPerFile, "global (legacy fallback)"),
                    reviewProfile: reviewProfile,
                    pipeline: pipeline,
                    modelAdapter: adapterResolver?.Resolve(legacySettings.DeploymentName),
                    rateLimitSignal: rateLimitSignal);
            }

            // Build all enabled providers
            var providers = settings.Providers
                .Where(kv => kv.Value.Enabled)
                .Select(kv => (
                    Name: kv.Value.DisplayName.Length > 0 ? kv.Value.DisplayName : kv.Key,
                    Service: CreateProvider(kv.Key, kv.Value, loggerFactory, settings.MaxInputLinesPerFile, reviewProfile, pipeline, adapterResolver, rateLimitSignal)))
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
        string key, ProviderConfig config, ILoggerFactory loggerFactory, int globalMaxInputLines,
        ReviewProfile reviewProfile, PromptAssemblyPipeline? pipeline = null,
        ModelAdapterResolver? adapterResolver = null,
        IGlobalRateLimitSignal? rateLimitSignal = null)
    {
        var type = config.Type.ToLowerInvariant();
        var maxLines = ValidateMaxInputLines(
            config.MaxInputLinesPerFile ?? globalMaxInputLines, key);
        var adapter = adapterResolver?.Resolve(config.Model);

        return type switch
        {
            "azure-openai" => new AzureOpenAiReviewService(
                config.Endpoint,
                config.ApiKey,
                config.Model,
                config.CustomInstructionsPath,
                loggerFactory.CreateLogger<AzureOpenAiReviewService>(),
                maxInputLinesPerFile: maxLines,
                reviewProfile: reviewProfile,
                pipeline: pipeline,
                modelAdapter: adapter,
                rateLimitSignal: rateLimitSignal),

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

    /// <summary>
    /// Validate that the effective MaxInputLinesPerFile is greater than zero.
    /// Shared by both the legacy fallback path and <see cref="CreateProvider"/>.
    /// </summary>
    private static int ValidateMaxInputLines(int value, string context)
    {
        if (value <= 0)
            throw new InvalidOperationException(
                $"Invalid MaxInputLinesPerFile configuration for '{context}'. " +
                $"The effective value must be greater than 0, but was {value}.");
        return value;
    }
}
