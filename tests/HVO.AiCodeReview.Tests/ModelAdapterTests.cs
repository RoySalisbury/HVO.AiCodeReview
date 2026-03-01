using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Issue #7 — Per-Model Adapter Configuration.
/// Covers: adapter loading, pattern matching, parameter overrides,
/// prompt pipeline integration, fallback behavior, and shipped profiles.
/// </summary>
[TestCategory("Unit")]
[TestCategory("Unit")]
[TestClass]
public class ModelAdapterTests
{
    private static readonly ILogger<ModelAdapterResolver> ResolverLogger
        = NullLoggerFactory.Instance.CreateLogger<ModelAdapterResolver>();

    private static readonly ILogger<PromptAssemblyPipeline> PipelineLogger
        = NullLoggerFactory.Instance.CreateLogger<PromptAssemblyPipeline>();

    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "ModelAdapterTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best-effort */ }
    }

    private string WriteAdapters(ModelAdapterConfig config)
    {
        var path = Path.Combine(_tempDir, "model-adapters.json");
        var json = JsonSerializer.Serialize(config, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }

    private static ModelAdapterConfig CreateTestConfig()
    {
        return new ModelAdapterConfig
        {
            Adapters = new List<ModelAdapter>
            {
                new()
                {
                    Name = "gpt-4o",
                    ModelPattern = "gpt-4o",
                    Temperature = 0.1f,
                    MaxOutputTokensBatch = 16000,
                    MaxOutputTokensSingleFile = 4000,
                    MaxOutputTokensVerification = 2000,
                    MaxOutputTokensPrSummary = 4000,
                    MaxInputLinesPerFile = 5000,
                    PromptStyle = "imperative",
                    Preamble = "GPT-4o specific: respond ONLY with valid JSON.",
                    Quirks = new List<string> { "May produce trailing text" },
                },
                new()
                {
                    Name = "gpt-4",
                    ModelPattern = "^gpt-4(?!o)",
                    Temperature = 0.05f,
                    MaxOutputTokensBatch = 7000,
                    MaxOutputTokensSingleFile = 2000,
                    MaxOutputTokensVerification = 1500,
                    MaxOutputTokensPrSummary = 2000,
                    MaxInputLinesPerFile = 3000,
                    PromptStyle = "imperative",
                    Preamble = "GPT-4 specific: keep responses concise.",
                    Quirks = new List<string> { "8K max output tokens" },
                },
                new()
                {
                    Name = "default",
                    ModelPattern = ".*",
                    PromptStyle = "imperative",
                    Preamble = "Generic model: respond ONLY with valid JSON.",
                    Quirks = new List<string> { "Generic adapter" },
                },
            },
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  1. Adapter Loading
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Resolver_LoadsAdaptersFromFile()
    {
        var config = CreateTestConfig();
        var path = WriteAdapters(config);

        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        Assert.AreEqual(3, resolver.Adapters.Count);
        Assert.AreEqual("gpt-4o", resolver.Adapters[0].Name);
        Assert.AreEqual("gpt-4", resolver.Adapters[1].Name);
        Assert.AreEqual("default", resolver.Adapters[2].Name);
    }

    [TestMethod]
    public void Resolver_MissingFile_ReturnsEmptyAdapters()
    {
        var resolver = new ModelAdapterResolver(ResolverLogger, "/nonexistent/model-adapters.json");

        Assert.AreEqual(0, resolver.Adapters.Count);
    }

    [TestMethod]
    public void Resolver_InvalidJson_ReturnsEmptyAdapters()
    {
        var path = Path.Combine(_tempDir, "model-adapters.json");
        File.WriteAllText(path, "{ not valid json !!!");

        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        Assert.AreEqual(0, resolver.Adapters.Count);
    }

    [TestMethod]
    public void Resolver_NullPath_DefaultResolve()
    {
        // Should not throw — just uses built-in default path resolution
        var resolver = new ModelAdapterResolver(ResolverLogger);
        Assert.IsNotNull(resolver);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. Pattern Matching
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Resolve_Gpt4o_MatchesGpt4oAdapter()
    {
        var path = WriteAdapters(CreateTestConfig());
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4o");

        Assert.AreEqual("gpt-4o", adapter.Name);
    }

    [TestMethod]
    public void Resolve_Gpt4oMini_MatchesGpt4oAdapter()
    {
        var path = WriteAdapters(CreateTestConfig());
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4o-mini");

        Assert.AreEqual("gpt-4o", adapter.Name);
    }

    [TestMethod]
    public void Resolve_Gpt4_MatchesGpt4Adapter()
    {
        var path = WriteAdapters(CreateTestConfig());
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4");

        Assert.AreEqual("gpt-4", adapter.Name);
    }

    [TestMethod]
    public void Resolve_Gpt4Turbo_MatchesGpt4Adapter()
    {
        var path = WriteAdapters(CreateTestConfig());
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4-turbo");

        Assert.AreEqual("gpt-4", adapter.Name);
    }

    [TestMethod]
    public void Resolve_UnknownModel_MatchesDefaultAdapter()
    {
        var path = WriteAdapters(CreateTestConfig());
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("claude-3.5-sonnet");

        Assert.AreEqual("default", adapter.Name);
    }

    [TestMethod]
    public void Resolve_EmptyModelName_ReturnsBuiltInDefault()
    {
        var path = WriteAdapters(CreateTestConfig());
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("");

        Assert.AreEqual("built-in-default", adapter.Name);
    }

    [TestMethod]
    public void Resolve_NoAdaptersLoaded_ReturnsBuiltInDefault()
    {
        var resolver = new ModelAdapterResolver(ResolverLogger, "/nonexistent/path");

        var adapter = resolver.Resolve("gpt-4o");

        Assert.AreEqual("built-in-default", adapter.Name);
    }

    [TestMethod]
    public void Resolve_FirstMatchWins()
    {
        // Both adapters match "gpt-4o", first should win
        var config = new ModelAdapterConfig
        {
            Adapters = new List<ModelAdapter>
            {
                new() { Name = "first", ModelPattern = "gpt-4o" },
                new() { Name = "second", ModelPattern = "gpt-4o" },
            },
        };
        var path = WriteAdapters(config);
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4o");

        Assert.AreEqual("first", adapter.Name);
    }

    [TestMethod]
    public void Resolve_InvalidRegex_SkipsAdapter()
    {
        var config = new ModelAdapterConfig
        {
            Adapters = new List<ModelAdapter>
            {
                new() { Name = "bad-regex", ModelPattern = "[invalid(" },
                new() { Name = "fallback", ModelPattern = ".*" },
            },
        };
        var path = WriteAdapters(config);
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4o");

        Assert.AreEqual("fallback", adapter.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. Parameter Overrides
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ApplyOverrides_OverridesAllFields()
    {
        var baseProfile = new ReviewProfile
        {
            Temperature = 0.5f,
            MaxOutputTokensBatch = 10000,
            MaxOutputTokensSingleFile = 5000,
            MaxOutputTokensVerification = 3000,
            MaxOutputTokensPrSummary = 5000,
        };

        var adapter = new ModelAdapter
        {
            Temperature = 0.05f,
            MaxOutputTokensBatch = 7000,
            MaxOutputTokensSingleFile = 2000,
            MaxOutputTokensVerification = 1500,
            MaxOutputTokensPrSummary = 2000,
        };

        var result = ModelAdapterResolver.ApplyOverrides(baseProfile, adapter);

        Assert.AreEqual(0.05f, result.Temperature);
        Assert.AreEqual(7000, result.MaxOutputTokensBatch);
        Assert.AreEqual(2000, result.MaxOutputTokensSingleFile);
        Assert.AreEqual(1500, result.MaxOutputTokensVerification);
        Assert.AreEqual(2000, result.MaxOutputTokensPrSummary);
    }

    [TestMethod]
    public void ApplyOverrides_NullFields_KeepsBaseProfile()
    {
        var baseProfile = new ReviewProfile
        {
            Temperature = 0.5f,
            MaxOutputTokensBatch = 10000,
            MaxOutputTokensSingleFile = 5000,
            MaxOutputTokensVerification = 3000,
            MaxOutputTokensPrSummary = 5000,
        };

        var adapter = new ModelAdapter(); // All nulls

        var result = ModelAdapterResolver.ApplyOverrides(baseProfile, adapter);

        Assert.AreEqual(0.5f, result.Temperature);
        Assert.AreEqual(10000, result.MaxOutputTokensBatch);
        Assert.AreEqual(5000, result.MaxOutputTokensSingleFile);
        Assert.AreEqual(3000, result.MaxOutputTokensVerification);
        Assert.AreEqual(5000, result.MaxOutputTokensPrSummary);
    }

    [TestMethod]
    public void ApplyOverrides_PartialOverrides_MergesCorrectly()
    {
        var baseProfile = new ReviewProfile
        {
            Temperature = 0.5f,
            MaxOutputTokensBatch = 10000,
            MaxOutputTokensSingleFile = 5000,
        };

        var adapter = new ModelAdapter
        {
            Temperature = 0.1f,
            // MaxOutputTokensBatch is null — should keep base
            MaxOutputTokensSingleFile = 2000,
        };

        var result = ModelAdapterResolver.ApplyOverrides(baseProfile, adapter);

        Assert.AreEqual(0.1f, result.Temperature);
        Assert.AreEqual(10000, result.MaxOutputTokensBatch); // Kept from base
        Assert.AreEqual(2000, result.MaxOutputTokensSingleFile); // Overridden
    }

    [TestMethod]
    public void ApplyOverrides_PreservesVerdictThresholds()
    {
        var baseProfile = new ReviewProfile
        {
            Temperature = 0.5f,
            VerdictThresholds = new VerdictThresholds
            {
                RejectOnCriticalCount = 5,
                NeedsWorkOnWarningCount = 10,
            },
        };

        var adapter = new ModelAdapter { Temperature = 0.1f };

        var result = ModelAdapterResolver.ApplyOverrides(baseProfile, adapter);

        Assert.AreEqual(0.1f, result.Temperature);
        Assert.AreEqual(5, result.VerdictThresholds.RejectOnCriticalCount);
        Assert.AreEqual(10, result.VerdictThresholds.NeedsWorkOnWarningCount);
    }

    [TestMethod]
    public void GetEffectiveMaxInputLines_WithOverride_UsesAdapter()
    {
        var adapter = new ModelAdapter { MaxInputLinesPerFile = 3000 };

        var result = ModelAdapterResolver.GetEffectiveMaxInputLines(5000, adapter);

        Assert.AreEqual(3000, result);
    }

    [TestMethod]
    public void GetEffectiveMaxInputLines_WithoutOverride_UsesDefault()
    {
        var adapter = new ModelAdapter(); // null MaxInputLinesPerFile

        var result = ModelAdapterResolver.GetEffectiveMaxInputLines(5000, adapter);

        Assert.AreEqual(5000, result);
    }

    [TestMethod]
    public void GetEffectiveMaxInputLines_ZeroOverride_FallsBackToDefault()
    {
        var adapter = new ModelAdapter { MaxInputLinesPerFile = 0 };

        var result = ModelAdapterResolver.GetEffectiveMaxInputLines(5000, adapter);

        Assert.AreEqual(5000, result);
    }

    [TestMethod]
    public void GetEffectiveMaxInputLines_NegativeOverride_FallsBackToDefault()
    {
        var adapter = new ModelAdapter { MaxInputLinesPerFile = -1 };

        var result = ModelAdapterResolver.GetEffectiveMaxInputLines(5000, adapter);

        Assert.AreEqual(5000, result);
    }

    [TestMethod]
    public void Resolve_NullQuirks_DoesNotThrow()
    {
        var config = new ModelAdapterConfig
        {
            Adapters = new List<ModelAdapter>
            {
                new() { Name = "null-quirks", ModelPattern = ".*", Quirks = null! },
            },
        };
        var path = WriteAdapters(config);
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        // Should not throw NRE
        var adapter = resolver.Resolve("gpt-4o");
        Assert.AreEqual("null-quirks", adapter.Name);
    }

    [TestMethod]
    public void Resolve_NullModelPattern_SkipsAdapter()
    {
        var config = new ModelAdapterConfig
        {
            Adapters = new List<ModelAdapter>
            {
                new() { Name = "null-pattern", ModelPattern = null! },
                new() { Name = "fallback", ModelPattern = ".*" },
            },
        };
        var path = WriteAdapters(config);
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4o");
        Assert.AreEqual("fallback", adapter.Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. Prompt Pipeline Integration
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Pipeline_AdapterPreamble_InjectedBetweenIdentityAndCustomInstructions()
    {
        var catalog = new ReviewRuleCatalog
        {
            Version = "1.0",
            Identity = "You are a code reviewer.",
            Scopes = new Dictionary<string, PromptScope>
            {
                ["batch"] = new PromptScope
                {
                    IncludeIdentity = true,
                    IncludeCustomInstructions = true,
                    Preamble = "Review preamble.",
                    RulesHeader = "Rules:",
                },
            },
            Rules = new List<ReviewRule>
            {
                new() { Id = "r01", Scope = "batch", Category = "format", Priority = 100, Enabled = true, Text = "Rule one." },
            },
        };

        var catalogPath = Path.Combine(_tempDir, "review-rules.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));

        using var pipeline = new PromptAssemblyPipeline(PipelineLogger, catalogPath);
        var prompt = pipeline.AssemblePrompt("batch", "Custom instructions text.", "MODEL ADAPTER PREAMBLE");

        Assert.IsNotNull(prompt);
        // Verify layer order: Identity → Adapter Preamble → Custom Instructions → Preamble → Rules
        var identityIdx = prompt!.IndexOf("You are a code reviewer.");
        var adapterIdx = prompt.IndexOf("MODEL ADAPTER PREAMBLE");
        var customIdx = prompt.IndexOf("Custom instructions text.");
        var preambleIdx = prompt.IndexOf("Review preamble.");
        var ruleIdx = prompt.IndexOf("Rule one.");

        Assert.IsTrue(identityIdx >= 0, "Identity should be present");
        Assert.IsTrue(adapterIdx >= 0, "Adapter preamble should be present");
        Assert.IsTrue(customIdx >= 0, "Custom instructions should be present");
        Assert.IsTrue(preambleIdx >= 0, "Scope preamble should be present");
        Assert.IsTrue(ruleIdx >= 0, "Rules should be present");

        Assert.IsTrue(identityIdx < adapterIdx, "Identity should come before adapter preamble");
        Assert.IsTrue(adapterIdx < customIdx, "Adapter preamble should come before custom instructions");
        Assert.IsTrue(customIdx < preambleIdx, "Custom instructions should come before scope preamble");
        Assert.IsTrue(preambleIdx < ruleIdx, "Scope preamble should come before rules");
    }

    [TestMethod]
    public void Pipeline_NullAdapterPreamble_SkipsLayer()
    {
        var catalog = new ReviewRuleCatalog
        {
            Version = "1.0",
            Identity = "You are a code reviewer.",
            Scopes = new Dictionary<string, PromptScope>
            {
                ["batch"] = new PromptScope
                {
                    IncludeIdentity = true,
                    IncludeCustomInstructions = false,
                    Preamble = "Preamble.",
                    RulesHeader = "Rules:",
                },
            },
            Rules = new List<ReviewRule>(),
        };

        var catalogPath = Path.Combine(_tempDir, "review-rules.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));

        using var pipeline = new PromptAssemblyPipeline(PipelineLogger, catalogPath);
        var prompt = pipeline.AssemblePrompt("batch", null, null);

        Assert.IsNotNull(prompt);
        Assert.IsTrue(prompt!.Contains("You are a code reviewer."));
        Assert.IsTrue(prompt.Contains("Preamble."));
        Assert.IsFalse(prompt.Contains("MODEL ADAPTER"));
    }

    [TestMethod]
    public void Pipeline_CacheKey_DistinguishesDifferentAdapterPreambles()
    {
        var catalog = new ReviewRuleCatalog
        {
            Version = "1.0",
            Identity = "Identity.",
            Scopes = new Dictionary<string, PromptScope>
            {
                ["batch"] = new PromptScope
                {
                    IncludeIdentity = true,
                    IncludeCustomInstructions = false,
                    Preamble = "Preamble.",
                    RulesHeader = "Rules:",
                },
            },
            Rules = new List<ReviewRule>(),
        };

        var catalogPath = Path.Combine(_tempDir, "review-rules.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));

        using var pipeline = new PromptAssemblyPipeline(PipelineLogger, catalogPath);
        var prompt1 = pipeline.AssemblePrompt("batch", null, "Preamble A");
        var prompt2 = pipeline.AssemblePrompt("batch", null, "Preamble B");

        Assert.IsNotNull(prompt1);
        Assert.IsNotNull(prompt2);
        Assert.IsTrue(prompt1!.Contains("Preamble A"));
        Assert.IsTrue(prompt2!.Contains("Preamble B"));
        Assert.AreNotEqual(prompt1, prompt2);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. Shipped Adapter Profiles
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ShippedAdapters_FileExists()
    {
        var path = FindProjectFile("model-adapters.json");
        Assert.IsTrue(File.Exists(path), $"model-adapters.json should exist at {path}");
    }

    [TestMethod]
    public void ShippedAdapters_LoadsSuccessfully()
    {
        var path = FindProjectFile("model-adapters.json");
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        Assert.IsTrue(resolver.Adapters.Count >= 3,
            "At least 3 adapter profiles should be shipped (gpt-4o, gpt-4, default)");
    }

    [TestMethod]
    public void ShippedAdapters_Gpt4o_Matches()
    {
        var path = FindProjectFile("model-adapters.json");
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4o");
        Assert.AreEqual("gpt-4o", adapter.Name);
        Assert.IsNotNull(adapter.Preamble);
        Assert.IsTrue(adapter.Quirks.Count > 0);
    }

    [TestMethod]
    public void ShippedAdapters_Gpt4_Matches()
    {
        var path = FindProjectFile("model-adapters.json");
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4");
        Assert.AreEqual("gpt-4", adapter.Name);
        Assert.IsNotNull(adapter.Preamble);
        Assert.IsTrue(adapter.Temperature < 0.1f, "gpt-4 adapter should use lower temperature");
    }

    [TestMethod]
    public void ShippedAdapters_Default_CatchAll()
    {
        var path = FindProjectFile("model-adapters.json");
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("some-unknown-model-xyz");
        Assert.AreEqual("default", adapter.Name);
    }

    [TestMethod]
    public void ShippedAdapters_Gpt4oMini_MatchesOwnAdapter()
    {
        var path = FindProjectFile("model-adapters.json");
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        var adapter = resolver.Resolve("gpt-4o-mini");
        Assert.AreEqual("gpt-4o-mini", adapter.Name);
    }

    [TestMethod]
    public void ShippedAdapters_AllHaveRequiredFields()
    {
        var path = FindProjectFile("model-adapters.json");
        var resolver = new ModelAdapterResolver(ResolverLogger, path);

        foreach (var adapter in resolver.Adapters)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(adapter.Name),
                $"Adapter should have a name");
            Assert.IsFalse(string.IsNullOrWhiteSpace(adapter.ModelPattern),
                $"Adapter '{adapter.Name}' should have a model pattern");
            Assert.IsFalse(string.IsNullOrWhiteSpace(adapter.PromptStyle),
                $"Adapter '{adapter.Name}' should have a prompt style");
        }
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  6. Backward Compatibility
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Pipeline_WithoutAdapterPreamble_ProducesSameOutput()
    {
        var catalog = new ReviewRuleCatalog
        {
            Version = "1.0",
            Identity = "You are a code reviewer.",
            Scopes = new Dictionary<string, PromptScope>
            {
                ["batch"] = new PromptScope
                {
                    IncludeIdentity = true,
                    IncludeCustomInstructions = true,
                    Preamble = "Review preamble.",
                    RulesHeader = "Rules:",
                },
            },
            Rules = new List<ReviewRule>
            {
                new() { Id = "r01", Scope = "batch", Category = "format", Priority = 100, Enabled = true, Text = "Rule one." },
            },
        };

        var catalogPath = Path.Combine(_tempDir, "review-rules.json");
        File.WriteAllText(catalogPath, JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true }));

        using var pipeline = new PromptAssemblyPipeline(PipelineLogger, catalogPath);

        // Without adapter preamble (backward compatible)
        var promptNoAdapter = pipeline.AssemblePrompt("batch", "Custom");
        // With null adapter preamble (explicit null)
        var promptNullAdapter = pipeline.AssemblePrompt("batch", "Custom", null);

        Assert.AreEqual(promptNoAdapter, promptNullAdapter);
    }

    [TestMethod]
    public void Resolver_BuiltInDefault_HasNullPreamble()
    {
        var resolver = new ModelAdapterResolver(ResolverLogger, "/nonexistent/path");
        var adapter = resolver.Resolve("any-model");

        Assert.IsNull(adapter.Preamble, "Built-in default adapter should have null preamble");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Walks up from the test output directory to find a source file in the main project.
    /// </summary>
    private static string FindProjectFile(string fileName)
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            var slnFiles = Directory.GetFiles(dir, "*.sln");
            if (slnFiles.Length > 0)
            {
                var candidate = Path.Combine(dir, "src", "HVO.AiCodeReview", fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new FileNotFoundException(
            $"Could not find '{fileName}' in the project directory tree.");
    }
}
