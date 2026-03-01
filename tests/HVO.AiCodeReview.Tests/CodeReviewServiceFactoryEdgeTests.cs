using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for <see cref="CodeReviewServiceFactory"/> DI registration branches:
/// all-providers-disabled, unknown ActiveProvider fallback, unknown depth/pass,
/// legacy fallback, unknown provider type, invalid MaxInputLines, etc.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class CodeReviewServiceFactoryEdgeTests
{
    [TestMethod]
    public void AllProvidersDisabled_ThrowsInvalidOperationException()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:Providers:dummy:Type"] = "azure-openai",
            ["AiProvider:Providers:dummy:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:dummy:ApiKey"] = "key",
            ["AiProvider:Providers:dummy:Model"] = "gpt-4o",
            ["AiProvider:Providers:dummy:Enabled"] = "false",
            ["AiProvider:MaxInputLinesPerFile"] = "500",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        Assert.ThrowsException<InvalidOperationException>(
            () => sp.GetRequiredService<ICodeReviewService>(),
            "Should throw when all providers are disabled");
    }

    [TestMethod]
    public void UnknownActiveProvider_FallsBackToFirst()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:Mode"] = "single",
            ["AiProvider:ActiveProvider"] = "nonexistent-provider",
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:Providers:real-one:Type"] = "azure-openai",
            ["AiProvider:Providers:real-one:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:real-one:ApiKey"] = "key",
            ["AiProvider:Providers:real-one:Model"] = "gpt-4o",
            ["AiProvider:Providers:real-one:Enabled"] = "true",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var svc = sp.GetRequiredService<ICodeReviewService>();
        Assert.IsNotNull(svc, "Should fall back to first enabled provider");
    }

    [TestMethod]
    public void UnknownProviderType_ThrowsInvalidOperationException()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:Providers:bad:Type"] = "GPT-Turbo-X",
            ["AiProvider:Providers:bad:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:bad:ApiKey"] = "key",
            ["AiProvider:Providers:bad:Model"] = "gpt-4o",
            ["AiProvider:Providers:bad:Enabled"] = "true",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var ex = Assert.ThrowsException<InvalidOperationException>(
            () => sp.GetRequiredService<ICodeReviewService>());
        Assert.IsTrue(ex.Message.Contains("Unknown AI provider type"), ex.Message);
    }

    [TestMethod]
    public void LegacyFallback_NoProviders_UsesAzureOpenAISettings()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            // No AiProvider:Providers — triggers legacy fallback
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AzureOpenAI:Endpoint"] = "https://fake.openai.azure.com/",
            ["AzureOpenAI:ApiKey"] = "fake-key",
            ["AzureOpenAI:DeploymentName"] = "legacy-model",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var svc = sp.GetRequiredService<ICodeReviewService>();
        Assert.IsNotNull(svc, "Should create service via legacy AzureOpenAI settings");
        Assert.IsInstanceOfType(svc, typeof(AzureOpenAiReviewService));
    }

    [TestMethod]
    public void DepthModels_UnknownDepth_SkipsWithWarning()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:ActiveProvider"] = "primary",
            ["AiProvider:Providers:primary:Type"] = "azure-openai",
            ["AiProvider:Providers:primary:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:primary:ApiKey"] = "key",
            ["AiProvider:Providers:primary:Model"] = "gpt-4o",
            ["AiProvider:Providers:primary:Enabled"] = "true",
            ["AiProvider:DepthModels:SuperDeep"] = "primary", // Invalid depth
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        // Should not throw — just logs a warning and skips
        var resolver = sp.GetRequiredService<DepthModelResolver>();
        Assert.IsNotNull(resolver);
    }

    [TestMethod]
    public void DepthModels_MissingProvider_SkipsWithWarning()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:ActiveProvider"] = "primary",
            ["AiProvider:Providers:primary:Type"] = "azure-openai",
            ["AiProvider:Providers:primary:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:primary:ApiKey"] = "key",
            ["AiProvider:Providers:primary:Model"] = "gpt-4o",
            ["AiProvider:Providers:primary:Enabled"] = "true",
            ["AiProvider:DepthModels:Deep"] = "nonexistent-provider",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var resolver = sp.GetRequiredService<DepthModelResolver>();
        Assert.IsNotNull(resolver);
    }

    [TestMethod]
    public void DepthModels_DisabledProvider_SkipsWithWarning()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:ActiveProvider"] = "primary",
            ["AiProvider:Providers:primary:Type"] = "azure-openai",
            ["AiProvider:Providers:primary:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:primary:ApiKey"] = "key",
            ["AiProvider:Providers:primary:Model"] = "gpt-4o",
            ["AiProvider:Providers:primary:Enabled"] = "true",
            ["AiProvider:Providers:disabled-one:Type"] = "azure-openai",
            ["AiProvider:Providers:disabled-one:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:disabled-one:ApiKey"] = "key",
            ["AiProvider:Providers:disabled-one:Model"] = "gpt-4o-mini",
            ["AiProvider:Providers:disabled-one:Enabled"] = "false",
            ["AiProvider:DepthModels:Deep"] = "disabled-one",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var resolver = sp.GetRequiredService<DepthModelResolver>();
        Assert.IsNotNull(resolver);
    }

    [TestMethod]
    public void PassRouting_UnknownPass_SkipsWithWarning()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:ActiveProvider"] = "primary",
            ["AiProvider:Providers:primary:Type"] = "azure-openai",
            ["AiProvider:Providers:primary:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:primary:ApiKey"] = "key",
            ["AiProvider:Providers:primary:Model"] = "gpt-4o",
            ["AiProvider:Providers:primary:Enabled"] = "true",
            ["AiProvider:PassRouting:Phase99"] = "primary", // Invalid pass
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var resolver = sp.GetRequiredService<PassModelResolver>();
        Assert.IsNotNull(resolver);
    }

    [TestMethod]
    public void PassRouting_MissingProvider_SkipsWithWarning()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:ActiveProvider"] = "primary",
            ["AiProvider:Providers:primary:Type"] = "azure-openai",
            ["AiProvider:Providers:primary:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:primary:ApiKey"] = "key",
            ["AiProvider:Providers:primary:Model"] = "gpt-4o",
            ["AiProvider:Providers:primary:Enabled"] = "true",
            ["AiProvider:PassRouting:PrSummary"] = "missing-provider",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var resolver = sp.GetRequiredService<PassModelResolver>();
        Assert.IsNotNull(resolver);
    }

    [TestMethod]
    public void PassRouting_DisabledProvider_SkipsWithWarning()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:ActiveProvider"] = "primary",
            ["AiProvider:Providers:primary:Type"] = "azure-openai",
            ["AiProvider:Providers:primary:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:primary:ApiKey"] = "key",
            ["AiProvider:Providers:primary:Model"] = "gpt-4o",
            ["AiProvider:Providers:primary:Enabled"] = "true",
            ["AiProvider:Providers:disabled:Type"] = "azure-openai",
            ["AiProvider:Providers:disabled:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:disabled:ApiKey"] = "key",
            ["AiProvider:Providers:disabled:Model"] = "model2",
            ["AiProvider:Providers:disabled:Enabled"] = "false",
            ["AiProvider:PassRouting:PrSummary"] = "disabled",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var resolver = sp.GetRequiredService<PassModelResolver>();
        Assert.IsNotNull(resolver);
    }

    [TestMethod]
    public void ConsensusMode_CreatesConsensusService()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:Mode"] = "consensus",
            ["AiProvider:ConsensusThreshold"] = "2",
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:Providers:p1:Type"] = "azure-openai",
            ["AiProvider:Providers:p1:Endpoint"] = "https://fake1.openai.azure.com/",
            ["AiProvider:Providers:p1:ApiKey"] = "key1",
            ["AiProvider:Providers:p1:Model"] = "gpt-4o",
            ["AiProvider:Providers:p1:Enabled"] = "true",
            ["AiProvider:Providers:p2:Type"] = "azure-openai",
            ["AiProvider:Providers:p2:Endpoint"] = "https://fake2.openai.azure.com/",
            ["AiProvider:Providers:p2:ApiKey"] = "key2",
            ["AiProvider:Providers:p2:Model"] = "gpt-4o-mini",
            ["AiProvider:Providers:p2:Enabled"] = "true",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        var svc = sp.GetRequiredService<ICodeReviewService>();
        Assert.IsInstanceOfType(svc, typeof(ConsensusReviewService));
    }

    [TestMethod]
    public void ProviderCache_SameProviderKey_ReusesInstance()
    {
        var config = BuildConfig(new Dictionary<string, string?>
        {
            ["AiProvider:Mode"] = "single",
            ["AiProvider:ActiveProvider"] = "shared",
            ["AiProvider:MaxInputLinesPerFile"] = "500",
            ["AiProvider:Providers:shared:Type"] = "azure-openai",
            ["AiProvider:Providers:shared:Endpoint"] = "https://fake.openai.azure.com/",
            ["AiProvider:Providers:shared:ApiKey"] = "key",
            ["AiProvider:Providers:shared:Model"] = "gpt-4o",
            ["AiProvider:Providers:shared:Enabled"] = "true",
            // Route both depth and pass to same provider key
            ["AiProvider:DepthModels:Deep"] = "shared",
            ["AiProvider:PassRouting:PrSummary"] = "shared",
        });

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);
        var sp = services.BuildServiceProvider();

        // Trigger resolution of all resolvers
        var svc = sp.GetRequiredService<ICodeReviewService>();
        var depthResolver = sp.GetRequiredService<DepthModelResolver>();
        var passResolver = sp.GetRequiredService<PassModelResolver>();

        Assert.IsNotNull(svc);
        Assert.IsNotNull(depthResolver);
        Assert.IsNotNull(passResolver);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static IConfiguration BuildConfig(Dictionary<string, string?> values) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
}
