using AiCodeReview.Models;
using AiCodeReview.Services;
using HVO.Enterprise.Telemetry.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests.Helpers;

/// <summary>
/// Shared test infrastructure for building the DI container with either
/// a fake or real AI service, and optionally a fake DevOps backend.
/// Centralises configuration and supports per-test model overrides so
/// new models can be exercised without touching every test file.
///
/// Usage:
///   var ctx = TestServiceBuilder.BuildWithFakeAi();                  // fake AI + real DevOps
///   var ctx = TestServiceBuilder.BuildFullyFake();                    // fake AI + fake DevOps
///   var ctx = TestServiceBuilder.BuildWithRealAi();                   // real AI + real DevOps
///   var ctx = TestServiceBuilder.BuildWithRealAi("gpt-5");            // model override
/// </summary>
public static class TestServiceBuilder
{
    /// <summary>Loads appsettings.Test.json.</summary>
    public static IConfiguration LoadConfig() => new ConfigurationBuilder()
        .SetBasePath(AppContext.BaseDirectory)
        .AddJsonFile("appsettings.Test.json")
        .Build();

    /// <summary>
    /// Builds the DI container with a <see cref="FakeCodeReviewService"/>.
    /// This is the default for all tests that exercise orchestrator logic,
    /// dedup, metadata, history, etc. — anything that doesn't need real AI.
    /// </summary>
    public static TestContext BuildWithFakeAi(
        FakeCodeReviewService? fakeService = null,
        IConfiguration? config = null)
    {
        config ??= LoadConfig();
        var fake = fakeService ?? new FakeCodeReviewService();

        var devOpsSettings = config.GetSection("AzureDevOps").Get<AzureDevOpsSettings>()!;
        var project = config["TestSettings:Project"]!;

        var services = new ServiceCollection();
        services.AddLogging(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
        services.Configure<AzureDevOpsSettings>(config.GetSection("AzureDevOps"));
        services.Configure<AiProviderSettings>(config.GetSection("AiProvider"));
        services.Configure<AzureOpenAISettings>(config.GetSection("AzureOpenAI"));
        services.Configure<AssistantsSettings>(config.GetSection("Assistants"));
        services.Configure<SizeGuardrailsSettings>(config.GetSection(SizeGuardrailsSettings.SectionName));
        services.AddHttpClient<IDevOpsService, AzureDevOpsService>();
        services.AddHttpClient();
        services.AddSingleton<ICodeReviewService>(fake);
        services.AddSingleton<DepthModelResolver>(sp =>
            new DepthModelResolver(
                new Dictionary<ReviewDepth, ICodeReviewService>(),
                fake,
                sp.GetRequiredService<ILogger<DepthModelResolver>>()));
        services.AddSingleton<PassModelResolver>(sp =>
            new PassModelResolver(
                new Dictionary<ReviewPass, ICodeReviewService>(),
                sp.GetRequiredService<DepthModelResolver>(),
                sp.GetRequiredService<ILogger<PassModelResolver>>()));
        services.AddSingleton<ICodeReviewServiceResolver>(sp =>
            sp.GetRequiredService<PassModelResolver>());
        services.AddSingleton<ModelAdapterResolver>(sp =>
            new ModelAdapterResolver(sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelAdapterResolver>()));
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddSingleton<IGlobalRateLimitSignal, GlobalRateLimitSignal>();
        services.AddSingleton<ITelemetryService, NullTelemetryService>();
        services.AddScoped<VectorStoreReviewService>();
        services.AddTransient<CodeReviewOrchestrator>();

        var sp = services.BuildServiceProvider();

        return new TestContext(
            sp,
            sp.GetRequiredService<CodeReviewOrchestrator>(),
            sp.GetRequiredService<IDevOpsService>(),
            devOpsSettings,
            project,
            fake,
            usesRealAi: false);
    }

    /// <summary>
    /// Builds the DI container with BOTH a <see cref="FakeCodeReviewService"/>
    /// and a <see cref="FakeDevOpsService"/>.  No external services are called.
    /// Use for pure-logic tests: orchestration, dedup, metadata, history,
    /// summary generation, etc.
    /// </summary>
    public static TestContext BuildFullyFake(
        FakeCodeReviewService? fakeAi = null,
        FakeDevOpsService? fakeDevOps = null,
        IConfiguration? config = null)
    {
        config ??= LoadConfig();
        var ai = fakeAi ?? new FakeCodeReviewService();
        var devOps = fakeDevOps ?? new FakeDevOpsService();

        var devOpsSettings = config.GetSection("AzureDevOps").Get<AzureDevOpsSettings>()!;
        var project = config["TestSettings:Project"]!;

        var services = new ServiceCollection();
        services.AddLogging(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
        services.Configure<AzureDevOpsSettings>(config.GetSection("AzureDevOps"));
        services.Configure<AiProviderSettings>(config.GetSection("AiProvider"));
        services.Configure<AzureOpenAISettings>(config.GetSection("AzureOpenAI"));
        services.Configure<AssistantsSettings>(config.GetSection("Assistants"));
        services.Configure<SizeGuardrailsSettings>(config.GetSection(SizeGuardrailsSettings.SectionName));
        services.AddSingleton<IDevOpsService>(devOps);
        services.AddHttpClient();
        services.AddSingleton<ICodeReviewService>(ai);
        services.AddSingleton<DepthModelResolver>(sp =>
            new DepthModelResolver(
                new Dictionary<ReviewDepth, ICodeReviewService>(),
                ai,
                sp.GetRequiredService<ILogger<DepthModelResolver>>()));
        services.AddSingleton<PassModelResolver>(sp =>
            new PassModelResolver(
                new Dictionary<ReviewPass, ICodeReviewService>(),
                sp.GetRequiredService<DepthModelResolver>(),
                sp.GetRequiredService<ILogger<PassModelResolver>>()));
        services.AddSingleton<ICodeReviewServiceResolver>(sp =>
            sp.GetRequiredService<PassModelResolver>());
        services.AddSingleton<ModelAdapterResolver>(sp =>
            new ModelAdapterResolver(sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelAdapterResolver>()));
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddSingleton<IGlobalRateLimitSignal, GlobalRateLimitSignal>();
        services.AddSingleton<ITelemetryService, NullTelemetryService>();
        services.AddScoped<VectorStoreReviewService>();
        services.AddTransient<CodeReviewOrchestrator>();

        var sp = services.BuildServiceProvider();

        return new TestContext(
            sp,
            sp.GetRequiredService<CodeReviewOrchestrator>(),
            sp.GetRequiredService<IDevOpsService>(),
            devOpsSettings,
            project,
            ai,
            usesRealAi: false,
            fakeDevOps: devOps);
    }

    /// <summary>
    /// Builds the DI container with the REAL <see cref="AzureOpenAiReviewService"/>
    /// that calls Azure OpenAI.  Use only for tests that must validate AI
    /// behaviour (e.g. known-bad-code detection, model comparison).
    /// </summary>
    /// <param name="modelOverride">
    /// If provided, overrides the AI model in both the legacy <c>AzureOpenAI:DeploymentName</c>
    /// and the new <c>AiProvider:Providers:azure-openai:Model</c> settings so the test
    /// uses a different model deployment.  Pass <c>null</c> to use the default from
    /// appsettings.Test.json (currently <c>gpt-4o</c>).
    /// </param>
    public static TestContext BuildWithRealAi(
        string? modelOverride = null,
        IConfiguration? config = null)
    {
        config ??= LoadConfig();

        // Apply model override if requested
        if (!string.IsNullOrEmpty(modelOverride))
        {
            config = new ConfigurationBuilder()
                .AddConfiguration(config)
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["AzureOpenAI:DeploymentName"] = modelOverride,
                    ["AiProvider:Providers:azure-openai:Model"] = modelOverride,
                })
                .Build();
        }

        var devOpsSettings = config.GetSection("AzureDevOps").Get<AzureDevOpsSettings>()!;
        var project = config["TestSettings:Project"]!;

        var services = new ServiceCollection();
        services.AddLogging(b => { b.AddConsole(); b.SetMinimumLevel(LogLevel.Information); });
        services.Configure<AzureDevOpsSettings>(config.GetSection("AzureDevOps"));
        services.Configure<AiProviderSettings>(config.GetSection("AiProvider"));
        services.Configure<AzureOpenAISettings>(config.GetSection("AzureOpenAI"));
        services.Configure<AssistantsSettings>(config.GetSection("Assistants"));
        services.Configure<SizeGuardrailsSettings>(config.GetSection(SizeGuardrailsSettings.SectionName));
        services.AddHttpClient<IDevOpsService, AzureDevOpsService>();
        services.AddHttpClient();
        services.AddCodeReviewService(config);
        services.AddSingleton<ModelAdapterResolver>(sp =>
            new ModelAdapterResolver(sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelAdapterResolver>()));
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddSingleton<IGlobalRateLimitSignal, GlobalRateLimitSignal>();
        services.AddSingleton<ITelemetryService, NullTelemetryService>();
        services.AddScoped<VectorStoreReviewService>();
        services.AddTransient<CodeReviewOrchestrator>();

        var sp = services.BuildServiceProvider();

        var modelName = config["AiProvider:Providers:azure-openai:Model"]
                     ?? config["AzureOpenAI:DeploymentName"]
                     ?? "default";
        Console.WriteLine($"  [TestServiceBuilder] Using REAL AI: model={modelName}");

        return new TestContext(
            sp,
            sp.GetRequiredService<CodeReviewOrchestrator>(),
            sp.GetRequiredService<IDevOpsService>(),
            devOpsSettings,
            project,
            fakeService: null,
            usesRealAi: true);
    }
}

/// <summary>
/// Container for all the test dependencies.  Implements IAsyncDisposable
/// so <c>await using</c> disposes the ServiceProvider.
/// </summary>
public sealed class TestContext : IAsyncDisposable
{
    public ServiceProvider ServiceProvider { get; }
    public CodeReviewOrchestrator Orchestrator { get; }
    public IDevOpsService DevOps { get; }
    public AzureDevOpsSettings Settings { get; }
    public string Project { get; }

    /// <summary>The fake AI service, if using fake AI. Null when using real AI.</summary>
    public FakeCodeReviewService? FakeService { get; }

    /// <summary>The fake DevOps service, if using fake DevOps. Null when using real DevOps.</summary>
    public FakeDevOpsService? FakeDevOps { get; }

    /// <summary>True when the test is calling a real AI endpoint.</summary>
    public bool UsesRealAi { get; }

    public TestContext(
        ServiceProvider sp,
        CodeReviewOrchestrator orchestrator,
        IDevOpsService devOps,
        AzureDevOpsSettings settings,
        string project,
        FakeCodeReviewService? fakeService,
        bool usesRealAi,
        FakeDevOpsService? fakeDevOps = null)
    {
        ServiceProvider = sp;
        Orchestrator = orchestrator;
        DevOps = devOps;
        Settings = settings;
        Project = project;
        FakeService = fakeService;
        UsesRealAi = usesRealAi;
        FakeDevOps = fakeDevOps;
    }

    public ValueTask DisposeAsync() => ServiceProvider.DisposeAsync();
}
