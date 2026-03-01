using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for <see cref="PassModelResolver"/> — per-pass model routing.
/// Validates the resolution order: PassRouting → DepthModels → ActiveProvider,
/// fallback behavior when no routing is configured, and DI wiring via
/// <see cref="CodeReviewServiceFactory"/>.
/// </summary>
[TestClass]
public class PassModelResolverTests
{
    // ── Resolution order tests ──────────────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public void GetService_PassRoutingConfigured_ReturnsPassSpecificService()
    {
        // Arrange: configure PrSummary → mini, everything else → default
        var miniService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o-mini" };
        var defaultService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o" };

        var passServices = new Dictionary<ReviewPass, ICodeReviewService>
        {
            [ReviewPass.PrSummary] = miniService,
        };

        var depthResolver = BuildDepthResolver(defaultService);
        var resolver = BuildPassResolver(passServices, depthResolver);

        // Act
        var result = resolver.GetService(ReviewPass.PrSummary);

        // Assert: pass-specific wins
        Assert.AreSame(miniService, result);
        Assert.AreEqual("gpt-4o-mini", result.ModelName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetService_NoPassRouting_FallsBackToDepthModel()
    {
        // Arrange: no pass routing, but depth model configured for Standard
        var depthService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o" };
        var defaultService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o-mini" };

        var depthServices = new Dictionary<ReviewDepth, ICodeReviewService>
        {
            [ReviewDepth.Standard] = depthService,
        };
        var depthResolver = BuildDepthResolver(defaultService, depthServices);
        var resolver = BuildPassResolver(new Dictionary<ReviewPass, ICodeReviewService>(), depthResolver);

        // Act
        var result = resolver.GetService(ReviewPass.PerFileReview, ReviewDepth.Standard);

        // Assert: falls back to depth-based model
        Assert.AreSame(depthService, result);
        Assert.AreEqual("gpt-4o", result.ModelName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetService_NoPassOrDepthRouting_FallsBackToActiveProvider()
    {
        // Arrange: no pass routing, no depth model
        var defaultService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o" };
        var depthResolver = BuildDepthResolver(defaultService);
        var resolver = BuildPassResolver(new Dictionary<ReviewPass, ICodeReviewService>(), depthResolver);

        // Act
        var result = resolver.GetService(ReviewPass.PerFileReview, ReviewDepth.Standard);

        // Assert: falls back to default (active provider)
        Assert.AreSame(defaultService, result);
        Assert.AreEqual("gpt-4o", result.ModelName);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void GetService_PassRoutingOverridesDepthModel()
    {
        // Arrange: both pass routing and depth model configured; pass routing should win
        var passService = new FakeCodeReviewService { ModelNameOverride = "o1-preview" };
        var depthService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o" };
        var defaultService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o-mini" };

        var passServices = new Dictionary<ReviewPass, ICodeReviewService>
        {
            [ReviewPass.DeepReview] = passService,
        };
        var depthServices = new Dictionary<ReviewDepth, ICodeReviewService>
        {
            [ReviewDepth.Deep] = depthService,
        };
        var depthResolver = BuildDepthResolver(defaultService, depthServices);
        var resolver = BuildPassResolver(passServices, depthResolver);

        // Act
        var result = resolver.GetService(ReviewPass.DeepReview, ReviewDepth.Deep);

        // Assert: pass routing takes priority
        Assert.AreSame(passService, result);
        Assert.AreEqual("o1-preview", result.ModelName);
    }

    // ── Multi-pass routing ──────────────────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public void GetService_DifferentPassesUseDifferentModels()
    {
        // Arrange: each pass has a different model
        var summaryService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o-mini" };
        var perFileService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o" };
        var deepService = new FakeCodeReviewService { ModelNameOverride = "o1-preview" };
        var threadService = new FakeCodeReviewService { ModelNameOverride = "gpt-4o-mini" };
        var defaultService = new FakeCodeReviewService { ModelNameOverride = "default-model" };

        var passServices = new Dictionary<ReviewPass, ICodeReviewService>
        {
            [ReviewPass.PrSummary] = summaryService,
            [ReviewPass.PerFileReview] = perFileService,
            [ReviewPass.DeepReview] = deepService,
            [ReviewPass.ThreadVerification] = threadService,
        };

        var depthResolver = BuildDepthResolver(defaultService);
        var resolver = BuildPassResolver(passServices, depthResolver);

        // Act & Assert
        Assert.AreEqual("gpt-4o-mini", resolver.GetService(ReviewPass.PrSummary).ModelName);
        Assert.AreEqual("gpt-4o", resolver.GetService(ReviewPass.PerFileReview).ModelName);
        Assert.AreEqual("o1-preview", resolver.GetService(ReviewPass.DeepReview).ModelName);
        Assert.AreEqual("gpt-4o-mini", resolver.GetService(ReviewPass.ThreadVerification).ModelName);
        // SecurityPass has no explicit mapping here → falls back to default
        // (in production, appsettings.json routes SecurityPass → azure-openai-mini)
        Assert.AreEqual("default-model", resolver.GetService(ReviewPass.SecurityPass).ModelName);
    }

    // ── ConfiguredPasses / HasPassRouting ────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public void HasPassRouting_NoMappings_ReturnsFalse()
    {
        var defaultService = new FakeCodeReviewService();
        var depthResolver = BuildDepthResolver(defaultService);
        var resolver = BuildPassResolver(new Dictionary<ReviewPass, ICodeReviewService>(), depthResolver);

        Assert.IsFalse(resolver.HasPassRouting);
        Assert.AreEqual(0, resolver.ConfiguredPasses.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void HasPassRouting_WithMappings_ReturnsTrue()
    {
        var service = new FakeCodeReviewService { ModelNameOverride = "gpt-4o-mini" };
        var defaultService = new FakeCodeReviewService();
        var depthResolver = BuildDepthResolver(defaultService);
        var resolver = BuildPassResolver(
            new Dictionary<ReviewPass, ICodeReviewService> { [ReviewPass.PrSummary] = service },
            depthResolver);

        Assert.IsTrue(resolver.HasPassRouting);
        Assert.AreEqual(1, resolver.ConfiguredPasses.Count);
    }

    // ── DI wiring via CodeReviewServiceFactory ──────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public void DI_PassModelResolver_RegistersViaFactory()
    {
        // Arrange: use in-memory config with no PassRouting (backward compatible)
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:ActiveProvider"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Type"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:azure-openai:ApiKey"] = "fake-key",
                ["AiProvider:Providers:azure-openai:Model"] = "gpt-4o",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();

        // Act
        var resolver = sp.GetService<ICodeReviewServiceResolver>();
        var passResolver = sp.GetService<PassModelResolver>();

        // Assert
        Assert.IsNotNull(resolver, "ICodeReviewServiceResolver should be registered");
        Assert.IsNotNull(passResolver, "PassModelResolver should be registered");
        Assert.AreSame(resolver, passResolver, "ICodeReviewServiceResolver should resolve to PassModelResolver");
        Assert.IsFalse(passResolver.HasPassRouting, "No PassRouting configured → HasPassRouting should be false");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DI_PassRouting_RoutesToConfiguredProviders()
    {
        // Arrange: configure two providers with pass routing
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:ActiveProvider"] = "default-provider",
                ["AiProvider:Providers:default-provider:Type"] = "azure-openai",
                ["AiProvider:Providers:default-provider:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:default-provider:ApiKey"] = "fake-key",
                ["AiProvider:Providers:default-provider:Model"] = "gpt-4o",
                ["AiProvider:Providers:mini-provider:Type"] = "azure-openai",
                ["AiProvider:Providers:mini-provider:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:mini-provider:ApiKey"] = "fake-key",
                ["AiProvider:Providers:mini-provider:Model"] = "gpt-4o-mini",
                ["AiProvider:PassRouting:PrSummary"] = "mini-provider",
                ["AiProvider:PassRouting:ThreadVerification"] = "mini-provider",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<ICodeReviewServiceResolver>();

        // Act & Assert
        Assert.AreEqual("gpt-4o-mini", resolver.GetService(ReviewPass.PrSummary).ModelName,
            "PrSummary should route to mini-provider");
        Assert.AreEqual("gpt-4o-mini", resolver.GetService(ReviewPass.ThreadVerification).ModelName,
            "ThreadVerification should route to mini-provider");
        Assert.AreEqual("gpt-4o", resolver.GetService(ReviewPass.PerFileReview).ModelName,
            "PerFileReview has no pass routing → falls back to active provider (gpt-4o)");
        Assert.AreEqual("gpt-4o", resolver.GetService(ReviewPass.DeepReview).ModelName,
            "DeepReview has no pass routing → falls back to active provider (gpt-4o)");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void DI_SecurityPass_RoutesToMiniByDefault()
    {
        // Arrange: mirror production appsettings.json — SecurityPass → mini-provider
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:ActiveProvider"] = "default-provider",
                ["AiProvider:Providers:default-provider:Type"] = "azure-openai",
                ["AiProvider:Providers:default-provider:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:default-provider:ApiKey"] = "fake-key",
                ["AiProvider:Providers:default-provider:Model"] = "gpt-4o",
                ["AiProvider:Providers:mini-provider:Type"] = "azure-openai",
                ["AiProvider:Providers:mini-provider:Endpoint"] = "https://test.openai.azure.com/",
                ["AiProvider:Providers:mini-provider:ApiKey"] = "fake-key",
                ["AiProvider:Providers:mini-provider:Model"] = "gpt-4o-mini",
                ["AiProvider:PassRouting:SecurityPass"] = "mini-provider",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var resolver = sp.GetRequiredService<ICodeReviewServiceResolver>();

        // Act & Assert
        Assert.AreEqual("gpt-4o-mini", resolver.GetService(ReviewPass.SecurityPass).ModelName,
            "SecurityPass should route to gpt-4o-mini (cheapest model, equal quality per benchmark)");
        Assert.AreEqual("gpt-4o", resolver.GetService(ReviewPass.PerFileReview).ModelName,
            "Other passes should fall back to default provider");
    }

    // ── Orchestrator integration (verifies resolver is wired correctly) ───

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Orchestrator_ResolvesWithPassModelResolver()
    {
        // Verify that the orchestrator can be constructed with ICodeReviewServiceResolver
        // This validates that TestServiceBuilder correctly wires up the PassModelResolver
        await using var ctx = TestServiceBuilder.BuildWithFakeAi();
        Assert.IsNotNull(ctx.Orchestrator, "Orchestrator should resolve successfully with PassModelResolver");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static DepthModelResolver BuildDepthResolver(
        ICodeReviewService defaultService,
        Dictionary<ReviewDepth, ICodeReviewService>? depthServices = null)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        return new DepthModelResolver(
            depthServices ?? new Dictionary<ReviewDepth, ICodeReviewService>(),
            defaultService,
            loggerFactory.CreateLogger<DepthModelResolver>());
    }

    private static PassModelResolver BuildPassResolver(
        Dictionary<ReviewPass, ICodeReviewService> passServices,
        DepthModelResolver depthResolver)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        return new PassModelResolver(
            passServices,
            depthResolver,
            loggerFactory.CreateLogger<PassModelResolver>());
    }
}
