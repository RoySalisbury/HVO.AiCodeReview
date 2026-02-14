using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests.Helpers;

/// <summary>
/// Shared test infrastructure for building the DI container with either
/// a fake or real AI service.  Centralises configuration and supports
/// per-test model overrides so new models can be exercised without touching
/// every test file.
///
/// Usage:
///   var ctx = TestServiceBuilder.BuildWithFakeAi();                  // deterministic
///   var ctx = TestServiceBuilder.BuildWithRealAi();                   // live gpt-4o
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
        services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();
        services.AddSingleton<ICodeReviewService>(fake);
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddTransient<CodeReviewOrchestrator>();

        var sp = services.BuildServiceProvider();

        return new TestContext(
            sp,
            sp.GetRequiredService<CodeReviewOrchestrator>(),
            sp.GetRequiredService<IAzureDevOpsService>(),
            devOpsSettings,
            project,
            fake,
            usesRealAi: false);
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
        services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>();
        services.AddCodeReviewService(config);
        services.AddSingleton<IReviewRateLimiter, ReviewRateLimiter>();
        services.AddTransient<CodeReviewOrchestrator>();

        var sp = services.BuildServiceProvider();

        var modelName = config["AiProvider:Providers:azure-openai:Model"]
                     ?? config["AzureOpenAI:DeploymentName"]
                     ?? "default";
        Console.WriteLine($"  [TestServiceBuilder] Using REAL AI: model={modelName}");

        return new TestContext(
            sp,
            sp.GetRequiredService<CodeReviewOrchestrator>(),
            sp.GetRequiredService<IAzureDevOpsService>(),
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
    public IAzureDevOpsService DevOps { get; }
    public AzureDevOpsSettings Settings { get; }
    public string Project { get; }

    /// <summary>The fake service, if using fake AI. Null when using real AI.</summary>
    public FakeCodeReviewService? FakeService { get; }

    /// <summary>True when the test is calling a real AI endpoint.</summary>
    public bool UsesRealAi { get; }

    public TestContext(
        ServiceProvider sp,
        CodeReviewOrchestrator orchestrator,
        IAzureDevOpsService devOps,
        AzureDevOpsSettings settings,
        string project,
        FakeCodeReviewService? fakeService,
        bool usesRealAi)
    {
        ServiceProvider = sp;
        Orchestrator = orchestrator;
        DevOps = devOps;
        Settings = settings;
        Project = project;
        FakeService = fakeService;
        UsesRealAi = usesRealAi;
    }

    public ValueTask DisposeAsync() => ServiceProvider.DisposeAsync();
}
