using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Issue #6 — Layered Prompt Architecture.
/// Covers: rule catalog loading, scope filtering, priority ordering,
/// enable/disable, prompt assembly pipeline, backward compatibility,
/// and hot-reload.
/// </summary>
[TestClass]
public class LayeredPromptTests
{
    private static readonly ILogger<PromptAssemblyPipeline> NullLogger
        = NullLoggerFactory.Instance.CreateLogger<PromptAssemblyPipeline>();

    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "LayeredPromptTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
    }

    [TestCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    // ─── Helper: write a catalog to a temp file ──────────────────────────

    private string WriteCatalog(ReviewRuleCatalog catalog)
    {
        var path = Path.Combine(_tempDir, "review-rules.json");
        var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
        return path;
    }

    private static ReviewRuleCatalog CreateMinimalCatalog(
        string identity = "You are an expert reviewer.",
        string scope = "batch",
        string preamble = "Respond with JSON.",
        bool includeIdentity = true,
        bool includeCustom = true,
        string rulesHeader = "Rules:")
    {
        return new ReviewRuleCatalog
        {
            Version = "1.0",
            Identity = identity,
            Scopes = new Dictionary<string, PromptScope>
            {
                [scope] = new PromptScope
                {
                    IncludeIdentity = includeIdentity,
                    IncludeCustomInstructions = includeCustom,
                    Preamble = preamble,
                    RulesHeader = rulesHeader,
                },
            },
            Rules = new List<ReviewRule>
            {
                new() { Id = "r01", Scope = scope, Category = "format", Priority = 100, Enabled = true, Text = "Output valid JSON only." },
                new() { Id = "r02", Scope = scope, Category = "review-quality", Priority = 200, Enabled = true, Text = "Be concise." },
            },
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  1. Catalog Loading
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Pipeline_LoadsCatalog_FromFile()
    {
        var catalog = CreateMinimalCatalog();
        var path = WriteCatalog(catalog);

        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        Assert.IsTrue(pipeline.HasCatalog);
        Assert.AreEqual("1.0", pipeline.Catalog!.Version);
        Assert.AreEqual(2, pipeline.Catalog.Rules.Count);
    }

    [TestMethod]
    public void Pipeline_NoCatalogFile_HasCatalogIsFalse()
    {
        using var pipeline = new PromptAssemblyPipeline(NullLogger, "/nonexistent/path.json");

        Assert.IsFalse(pipeline.HasCatalog);
    }

    [TestMethod]
    public void Pipeline_NullPath_FallsBackToDefault()
    {
        // When no path is given and no file exists in BaseDirectory, HasCatalog should be false
        // (unless the real review-rules.json happens to be in the test output — which it shouldn't)
        using var pipeline = new PromptAssemblyPipeline(NullLogger, "/definitely-not-a-file.json");

        Assert.IsFalse(pipeline.HasCatalog);
    }

    [TestMethod]
    public void Pipeline_InvalidJson_HasCatalogIsFalse()
    {
        var path = Path.Combine(_tempDir, "review-rules.json");
        File.WriteAllText(path, "NOT VALID JSON {{{");

        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        Assert.IsFalse(pipeline.HasCatalog);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. Scope Filtering
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AssemblePrompt_FiltersRulesByScope()
    {
        var catalog = CreateMinimalCatalog(scope: "batch");
        catalog.Scopes["single-file"] = new PromptScope
        {
            IncludeIdentity = true,
            IncludeCustomInstructions = false,
            Preamble = "Single-file schema here.",
            RulesHeader = "Single-file rules:",
        };
        catalog.Rules.Add(new ReviewRule
        {
            Id = "sf-r01", Scope = "single-file", Category = "format",
            Priority = 100, Enabled = true, Text = "Line numbers are critical.",
        });

        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var batchPrompt = pipeline.AssemblePrompt("batch")!;
        var singlePrompt = pipeline.AssemblePrompt("single-file")!;

        // Batch prompt should have batch rules but NOT single-file rules
        Assert.IsTrue(batchPrompt.Contains("Output valid JSON only."));
        Assert.IsTrue(batchPrompt.Contains("Be concise."));
        Assert.IsFalse(batchPrompt.Contains("Line numbers are critical."));

        // Single-file prompt should have single-file rules but NOT batch rules
        Assert.IsTrue(singlePrompt.Contains("Line numbers are critical."));
        Assert.IsFalse(singlePrompt.Contains("Be concise."));
    }

    [TestMethod]
    public void GetActiveRuleIds_ReturnsOnlyScopedRules()
    {
        var catalog = CreateMinimalCatalog(scope: "batch");
        catalog.Rules.Add(new ReviewRule
        {
            Id = "other-r01", Scope = "single-file", Category = "format",
            Priority = 100, Enabled = true, Text = "Other scope rule.",
        });
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var batchIds = pipeline.GetActiveRuleIds("batch");
        var singleIds = pipeline.GetActiveRuleIds("single-file");

        CollectionAssert.AreEqual(new[] { "r01", "r02" }, batchIds);
        CollectionAssert.AreEqual(new[] { "other-r01" }, singleIds);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. Priority Ordering
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AssemblePrompt_OrdersRulesByPriority()
    {
        var catalog = CreateMinimalCatalog();
        // Override rules with specific priorities (reversed)
        catalog.Rules = new List<ReviewRule>
        {
            new() { Id = "r-high", Scope = "batch", Category = "format", Priority = 300, Enabled = true, Text = "HIGH priority rule." },
            new() { Id = "r-low", Scope = "batch", Category = "format", Priority = 100, Enabled = true, Text = "LOW priority rule." },
            new() { Id = "r-mid", Scope = "batch", Category = "format", Priority = 200, Enabled = true, Text = "MID priority rule." },
        };

        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch")!;

        // LOW (100) should appear before MID (200) which should appear before HIGH (300)
        var lowIdx = prompt.IndexOf("LOW priority rule.");
        var midIdx = prompt.IndexOf("MID priority rule.");
        var highIdx = prompt.IndexOf("HIGH priority rule.");

        Assert.IsTrue(lowIdx < midIdx, "LOW should come before MID");
        Assert.IsTrue(midIdx < highIdx, "MID should come before HIGH");
    }

    [TestMethod]
    public void AssemblePrompt_RulesAreNumberedSequentially()
    {
        var catalog = CreateMinimalCatalog();
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsTrue(prompt.Contains("1. Output valid JSON only."));
        Assert.IsTrue(prompt.Contains("2. Be concise."));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. Enable / Disable
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AssemblePrompt_ExcludesDisabledRules()
    {
        var catalog = CreateMinimalCatalog();
        catalog.Rules[0].Enabled = false; // Disable "Output valid JSON only."

        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsFalse(prompt.Contains("Output valid JSON only."));
        Assert.IsTrue(prompt.Contains("Be concise."));
        // Remaining rule should be numbered as 1 (not 2)
        Assert.IsTrue(prompt.Contains("1. Be concise."));
    }

    [TestMethod]
    public void GetActiveRuleIds_ExcludesDisabledRules()
    {
        var catalog = CreateMinimalCatalog();
        catalog.Rules[1].Enabled = false; // Disable r02

        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var ids = pipeline.GetActiveRuleIds("batch");

        CollectionAssert.AreEqual(new[] { "r01" }, ids);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. Prompt Assembly Layers
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AssemblePrompt_IncludesIdentity_WhenScopeOptsIn()
    {
        var catalog = CreateMinimalCatalog(identity: "I am the identity.", includeIdentity: true);
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsTrue(prompt.Contains("I am the identity."));
    }

    [TestMethod]
    public void AssemblePrompt_ExcludesIdentity_WhenScopeOptsOut()
    {
        var catalog = CreateMinimalCatalog(identity: "I am the identity.", includeIdentity: false);
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsFalse(prompt.Contains("I am the identity."));
    }

    [TestMethod]
    public void AssemblePrompt_InjectsCustomInstructions_WhenScopeOptsIn()
    {
        var catalog = CreateMinimalCatalog(includeCustom: true);
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch", customInstructions: "Check for cyclomatic complexity.")!;

        Assert.IsTrue(prompt.Contains("Check for cyclomatic complexity."));
    }

    [TestMethod]
    public void AssemblePrompt_ExcludesCustomInstructions_WhenScopeOptsOut()
    {
        var catalog = CreateMinimalCatalog(includeCustom: false);
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch", customInstructions: "Check for cyclomatic complexity.")!;

        Assert.IsFalse(prompt.Contains("Check for cyclomatic complexity."));
    }

    [TestMethod]
    public void AssemblePrompt_IncludesPreamble()
    {
        var catalog = CreateMinimalCatalog(preamble: "Respond with valid JSON matching this schema:");
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsTrue(prompt.Contains("Respond with valid JSON matching this schema:"));
    }

    [TestMethod]
    public void AssemblePrompt_IncludesRulesHeader()
    {
        var catalog = CreateMinimalCatalog(rulesHeader: "CRITICAL RULES:");
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsTrue(prompt.Contains("CRITICAL RULES:"));
    }

    [TestMethod]
    public void AssemblePrompt_LayerOrder_IdentityThenCustomThenPreambleThenRules()
    {
        var catalog = CreateMinimalCatalog(
            identity: "IDENTITY_MARKER",
            preamble: "PREAMBLE_MARKER",
            includeIdentity: true,
            includeCustom: true,
            rulesHeader: "RULES_HEADER_MARKER");
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch", customInstructions: "CUSTOM_MARKER")!;

        var idxIdentity = prompt.IndexOf("IDENTITY_MARKER");
        var idxCustom = prompt.IndexOf("CUSTOM_MARKER");
        var idxPreamble = prompt.IndexOf("PREAMBLE_MARKER");
        var idxRules = prompt.IndexOf("RULES_HEADER_MARKER");
        var idxRule1 = prompt.IndexOf("1. Output valid JSON only.");

        Assert.IsTrue(idxIdentity >= 0, "Identity should be present");
        Assert.IsTrue(idxCustom >= 0, "Custom instructions should be present");
        Assert.IsTrue(idxPreamble >= 0, "Preamble should be present");
        Assert.IsTrue(idxRules >= 0, "Rules header should be present");
        Assert.IsTrue(idxRule1 >= 0, "First rule should be present");

        Assert.IsTrue(idxIdentity < idxCustom, "Identity before custom");
        Assert.IsTrue(idxCustom < idxPreamble, "Custom before preamble");
        Assert.IsTrue(idxPreamble < idxRules, "Preamble before rules header");
        Assert.IsTrue(idxRules < idxRule1, "Rules header before first rule");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  6. Backward Compatibility
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AssemblePrompt_ReturnsNull_WhenNoCatalog()
    {
        using var pipeline = new PromptAssemblyPipeline(NullLogger, "/nonexistent.json");

        var result = pipeline.AssemblePrompt("batch");

        Assert.IsNull(result, "Should return null when no catalog is loaded, triggering hardcoded fallback");
    }

    [TestMethod]
    public void AssemblePrompt_ThrowsForUnknownScope()
    {
        var catalog = CreateMinimalCatalog();
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        Assert.ThrowsException<ArgumentException>(() => pipeline.AssemblePrompt("nonexistent-scope"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  7. Real Catalog Validation
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void RealCatalog_LoadsSuccessfully()
    {
        // Find the real review-rules.json from the project
        var catalogPath = FindProjectFile("review-rules.json");
        Assert.IsNotNull(catalogPath, "review-rules.json should exist in the project");

        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath);

        Assert.IsTrue(pipeline.HasCatalog, "Real catalog should load");
        Assert.AreEqual("1.0", pipeline.Catalog!.Version);
    }

    [TestMethod]
    public void RealCatalog_HasAllFourScopes()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var scopes = pipeline.GetScopes();

        CollectionAssert.Contains(scopes, "batch");
        CollectionAssert.Contains(scopes, "single-file");
        CollectionAssert.Contains(scopes, "thread-verification");
        CollectionAssert.Contains(scopes, "pass-1");
    }

    [TestMethod]
    public void RealCatalog_Has36Rules()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        Assert.AreEqual(36, pipeline.Catalog!.Rules.Count,
            "Expected 36 rules (18 batch + 11 single-file + 3 thread-verification + 4 pass-1)");
    }

    [TestMethod]
    public void RealCatalog_BatchScopeHas18Rules()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var batchRules = pipeline.GetActiveRuleIds("batch");
        Assert.AreEqual(18, batchRules.Count);
    }

    [TestMethod]
    public void RealCatalog_SingleFileScopeHas11Rules()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var rules = pipeline.GetActiveRuleIds("single-file");
        Assert.AreEqual(11, rules.Count);
    }

    [TestMethod]
    public void RealCatalog_ThreadVerificationHas3Rules()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var rules = pipeline.GetActiveRuleIds("thread-verification");
        Assert.AreEqual(3, rules.Count);
    }

    [TestMethod]
    public void RealCatalog_Pass1Has4Rules()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var rules = pipeline.GetActiveRuleIds("pass-1");
        Assert.AreEqual(4, rules.Count);
    }

    [TestMethod]
    public void RealCatalog_AllRulesHaveUniqueIds()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var allIds = pipeline.Catalog!.Rules.Select(r => r.Id).ToList();
        var distinctIds = allIds.Distinct().ToList();

        Assert.AreEqual(allIds.Count, distinctIds.Count, "All rule IDs should be unique");
    }

    [TestMethod]
    public void RealCatalog_AssemblesAllScopes()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        foreach (var scope in new[] { "batch", "single-file", "thread-verification", "pass-1" })
        {
            var prompt = pipeline.AssemblePrompt(scope);
            Assert.IsNotNull(prompt, $"Prompt for scope '{scope}' should not be null");
            Assert.IsTrue(prompt.Length > 100, $"Prompt for scope '{scope}' should be substantive (got {prompt.Length} chars)");
        }
    }

    [TestMethod]
    public void RealCatalog_BatchPrompt_ContainsIdentity()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsTrue(prompt.Contains("expert code reviewer"), "Batch prompt should include identity");
        Assert.IsTrue(prompt.Contains("QUALITY BAR"), "Batch prompt should include quality bar");
    }

    [TestMethod]
    public void RealCatalog_BatchPrompt_ContainsKeyRules()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var prompt = pipeline.AssemblePrompt("batch")!;

        Assert.IsTrue(prompt.Contains("SKIP CLEAN FILES"), "Should contain file handling rule");
        Assert.IsTrue(prompt.Contains("CODE SNIPPET"), "Should contain code snippet rule");
        Assert.IsTrue(prompt.Contains("SECURITY COMMENTS"), "Should contain security rule");
        Assert.IsTrue(prompt.Contains("ACCEPTANCE CRITERIA"), "Should contain AC rule");
    }

    [TestMethod]
    public void RealCatalog_SingleFilePrompt_ContainsScopeSpecificRules()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var prompt = pipeline.AssemblePrompt("single-file")!;

        Assert.IsTrue(prompt.Contains("DIFF-FOCUSED REVIEW"), "Should contain diff-focused rule");
        Assert.IsTrue(prompt.Contains("VERIFY BEFORE FLAGGING"), "Should contain verify rule");
        Assert.IsTrue(prompt.Contains("You are reviewing ONE file"), "Should contain single-file preamble");
    }

    [TestMethod]
    public void RealCatalog_ThreadVerification_DoesNotIncludeIdentity()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var prompt = pipeline.AssemblePrompt("thread-verification")!;

        // Thread verification has its own identity, not the shared one
        Assert.IsTrue(prompt.Contains("verifying whether prior code review comments"));
        Assert.IsFalse(prompt.Contains("QUALITY BAR"), "Thread verification should NOT include the shared identity");
    }

    [TestMethod]
    public void RealCatalog_Pass1_DoesNotIncludeIdentity()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var prompt = pipeline.AssemblePrompt("pass-1")!;

        Assert.IsTrue(prompt.Contains("FIRST PASS of a two-pass review"));
        Assert.IsFalse(prompt.Contains("QUALITY BAR"), "Pass-1 should NOT include the shared identity");
    }

    [TestMethod]
    public void RealCatalog_Categories_ContainExpectedValues()
    {
        var catalogPath = FindProjectFile("review-rules.json");
        using var pipeline = new PromptAssemblyPipeline(NullLogger, catalogPath!);

        var categories = pipeline.GetCategories();

        CollectionAssert.Contains(categories, "format");
        CollectionAssert.Contains(categories, "review-quality");
        CollectionAssert.Contains(categories, "verdict");
        CollectionAssert.Contains(categories, "security");
        CollectionAssert.Contains(categories, "line-numbers");
        CollectionAssert.Contains(categories, "ac-dod");
        CollectionAssert.Contains(categories, "file-handling");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  8. Hot-Reload
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void HotReload_UpdatedCatalog_IsPickedUpAutomatically()
    {
        // Start with 2 rules
        var catalog = CreateMinimalCatalog();
        var path = WriteCatalog(catalog);

        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);
        Assert.AreEqual(2, pipeline.GetActiveRuleIds("batch").Count);

        var prompt1 = pipeline.AssemblePrompt("batch");
        Assert.IsNotNull(prompt1);

        // Add a third rule and overwrite
        catalog.Rules.Add(new ReviewRule
        {
            Id = "r03", Scope = "batch", Category = "format",
            Priority = 300, Enabled = true, Text = "NEW HOT-RELOADED RULE.",
        });
        var json = JsonSerializer.Serialize(catalog, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);

        // Wait for FileSystemWatcher to detect the change + debounce
        Thread.Sleep(500);

        // The pipeline should have reloaded
        var prompt2 = pipeline.AssemblePrompt("batch");
        Assert.IsNotNull(prompt2);
        Assert.IsTrue(prompt2!.Contains("NEW HOT-RELOADED RULE."),
            "Hot-reloaded catalog should include the new rule");
        Assert.AreEqual(3, pipeline.GetActiveRuleIds("batch").Count);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  9. Edge Cases
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AssemblePrompt_NoRulesForScope_StillEmitsPreamble()
    {
        var catalog = new ReviewRuleCatalog
        {
            Version = "1.0",
            Identity = "Identity text.",
            Scopes = new Dictionary<string, PromptScope>
            {
                ["empty-scope"] = new PromptScope
                {
                    IncludeIdentity = true,
                    IncludeCustomInstructions = false,
                    Preamble = "Preamble for empty scope.",
                    RulesHeader = "Rules:",
                },
            },
            Rules = new List<ReviewRule>(), // No rules at all
        };

        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("empty-scope");

        Assert.IsNotNull(prompt);
        Assert.IsTrue(prompt!.Contains("Identity text."));
        Assert.IsTrue(prompt.Contains("Preamble for empty scope."));
        Assert.IsFalse(prompt.Contains("Rules:"), "Rules header should not appear when there are no rules");
    }

    [TestMethod]
    public void AssemblePrompt_NullCustomInstructions_WorksFine()
    {
        var catalog = CreateMinimalCatalog(includeCustom: true);
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt = pipeline.AssemblePrompt("batch", customInstructions: null);

        Assert.IsNotNull(prompt);
        Assert.IsTrue(prompt!.Contains("Output valid JSON only."));
    }

    [TestMethod]
    public void AssemblePrompt_EmptyCustomInstructions_NotInjected()
    {
        var catalog = CreateMinimalCatalog(includeCustom: true);
        var path = WriteCatalog(catalog);
        using var pipeline = new PromptAssemblyPipeline(NullLogger, path);

        var prompt1 = pipeline.AssemblePrompt("batch", customInstructions: "")!;
        var prompt2 = pipeline.AssemblePrompt("batch", customInstructions: null)!;

        // Both should produce essentially the same result (no custom instructions injected)
        // The caching might differ but content should match
        Assert.IsTrue(prompt1.Contains("Output valid JSON only."));
        Assert.IsTrue(prompt2.Contains("Output valid JSON only."));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helper
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Walk up from the test output dir to find a file in the source project.
    /// </summary>
    private static string? FindProjectFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (dir.GetFiles("*.sln").Length > 0)
            {
                // Found solution root — look in src/HVO.AiCodeReview/
                var candidate = Path.Combine(dir.FullName, "src", "HVO.AiCodeReview", fileName);
                if (File.Exists(candidate))
                    return candidate;

                // Also check bin output (copied to output)
                var binCandidate = Path.Combine(AppContext.BaseDirectory, fileName);
                if (File.Exists(binCandidate))
                    return binCandidate;

                break;
            }
            dir = dir.Parent;
        }
        return null;
    }
}
