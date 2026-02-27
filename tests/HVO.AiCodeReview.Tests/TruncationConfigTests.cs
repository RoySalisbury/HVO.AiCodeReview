using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Issue #5 — configurable file truncation limit.
/// Validates that MaxInputLinesPerFile flows through settings, factory, and
/// the review service's prompt builder.
/// </summary>
[TestClass]
public class TruncationConfigTests
{
    // ── AC-1: Default is 5000 ──────────────────────────────────────────

    [TestMethod]
    public void AiProviderSettings_Default_MaxInputLinesPerFile_Is5000()
    {
        var settings = new AiProviderSettings();
        Assert.AreEqual(5000, settings.MaxInputLinesPerFile,
            "Global default must be 5 000 lines.");
    }

    [TestMethod]
    public void ProviderConfig_Default_MaxInputLinesPerFile_IsNull()
    {
        var config = new ProviderConfig();
        Assert.IsNull(config.MaxInputLinesPerFile,
            "Per-provider override must default to null (inherit global).");
    }

    // ── AC-2: Config binds from appsettings ────────────────────────────

    [TestMethod]
    public void AppsettingsJson_Binds_MaxInputLinesPerFile()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:MaxInputLinesPerFile"] = "3000",
                ["AiProvider:MaxParallelReviews"] = "5",
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "azure-openai",
            })
            .Build();

        var settings = config.GetSection("AiProvider").Get<AiProviderSettings>()!;
        Assert.AreEqual(3000, settings.MaxInputLinesPerFile);
    }

    [TestMethod]
    public void AppsettingsJson_Binds_ProviderLevel_MaxInputLinesPerFile()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Providers:myProvider:Type"] = "azure-openai",
                ["AiProvider:Providers:myProvider:MaxInputLinesPerFile"] = "8000",
            })
            .Build();

        var settings = config.GetSection("AiProvider").Get<AiProviderSettings>()!;
        var provider = settings.Providers["myProvider"];
        Assert.AreEqual(8000, provider.MaxInputLinesPerFile);
    }

    // ── AC-3: Truncation behaviour at the new limit ────────────────────

    [TestMethod]
    public void TruncateContent_BelowLimit_ReturnsUnchanged()
    {
        var service = CreateServiceWithMaxLines(100);
        var content = GenerateLines(99);
        var result = InvokeTruncate(service, content);
        Assert.AreEqual(content, result, "Content under the limit must not be altered.");
    }

    [TestMethod]
    public void TruncateContent_ExactlyAtLimit_ReturnsUnchanged()
    {
        var service = CreateServiceWithMaxLines(100);
        var content = GenerateLines(100);
        var result = InvokeTruncate(service, content);
        Assert.AreEqual(content, result, "Content exactly at the limit must not be altered.");
    }

    [TestMethod]
    public void TruncateContent_AboveLimit_Truncates_And_ShowsMarker()
    {
        var service = CreateServiceWithMaxLines(100);
        var content = GenerateLines(250);
        var result = InvokeTruncate(service, content);

        var resultLines = result.Split('\n');
        // First 100 data lines + 1 blank + 1 marker = 102
        Assert.AreEqual(102, resultLines.Length,
            $"Expected 102 lines after truncation (100 data + blank + marker), but got {resultLines.Length}.");
        Assert.IsTrue(result.Contains("... [truncated: 150 more lines] ..."),
            $"Truncation marker must show 150 remaining lines. Got:\n{result[^200..]}");
        // Verify we kept exactly 100 source lines
        Assert.IsTrue(result.StartsWith("Line 1\n"), "Must start with the first line.");
        Assert.IsTrue(result.Contains("Line 100\n"), "Must include line 100.");
        Assert.IsFalse(result.Contains("Line 101\n"), "Must NOT include line 101.");
    }

    [TestMethod]
    public void TruncateContent_DefaultLimit_5000_AllowsLargeFiles()
    {
        // Use default (5000)
        var service = CreateServiceWithMaxLines(5000);
        var content = GenerateLines(4999);
        var result = InvokeTruncate(service, content);
        Assert.AreEqual(content, result, "4999-line file should pass through with 5000-line limit.");
    }

    [TestMethod]
    public void TruncateContent_CustomLimit_AppliesCorrectly()
    {
        var service = CreateServiceWithMaxLines(200);
        var content = GenerateLines(300);
        var result = InvokeTruncate(service, content);
        Assert.IsTrue(result.Contains("... [truncated: 100 more lines] ..."),
            "Custom limit of 200 should truncate 300-line content.");
        Assert.IsTrue(result.Contains("Line 200\n"), "Must include line 200.");
        Assert.IsFalse(result.Contains("Line 201\n"), "Must NOT include line 201.");
    }

    // ── AC-4: Factory wires the value correctly ────────────────────────

    [TestMethod]
    public void Factory_PassesGlobalMaxInputLinesToProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:MaxInputLinesPerFile"] = "7500",
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Type"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Endpoint"] = "https://fake.openai.azure.com/",
                ["AiProvider:Providers:azure-openai:ApiKey"] = "fake-key",
                ["AiProvider:Providers:azure-openai:Model"] = "gpt-4o",
                ["AiProvider:Providers:azure-openai:Enabled"] = "true",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<ICodeReviewService>();
        Assert.IsInstanceOfType(service, typeof(AzureOpenAiReviewService));

        // Verify the limit is wired by testing truncation behaviour
        var content = GenerateLines(7501);
        var result = InvokeTruncate((AzureOpenAiReviewService)service, content);
        Assert.IsTrue(result.Contains("[truncated:"),
            "7501-line content should be truncated at configured limit of 7500.");
    }

    [TestMethod]
    public void Factory_ProviderOverride_TakesPrecedence()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:MaxInputLinesPerFile"] = "5000",
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Type"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Endpoint"] = "https://fake.openai.azure.com/",
                ["AiProvider:Providers:azure-openai:ApiKey"] = "fake-key",
                ["AiProvider:Providers:azure-openai:Model"] = "gpt-4o",
                ["AiProvider:Providers:azure-openai:Enabled"] = "true",
                ["AiProvider:Providers:azure-openai:MaxInputLinesPerFile"] = "1000",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var service = (AzureOpenAiReviewService)sp.GetRequiredService<ICodeReviewService>();

        // Provider override = 1000, so 1001 lines should truncate
        var content = GenerateLines(1001);
        var result = InvokeTruncate(service, content);
        Assert.IsTrue(result.Contains("[truncated: 1 more lines]"),
            "Provider-level override (1000) should take precedence over global (5000).");
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    /// <summary>
    /// Creates an <see cref="AzureOpenAiReviewService"/> with a specific
    /// truncation limit. Uses a fake endpoint — we only exercise prompt
    /// building, not the AI call itself.
    /// </summary>
    private static AzureOpenAiReviewService CreateServiceWithMaxLines(int maxLines)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        return new AzureOpenAiReviewService(
            "https://fake.openai.azure.com/",
            "fake-key",
            "gpt-4o",
            customInstructionsPath: null,
            loggerFactory.CreateLogger<AzureOpenAiReviewService>(),
            maxInputLinesPerFile: maxLines);
    }

    /// <summary>
    /// Invokes the private <c>TruncateContent</c> method via reflection.
    /// </summary>
    private static string InvokeTruncate(AzureOpenAiReviewService service, string content)
    {
        var method = typeof(AzureOpenAiReviewService)
            .GetMethod("TruncateContent",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        Assert.IsNotNull(method, "TruncateContent method must exist as a non-public instance method.");
        return (string)method.Invoke(service, new object[] { content })!;
    }

    /// <summary>Generate a string with the specified number of newline-separated lines.</summary>
    private static string GenerateLines(int count)
    {
        return string.Join('\n', Enumerable.Range(1, count).Select(i => $"Line {i}"));
    }
}
