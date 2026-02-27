using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Reflection;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Issue #8 — Configurable Review Profile.
/// Validates that Temperature, MaxOutputTokens*, and VerdictThresholds
/// flow correctly from configuration through the factory to the service.
/// </summary>
[TestClass]
public class ReviewProfileTests
{
    // ── AC-1: Defaults match previous hardcoded values ─────────────────

    [TestMethod]
    public void ReviewProfile_Defaults_MatchHardcodedValues()
    {
        var profile = new ReviewProfile();

        Assert.AreEqual(0.1f, profile.Temperature, "Default temperature must be 0.1.");
        Assert.AreEqual(16000, profile.MaxOutputTokensBatch, "Default batch tokens must be 16000.");
        Assert.AreEqual(4000, profile.MaxOutputTokensSingleFile, "Default single-file tokens must be 4000.");
        Assert.AreEqual(2000, profile.MaxOutputTokensVerification, "Default verification tokens must be 2000.");
        Assert.AreEqual(4000, profile.MaxOutputTokensPrSummary, "Default PR summary tokens must be 4000.");
    }

    [TestMethod]
    public void VerdictThresholds_Defaults_AreReasonable()
    {
        var thresholds = new VerdictThresholds();

        Assert.AreEqual(1, thresholds.RejectOnCriticalCount, "Default reject-on-critical must be 1.");
        Assert.AreEqual(3, thresholds.NeedsWorkOnWarningCount, "Default needs-work-on-warning must be 3.");
    }

    [TestMethod]
    public void ReviewProfile_SectionName_IsCorrect()
    {
        Assert.AreEqual("ReviewProfile", ReviewProfile.SectionName);
    }

    // ── AC-2: Config binding from appsettings ──────────────────────────

    [TestMethod]
    public void Config_Binds_AllReviewProfileValues()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewProfile:Temperature"] = "0.3",
                ["ReviewProfile:MaxOutputTokensBatch"] = "20000",
                ["ReviewProfile:MaxOutputTokensSingleFile"] = "6000",
                ["ReviewProfile:MaxOutputTokensVerification"] = "3000",
                ["ReviewProfile:MaxOutputTokensPrSummary"] = "5000",
            })
            .Build();

        var profile = config.GetSection("ReviewProfile").Get<ReviewProfile>()!;

        Assert.AreEqual(0.3f, profile.Temperature, 0.001f);
        Assert.AreEqual(20000, profile.MaxOutputTokensBatch);
        Assert.AreEqual(6000, profile.MaxOutputTokensSingleFile);
        Assert.AreEqual(3000, profile.MaxOutputTokensVerification);
        Assert.AreEqual(5000, profile.MaxOutputTokensPrSummary);
    }

    [TestMethod]
    public void Config_Binds_VerdictThresholds()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewProfile:VerdictThresholds:RejectOnCriticalCount"] = "2",
                ["ReviewProfile:VerdictThresholds:NeedsWorkOnWarningCount"] = "5",
            })
            .Build();

        var profile = config.GetSection("ReviewProfile").Get<ReviewProfile>()!;

        Assert.AreEqual(2, profile.VerdictThresholds.RejectOnCriticalCount);
        Assert.AreEqual(5, profile.VerdictThresholds.NeedsWorkOnWarningCount);
    }

    [TestMethod]
    public void Config_PartialBinding_UsesDefaults()
    {
        // Only set Temperature — everything else should be default
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ReviewProfile:Temperature"] = "0.5",
            })
            .Build();

        var profile = config.GetSection("ReviewProfile").Get<ReviewProfile>()!;

        Assert.AreEqual(0.5f, profile.Temperature, 0.001f, "Explicitly set value should apply.");
        Assert.AreEqual(16000, profile.MaxOutputTokensBatch, "Unset value should use default.");
        Assert.AreEqual(4000, profile.MaxOutputTokensSingleFile, "Unset value should use default.");
        Assert.AreEqual(2000, profile.MaxOutputTokensVerification, "Unset value should use default.");
        Assert.AreEqual(4000, profile.MaxOutputTokensPrSummary, "Unset value should use default.");
    }

    [TestMethod]
    public void Config_EmptySection_UsesAllDefaults()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { })
            .Build();

        var profile = config.GetSection("ReviewProfile").Get<ReviewProfile>() ?? new ReviewProfile();

        Assert.AreEqual(0.1f, profile.Temperature, 0.001f);
        Assert.AreEqual(16000, profile.MaxOutputTokensBatch);
    }

    // ── AC-3: Service uses ReviewProfile values ────────────────────────

    [TestMethod]
    public void Service_StoresReviewProfile()
    {
        var profile = new ReviewProfile
        {
            Temperature = 0.5f,
            MaxOutputTokensBatch = 20000,
        };

        var service = CreateServiceWithProfile(profile);
        var storedProfile = GetPrivateField<ReviewProfile>(service, "_reviewProfile");

        Assert.AreEqual(0.5f, storedProfile.Temperature, 0.001f);
        Assert.AreEqual(20000, storedProfile.MaxOutputTokensBatch);
    }

    [TestMethod]
    public void Service_DefaultProfile_WhenNullPassed()
    {
        var service = CreateServiceWithProfile(reviewProfile: null);
        var storedProfile = GetPrivateField<ReviewProfile>(service, "_reviewProfile");

        Assert.IsNotNull(storedProfile, "A null ReviewProfile should result in a default instance.");
        Assert.AreEqual(0.1f, storedProfile.Temperature, 0.001f);
        Assert.AreEqual(16000, storedProfile.MaxOutputTokensBatch);
    }

    // ── AC-4: Factory wires ReviewProfile through DI ───────────────────

    [TestMethod]
    public void Factory_PassesReviewProfileToProvider()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Type"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Endpoint"] = "https://fake.openai.azure.com/",
                ["AiProvider:Providers:azure-openai:ApiKey"] = "fake-key",
                ["AiProvider:Providers:azure-openai:Model"] = "gpt-4o",
                ["AiProvider:Providers:azure-openai:Enabled"] = "true",
                ["ReviewProfile:Temperature"] = "0.7",
                ["ReviewProfile:MaxOutputTokensBatch"] = "32000",
                ["ReviewProfile:MaxOutputTokensSingleFile"] = "8000",
                ["ReviewProfile:MaxOutputTokensVerification"] = "4000",
                ["ReviewProfile:MaxOutputTokensPrSummary"] = "6000",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var service = sp.GetRequiredService<ICodeReviewService>();
        Assert.IsInstanceOfType(service, typeof(AzureOpenAiReviewService));

        var profile = GetPrivateField<ReviewProfile>((AzureOpenAiReviewService)service, "_reviewProfile");
        Assert.AreEqual(0.7f, profile.Temperature, 0.001f, "Temperature must flow from config through factory.");
        Assert.AreEqual(32000, profile.MaxOutputTokensBatch, "MaxOutputTokensBatch must flow from config.");
        Assert.AreEqual(8000, profile.MaxOutputTokensSingleFile, "MaxOutputTokensSingleFile must flow from config.");
        Assert.AreEqual(4000, profile.MaxOutputTokensVerification, "MaxOutputTokensVerification must flow from config.");
        Assert.AreEqual(6000, profile.MaxOutputTokensPrSummary, "MaxOutputTokensPrSummary must flow from config.");
    }

    [TestMethod]
    public void Factory_UsesDefaultProfile_WhenNoConfigSection()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:Mode"] = "single",
                ["AiProvider:ActiveProvider"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Type"] = "azure-openai",
                ["AiProvider:Providers:azure-openai:Endpoint"] = "https://fake.openai.azure.com/",
                ["AiProvider:Providers:azure-openai:ApiKey"] = "fake-key",
                ["AiProvider:Providers:azure-openai:Model"] = "gpt-4o",
                ["AiProvider:Providers:azure-openai:Enabled"] = "true",
                // No ReviewProfile section
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var service = (AzureOpenAiReviewService)sp.GetRequiredService<ICodeReviewService>();
        var profile = GetPrivateField<ReviewProfile>(service, "_reviewProfile");

        Assert.AreEqual(0.1f, profile.Temperature, 0.001f, "Missing config should use default temperature.");
        Assert.AreEqual(16000, profile.MaxOutputTokensBatch, "Missing config should use default batch tokens.");
    }

    [TestMethod]
    public void Factory_LegacyFallback_StillPassesReviewProfile()
    {
        // No AiProvider:Providers — triggers legacy path
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AzureOpenAI:Endpoint"] = "https://fake.openai.azure.com/",
                ["AzureOpenAI:ApiKey"] = "fake-key",
                ["AzureOpenAI:DeploymentName"] = "gpt-4o",
                ["ReviewProfile:Temperature"] = "0.2",
                ["ReviewProfile:MaxOutputTokensBatch"] = "12000",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.AddCodeReviewService(config);

        var sp = services.BuildServiceProvider();
        var service = (AzureOpenAiReviewService)sp.GetRequiredService<ICodeReviewService>();
        var profile = GetPrivateField<ReviewProfile>(service, "_reviewProfile");

        Assert.AreEqual(0.2f, profile.Temperature, 0.001f, "Legacy path should also receive ReviewProfile.");
        Assert.AreEqual(12000, profile.MaxOutputTokensBatch, "Legacy path should pass configured batch tokens.");
    }

    // ── AC-5: appsettings.json contains ReviewProfile section ──────────

    [TestMethod]
    public void AppSettingsJson_ContainsReviewProfileSection()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath(Path.GetFullPath(
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                    "src", "HVO.AiCodeReview")))
            .AddJsonFile("appsettings.json")
            .Build();

        var profile = config.GetSection("ReviewProfile").Get<ReviewProfile>();
        Assert.IsNotNull(profile, "appsettings.json must contain a ReviewProfile section.");
        Assert.AreEqual(0.1f, profile.Temperature, 0.001f, "Default temperature in appsettings.json should be 0.1.");
        Assert.AreEqual(16000, profile.MaxOutputTokensBatch);
        Assert.AreEqual(4000, profile.MaxOutputTokensSingleFile);
        Assert.AreEqual(2000, profile.MaxOutputTokensVerification);
        Assert.AreEqual(4000, profile.MaxOutputTokensPrSummary);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static AzureOpenAiReviewService CreateServiceWithProfile(ReviewProfile? reviewProfile)
    {
        var loggerFactory = LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning));
        return new AzureOpenAiReviewService(
            "https://fake.openai.azure.com/",
            "fake-key",
            "gpt-4o",
            customInstructionsPath: null,
            loggerFactory.CreateLogger<AzureOpenAiReviewService>(),
            maxInputLinesPerFile: 5000,
            reviewProfile: reviewProfile);
    }

    private static T GetPrivateField<T>(object instance, string fieldName)
    {
        var field = instance.GetType()
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.IsNotNull(field, $"Private field '{fieldName}' must exist.");
        return (T)field.GetValue(instance)!;
    }
}
