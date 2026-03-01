using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for the dedicated security review pass (Issue #17).
/// Covers: model, FakeCodeReviewService, orchestrator integration (enabled/disabled),
/// and BuildSummaryMarkdown rendering of security findings.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class SecurityAnalysisTests
{
    // ── Model tests ────────────────────────────────────────────────────

    [TestMethod]
    public void SecurityAnalysisResult_DefaultValues_AreCorrect()
    {
        var result = new SecurityAnalysisResult();

        Assert.AreEqual(string.Empty, result.ExecutiveSummary);
        Assert.AreEqual("None", result.OverallRiskLevel);
        Assert.AreEqual(0, result.Findings.Count);
        Assert.IsNull(result.ModelName);
        Assert.IsNull(result.PromptTokens);
        Assert.IsNull(result.CompletionTokens);
        Assert.IsNull(result.TotalTokens);
        Assert.IsNull(result.AiDurationMs);
    }

    [TestMethod]
    public void SecurityFinding_DefaultSeverity_IsCritical()
    {
        var finding = new SecurityFinding();
        Assert.AreEqual("Critical", finding.Severity);
    }

    [TestMethod]
    public void SecurityAnalysisResult_RoundTrips_ViaJson()
    {
        var original = new SecurityAnalysisResult
        {
            ExecutiveSummary = "No critical issues found.",
            OverallRiskLevel = "Low",
            Findings = new List<SecurityFinding>
            {
                new SecurityFinding
                {
                    Severity = "Critical",
                    CweId = "CWE-79",
                    OwaspCategory = "A03:2021 — Injection",
                    Description = "Potential XSS in user input.",
                    FilePath = "src/Controller.cs",
                    LineNumber = 42,
                    Remediation = "Sanitize output.",
                },
            },
            ModelName = "gpt-4o",
            PromptTokens = 500,
            CompletionTokens = 200,
            TotalTokens = 700,
            AiDurationMs = 1234,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(original,
            System.Text.Json.JsonSerializerOptions.Web);
        var deserialized = System.Text.Json.JsonSerializer.Deserialize<SecurityAnalysisResult>(json,
            System.Text.Json.JsonSerializerOptions.Web);

        Assert.IsNotNull(deserialized);
        Assert.AreEqual("No critical issues found.", deserialized.ExecutiveSummary);
        Assert.AreEqual("Low", deserialized.OverallRiskLevel);
        Assert.AreEqual(1, deserialized.Findings.Count);
        Assert.AreEqual("CWE-79", deserialized.Findings[0].CweId);
        Assert.AreEqual("A03:2021 — Injection", deserialized.Findings[0].OwaspCategory);
        Assert.AreEqual("Potential XSS in user input.", deserialized.Findings[0].Description);
        Assert.AreEqual("src/Controller.cs", deserialized.Findings[0].FilePath);
        Assert.AreEqual(42, deserialized.Findings[0].LineNumber);
        Assert.AreEqual("Sanitize output.", deserialized.Findings[0].Remediation);

        // JsonIgnore metrics should not be serialized
        Assert.IsNull(deserialized.ModelName);
        Assert.IsNull(deserialized.PromptTokens);
    }

    // ── FakeCodeReviewService tests ────────────────────────────────────

    [TestMethod]
    public async Task FakeService_GenerateSecurityAnalysis_ReturnsDeterministicResult()
    {
        var fake = new FakeCodeReviewService();
        var prInfo = CreatePrInfo();
        var fileChanges = CreateFileChanges();

        var result = await fake.GenerateSecurityAnalysisAsync(prInfo, fileChanges);

        Assert.IsNotNull(result);
        Assert.AreEqual("Low", result.OverallRiskLevel);
        Assert.AreEqual(1, result.Findings.Count);
        Assert.AreEqual("Critical", result.Findings[0].Severity);
        Assert.AreEqual("CWE-79", result.Findings[0].CweId);
        Assert.AreEqual("fake-model", result.ModelName);
    }

    [TestMethod]
    public async Task FakeService_SecurityAnalysisFactory_OverridesDefault()
    {
        var fake = new FakeCodeReviewService
        {
            SecurityAnalysisFactory = (pr, files, summary) => new SecurityAnalysisResult
            {
                ExecutiveSummary = "Custom factory result",
                OverallRiskLevel = "Critical",
                Findings = new List<SecurityFinding>(),
            },
        };

        var result = await fake.GenerateSecurityAnalysisAsync(CreatePrInfo(), CreateFileChanges());

        Assert.IsNotNull(result);
        Assert.AreEqual("Custom factory result", result.ExecutiveSummary);
        Assert.AreEqual("Critical", result.OverallRiskLevel);
        Assert.AreEqual(0, result.Findings.Count);
    }

    [TestMethod]
    public async Task FakeService_SecurityAnalysisFactory_CanReturnNull()
    {
        var fake = new FakeCodeReviewService
        {
            SecurityAnalysisFactory = (_, _, _) => null,
        };

        var result = await fake.GenerateSecurityAnalysisAsync(CreatePrInfo(), CreateFileChanges());
        Assert.IsNull(result);
    }

    // ── BuildSummaryMarkdown tests ─────────────────────────────────────

    [TestMethod]
    public void BuildSummaryMarkdown_WithSecurityAnalysis_RendersSecuritySection()
    {
        var result = CreateMinimalReviewResult();
        var securityAnalysis = new SecurityAnalysisResult
        {
            ExecutiveSummary = "One critical injection vulnerability found.",
            OverallRiskLevel = "High",
            Findings = new List<SecurityFinding>
            {
                new SecurityFinding
                {
                    Severity = "Critical",
                    CweId = "CWE-89",
                    OwaspCategory = "A03:2021 — Injection",
                    Description = "SQL injection via unsanitized input.",
                    FilePath = "src/Data/UserRepo.cs",
                    LineNumber = 55,
                    Remediation = "Use parameterized queries.",
                },
            },
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, securityAnalysis: securityAnalysis);

        Assert.IsTrue(markdown.Contains("Security Analysis"), "Should contain Security Analysis header");
        Assert.IsTrue(markdown.Contains("One critical injection vulnerability found."), "Should contain executive summary");
        Assert.IsTrue(markdown.Contains("High"), "Should contain risk level");
        Assert.IsTrue(markdown.Contains("CWE-89"), "Should contain CWE ID");
        Assert.IsTrue(markdown.Contains("A03:2021"), "Should contain OWASP category");
        Assert.IsTrue(markdown.Contains("SQL injection via unsanitized input."), "Should contain description");
        Assert.IsTrue(markdown.Contains("UserRepo.cs"), "Should contain file path");
        Assert.IsTrue(markdown.Contains("Use parameterized queries."), "Should contain remediation");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_WithNoFindings_ShowsCleanMessage()
    {
        var result = CreateMinimalReviewResult();
        var securityAnalysis = new SecurityAnalysisResult
        {
            ExecutiveSummary = "No issues detected.",
            OverallRiskLevel = "None",
            Findings = new List<SecurityFinding>(),
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, securityAnalysis: securityAnalysis);

        Assert.IsTrue(markdown.Contains("Security Analysis"), "Should contain Security Analysis header");
        Assert.IsTrue(markdown.Contains("No security vulnerabilities detected"), "Should show clean message");
        Assert.IsFalse(markdown.Contains("rotating_light"), "Should not contain finding marker");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_WithoutSecurityAnalysis_OmitsSection()
    {
        var result = CreateMinimalReviewResult();

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsFalse(markdown.Contains("Security Analysis"), "Should not contain Security Analysis header");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_SecurityFinding_WithoutCweOrOwasp_RendersCleanly()
    {
        var result = CreateMinimalReviewResult();
        var securityAnalysis = new SecurityAnalysisResult
        {
            ExecutiveSummary = "Minor concern found.",
            OverallRiskLevel = "Low",
            Findings = new List<SecurityFinding>
            {
                new SecurityFinding
                {
                    Severity = "Critical",
                    // No CWE or OWASP
                    Description = "Debug mode enabled in production config.",
                    FilePath = "appsettings.json",
                    LineNumber = 0,
                    Remediation = "Set debug = false in production.",
                },
            },
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, securityAnalysis: securityAnalysis);

        Assert.IsTrue(markdown.Contains("Debug mode enabled"), "Should contain finding description");
        Assert.IsFalse(markdown.Contains("CWE-"), "Should not contain CWE prefix when none provided");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_SecurityRiskLevel_ShowsCorrectBadge()
    {
        var result = CreateMinimalReviewResult();

        // Test each risk level badge
        var testCases = new Dictionary<string, string>
        {
            ["Critical"] = "red_circle",
            ["High"] = "orange_circle",
            ["Medium"] = "yellow_circle",
            ["Low"] = "green_circle",
            ["None"] = "white_circle",
        };

        foreach (var (riskLevel, expectedBadge) in testCases)
        {
            var analysis = new SecurityAnalysisResult
            {
                OverallRiskLevel = riskLevel,
                Findings = new List<SecurityFinding>(),
            };

            var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(
                42, result, securityAnalysis: analysis);

            Assert.IsTrue(markdown.Contains(expectedBadge),
                $"Risk level '{riskLevel}' should use badge '{expectedBadge}' but markdown was: {markdown[..200]}");
        }
    }

    // ── ReviewRequest & AiProviderSettings tests ───────────────────────

    [TestMethod]
    public void ReviewRequest_EnableSecurityPass_DefaultsToNull()
    {
        var request = new ReviewRequest();
        Assert.IsNull(request.EnableSecurityPass);
    }

    [TestMethod]
    public void AiProviderSettings_SecurityPassEnabled_DefaultsToFalse()
    {
        var settings = new AiProviderSettings();
        Assert.IsFalse(settings.SecurityPassEnabled);
    }

    // ── ReviewResponse tests ───────────────────────────────────────────

    [TestMethod]
    public void ReviewResponse_SecurityFindingCount_DefaultsToNull()
    {
        var response = new ReviewResponse();
        Assert.IsNull(response.SecurityFindingCount);
    }

    [TestMethod]
    public void ReviewResponse_SecurityFindingCount_SerializedWhenSet()
    {
        var response = new ReviewResponse { SecurityFindingCount = 3, Status = "Reviewed" };
        var json = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.IsTrue(json.Contains("SecurityFindingCount", StringComparison.OrdinalIgnoreCase)
            || json.Contains("securityFindingCount"));
    }

    [TestMethod]
    public void ReviewResponse_SecurityFindingCount_OmittedWhenNull()
    {
        var response = new ReviewResponse { SecurityFindingCount = null, Status = "Reviewed" };
        var json = System.Text.Json.JsonSerializer.Serialize(response);
        Assert.IsFalse(json.Contains("SecurityFindingCount", StringComparison.OrdinalIgnoreCase)
            || json.Contains("securityFindingCount"));
    }

    // ── Prompt content tests ───────────────────────────────────────────

    [TestMethod]
    public void SecuritySystemPrompt_ContainsOwaspCategories()
    {
        var prompt = AzureOpenAiReviewService.GetSecurityAnalysisSystemPrompt();

        Assert.IsTrue(prompt.Contains("OWASP"), "Should mention OWASP");
        Assert.IsTrue(prompt.Contains("A01"), "Should mention A01 Broken Access Control");
        Assert.IsTrue(prompt.Contains("A03"), "Should mention A03 Injection");
        Assert.IsTrue(prompt.Contains("A10"), "Should mention A10 SSRF");
        Assert.IsTrue(prompt.Contains("CWE"), "Should mention CWE references");
        Assert.IsTrue(prompt.Contains("Hardcoded Secrets"), "Should mention secrets detection");
        Assert.IsTrue(prompt.Contains("remediation"), "Should require remediation");
    }

    [TestMethod]
    public void SecuritySystemPrompt_RequiresJsonOutput()
    {
        var prompt = AzureOpenAiReviewService.GetSecurityAnalysisSystemPrompt();

        Assert.IsTrue(prompt.Contains("JSON"), "Should require JSON output");
        Assert.IsTrue(prompt.Contains("executiveSummary"), "Should define executiveSummary field");
        Assert.IsTrue(prompt.Contains("overallRiskLevel"), "Should define overallRiskLevel field");
        Assert.IsTrue(prompt.Contains("findings"), "Should define findings array");
    }

    [TestMethod]
    public void SecurityUserPrompt_IncludesPrContext()
    {
        var prInfo = CreatePrInfo();
        var fileChanges = CreateFileChanges();

        var prompt = AzureOpenAiReviewService.BuildSecurityAnalysisUserPrompt(prInfo, fileChanges, null);

        Assert.IsTrue(prompt.Contains("Security Analysis"), "Should mention security analysis");
        Assert.IsTrue(prompt.Contains("#42"), "Should include PR number");
        Assert.IsTrue(prompt.Contains("Test PR"), "Should include PR title");
        Assert.IsTrue(prompt.Contains("src/MyFile.cs"), "Should include file paths");
    }

    [TestMethod]
    public void SecurityUserPrompt_IncludesPrSummaryContext_WhenAvailable()
    {
        var prInfo = CreatePrInfo();
        var fileChanges = CreateFileChanges();
        var prSummary = new PrSummaryResult
        {
            ArchitecturalImpact = "Adds new data access layer",
            RiskAreas = new List<RiskArea>
            {
                new RiskArea { Area = "Security", Reason = "New auth flow" },
            },
        };

        var prompt = AzureOpenAiReviewService.BuildSecurityAnalysisUserPrompt(prInfo, fileChanges, prSummary);

        Assert.IsTrue(prompt.Contains("PR Summary Context"), "Should include PR summary section");
        Assert.IsTrue(prompt.Contains("Adds new data access layer"), "Should include architectural impact");
        Assert.IsTrue(prompt.Contains("New auth flow"), "Should include risk areas");
    }

    [TestMethod]
    public void SecurityUserPrompt_IncludesFileDiffs()
    {
        var prInfo = CreatePrInfo();
        var fileChanges = new List<FileChange>
        {
            new FileChange
            {
                FilePath = "src/Auth.cs",
                ChangeType = "edit",
                UnifiedDiff = "+var password = Request.Query[\"pwd\"];",
            },
        };

        var prompt = AzureOpenAiReviewService.BuildSecurityAnalysisUserPrompt(prInfo, fileChanges, null);

        Assert.IsTrue(prompt.Contains("password = Request.Query"), "Should include diff content");
        Assert.IsTrue(prompt.Contains("```diff"), "Should wrap diff in code fence");
    }

    [TestMethod]
    public void SecurityUserPrompt_TruncatesLargeDiffs()
    {
        var prInfo = CreatePrInfo();
        var largeDiff = new string('x', 10000);
        var fileChanges = new List<FileChange>
        {
            new FileChange
            {
                FilePath = "src/BigFile.cs",
                ChangeType = "edit",
                UnifiedDiff = largeDiff,
            },
        };

        var prompt = AzureOpenAiReviewService.BuildSecurityAnalysisUserPrompt(prInfo, fileChanges, null);

        Assert.IsTrue(prompt.Contains("truncated"), "Should show truncation notice");
        // Diff content should be truncated to 8000 chars
        Assert.IsTrue(prompt.Length < largeDiff.Length, "Prompt should be shorter than full diff");
    }

    // ── Orchestrator integration tests ─────────────────────────────────

    [TestMethod]
    public async Task Orchestrator_SecurityPassDisabled_ByDefault_NoSecuritySection()
    {
        var ctx = TestServiceBuilder.BuildFullyFake();

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 999,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Standard);

        Assert.IsNotNull(response.Summary);
        Assert.IsFalse(response.Summary.Contains("Security Analysis"),
            "Security section should not appear when security pass is disabled");
        Assert.IsNull(response.SecurityFindingCount,
            "SecurityFindingCount should be null when pass is disabled");
    }

    [TestMethod]
    public async Task Orchestrator_SecurityPassEnabled_ViaParameter_IncludesSecuritySection()
    {
        var ctx = TestServiceBuilder.BuildFullyFake();

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 999,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Standard,
            enableSecurityPass: true);

        Assert.IsNotNull(response.Summary);
        Assert.IsTrue(response.Summary.Contains("Security Analysis"),
            "Security section should appear when security pass is enabled");
        Assert.IsNotNull(response.SecurityFindingCount,
            "SecurityFindingCount should be set when pass is enabled");
        Assert.AreEqual(1, response.SecurityFindingCount,
            "Should reflect the fake service's single finding");
    }

    [TestMethod]
    public async Task Orchestrator_SecurityPassEnabled_WorksWithQuickMode()
    {
        var ctx = TestServiceBuilder.BuildFullyFake();

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 999,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Quick,
            enableSecurityPass: true);

        Assert.IsNotNull(response.Summary);
        Assert.IsTrue(response.Summary.Contains("Security Analysis"),
            "Security section should appear even in Quick mode when enabled");
    }

    [TestMethod]
    public async Task Orchestrator_SecurityPassEnabled_WorksWithDeepMode()
    {
        var ctx = TestServiceBuilder.BuildFullyFake();

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 999,
            simulationOnly: true,
            reviewDepth: ReviewDepth.Deep,
            enableSecurityPass: true);

        Assert.IsNotNull(response.Summary);
        Assert.IsTrue(response.Summary.Contains("Security Analysis"),
            "Security section should appear in Deep mode when enabled");
        // Deep mode should also have Deep Analysis section
        Assert.IsTrue(response.Summary.Contains("Deep Analysis"),
            "Deep mode should still include Deep Analysis section");
    }

    [TestMethod]
    public async Task Orchestrator_SecurityPassExplicitlyDisabled_OverridesGlobalSetting()
    {
        // Even if global setting were enabled, explicit false should disable
        var ctx = TestServiceBuilder.BuildFullyFake();

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 999,
            simulationOnly: true,
            enableSecurityPass: false);

        Assert.IsNotNull(response.Summary);
        Assert.IsFalse(response.Summary.Contains("Security Analysis"),
            "Security section should not appear when explicitly disabled");
    }

    [TestMethod]
    public async Task Orchestrator_SecurityPassWithCustomFactory_UsesFactoryResult()
    {
        var fake = new FakeCodeReviewService
        {
            SecurityAnalysisFactory = (_, _, _) => new SecurityAnalysisResult
            {
                ExecutiveSummary = "Custom security result from factory",
                OverallRiskLevel = "Critical",
                Findings = new List<SecurityFinding>
                {
                    new SecurityFinding
                    {
                        Severity = "Critical",
                        CweId = "CWE-502",
                        Description = "Insecure deserialization",
                        FilePath = "src/Api.cs",
                        LineNumber = 10,
                        Remediation = "Use safe deserializer",
                    },
                    new SecurityFinding
                    {
                        Severity = "Critical",
                        CweId = "CWE-918",
                        Description = "SSRF via user input",
                        FilePath = "src/Proxy.cs",
                        LineNumber = 20,
                        Remediation = "Allowlist URLs",
                    },
                },
            },
        };

        var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fake);

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 999,
            simulationOnly: true,
            enableSecurityPass: true);

        Assert.IsNotNull(response.Summary);
        Assert.IsTrue(response.Summary.Contains("Custom security result from factory"));
        Assert.IsTrue(response.Summary.Contains("CWE-502"));
        Assert.IsTrue(response.Summary.Contains("CWE-918"));
        Assert.AreEqual(2, response.SecurityFindingCount);
    }

    [TestMethod]
    public async Task Orchestrator_SecurityPassReturnsNull_NoSecuritySection()
    {
        var fake = new FakeCodeReviewService
        {
            SecurityAnalysisFactory = (_, _, _) => null,
        };

        var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fake);

        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 999,
            simulationOnly: true,
            enableSecurityPass: true);

        Assert.IsNotNull(response.Summary);
        Assert.IsFalse(response.Summary.Contains("Security Analysis"),
            "Should not render security section when analysis returns null");
        Assert.IsNull(response.SecurityFindingCount);
    }

    // ── ConsensusReviewService delegation test ─────────────────────────

    [TestMethod]
    public void ConsensusReviewService_ImplementsSecurityAnalysis()
    {
        // Verify ConsensusReviewService implements the new interface method
        var type = typeof(ConsensusReviewService);
        var method = type.GetMethod("GenerateSecurityAnalysisAsync");
        Assert.IsNotNull(method, "ConsensusReviewService should implement GenerateSecurityAnalysisAsync");
    }

    // ── Helper methods ─────────────────────────────────────────────────

    private static PullRequestInfo CreatePrInfo() => new()
    {
        PullRequestId = 42,
        Title = "Test PR",
        Description = "Test description",
        CreatedBy = "testuser",
        SourceBranch = "refs/heads/feature/test",
        TargetBranch = "refs/heads/main",
    };

    private static List<FileChange> CreateFileChanges() => new()
    {
        new FileChange
        {
            FilePath = "src/MyFile.cs",
            ChangeType = "edit",
            UnifiedDiff = "+// new line added",
        },
    };

    private static CodeReviewResult CreateMinimalReviewResult() => new()
    {
        Summary = new ReviewSummary
        {
            Verdict = "APPROVED",
            VerdictJustification = "Test verdict",
        },
        InlineComments = new List<InlineComment>(),
        FileReviews = new List<FileReview>(),
    };
}
