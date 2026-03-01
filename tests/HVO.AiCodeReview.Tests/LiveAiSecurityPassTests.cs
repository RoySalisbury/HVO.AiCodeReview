using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeReview.Tests;

/// <summary>
/// LiveAI test for the dedicated security review pass.
///
/// This test calls <see cref="ICodeReviewService.GenerateSecurityAnalysisAsync"/>
/// directly with known-bad code to validate that the default model produces
/// a well-structured <see cref="SecurityAnalysisResult"/> with real findings.
///
/// Unlike the full orchestrator LiveAI tests, this does NOT require a DevOps
/// repo — it feeds synthetic <see cref="PullRequestInfo"/> and <see cref="FileChange"/>
/// objects directly to the AI service. This makes it faster and cheaper
/// (single AI call, no DevOps API overhead).
///
/// Run selectively:
///   dotnet test --filter "TestCategory=LiveAI&amp;FullyQualifiedName~LiveAiSecurityPass"
/// </summary>
[TestClass]
[TestCategory("LiveAI")]
public class LiveAiSecurityPassTests
{
    [TestMethod]
    [Timeout(120_000)] // 2 min — single AI call
    public async Task SecurityPass_RealAi_DetectsKnownVulnerabilities()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAiAndFakeDevOps();

        // Resolve the real AI service (default model) — DevOps is faked
        var aiService = ctx.ServiceProvider.GetRequiredService<ICodeReviewService>();
        Console.WriteLine($"  [Security-LiveAI] Using model: {aiService.ModelName}");

        var prInfo = new PullRequestInfo
        {
            PullRequestId = 1,
            Title = "Add user repository with database access",
            Description = "Implements user lookup and file access",
            CreatedBy = "dev@example.com",
            SourceBranch = "refs/heads/feature/user-repo",
            TargetBranch = "refs/heads/main",
        };

        // Use KnownBadCode with deliberate security issues:
        // hardcoded secrets, SQL injection, path traversal, sensitive logging
        var fileChanges = new List<FileChange>
        {
            new FileChange
            {
                FilePath = "src/SecurityFlaws.cs",
                ChangeType = "add",
                UnifiedDiff = KnownBadCode.SecurityIssues,
                ModifiedContent = KnownBadCode.SecurityIssues,
            },
        };

        // ── Act ──────────────────────────────────────────────────────────
        var result = await aiService.GenerateSecurityAnalysisAsync(prInfo, fileChanges);

        // ── Assert: Result structure ─────────────────────────────────────
        Assert.IsNotNull(result, "AI should return a SecurityAnalysisResult, not null.");
        Console.WriteLine($"  Executive summary: {result.ExecutiveSummary[..Math.Min(200, result.ExecutiveSummary.Length)]}");
        Console.WriteLine($"  Overall risk: {result.OverallRiskLevel}");
        Console.WriteLine($"  Findings: {result.Findings.Count}");

        Assert.IsFalse(string.IsNullOrWhiteSpace(result.ExecutiveSummary),
            "ExecutiveSummary should not be empty.");

        var validRiskLevels = new[] { "None", "Low", "Medium", "High", "Critical" };
        Assert.IsTrue(validRiskLevels.Contains(result.OverallRiskLevel),
            $"OverallRiskLevel '{result.OverallRiskLevel}' should be one of: {string.Join(", ", validRiskLevels)}");

        // ── Assert: Findings quality ─────────────────────────────────────
        // KnownBadCode.SecurityIssues has hardcoded secrets, SQL injection,
        // path traversal, and sensitive data logging. AI should find at least 2.
        Assert.IsTrue(result.Findings.Count >= 2,
            $"AI should detect at least 2 security findings in known-bad code, found {result.Findings.Count}.");

        // Risk should be at least Medium for code with hardcoded secrets + SQL injection
        var riskOrder = new[] { "None", "Low", "Medium", "High", "Critical" };
        var riskIndex = Array.IndexOf(riskOrder, result.OverallRiskLevel);
        Assert.IsTrue(riskIndex >= 2,
            $"Risk level should be at least Medium for code with hardcoded secrets and SQL injection, was '{result.OverallRiskLevel}'.");

        // ── Assert: Finding structure ────────────────────────────────────
        foreach (var finding in result.Findings)
        {
            // Severity should be enforced as Critical by our code
            Assert.AreEqual("Critical", finding.Severity,
                "All findings should have severity 'Critical' (enforced by caller).");

            Assert.IsFalse(string.IsNullOrWhiteSpace(finding.Description),
                "Finding description should not be empty.");

            Assert.IsFalse(string.IsNullOrWhiteSpace(finding.Remediation),
                $"Finding should include remediation: '{finding.Description}'");

            Console.WriteLine($"  - [{finding.CweId ?? "no-CWE"}] {finding.Description}");
            Console.WriteLine($"    File: {finding.FilePath}:{finding.LineNumber}, Remediation: {finding.Remediation}");
        }

        // ── Assert: Expected vulnerability types detected ────────────────
        var allText = string.Join(" ", result.Findings.Select(f =>
            $"{f.Description} {f.CweId} {f.OwaspCategory} {f.Remediation}")).ToLowerInvariant();

        // At least one of these known vulnerability families should be detected
        var expectedFamilies = new (string Name, string[] Terms)[]
        {
            ("Hardcoded secrets", new[] { "secret", "hardcoded", "credential", "api key", "password", "cwe-798", "cwe-259" }),
            ("SQL injection", new[] { "sql injection", "injection", "parameterized", "cwe-89" }),
            ("Path traversal", new[] { "path traversal", "traversal", "directory", "cwe-22" }),
            ("Sensitive logging", new[] { "logging", "log", "sensitive", "cwe-532", "cwe-200" }),
        };

        int familiesDetected = 0;
        foreach (var (name, terms) in expectedFamilies)
        {
            bool found = terms.Any(t => allText.Contains(t));
            Console.WriteLine($"  Vulnerability family '{name}': {(found ? "DETECTED" : "not detected")}");
            if (found) familiesDetected++;
        }

        Assert.IsTrue(familiesDetected >= 2,
            $"AI should detect at least 2 of 4 vulnerability families (secrets, SQLi, traversal, logging), detected {familiesDetected}.");

        // ── Assert: At least some findings have CWE references ───────────
        var findingsWithCwe = result.Findings.Count(f => !string.IsNullOrWhiteSpace(f.CweId));
        Console.WriteLine($"  Findings with CWE: {findingsWithCwe}/{result.Findings.Count}");
        Assert.IsTrue(findingsWithCwe > 0,
            "At least one finding should include a CWE reference.");

        // ── Assert: Metrics populated ────────────────────────────────────
        Assert.IsNotNull(result.ModelName, "ModelName should be populated.");
        Assert.IsTrue(result.PromptTokens > 0, "PromptTokens should be > 0.");
        Assert.IsTrue(result.CompletionTokens > 0, "CompletionTokens should be > 0.");
        Assert.IsTrue(result.AiDurationMs > 0, "AiDurationMs should be > 0.");

        Console.WriteLine($"  Model: {result.ModelName}");
        Console.WriteLine($"  Tokens: {result.PromptTokens} prompt + {result.CompletionTokens} completion = {result.TotalTokens} total");
        Console.WriteLine($"  Duration: {result.AiDurationMs}ms");
        Console.WriteLine($"  ✓ Security pass LiveAI test passed.");
    }

    [TestMethod]
    [Timeout(120_000)]
    public async Task SecurityPass_RealAi_CleanCode_ReportsLowOrNoRisk()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAiAndFakeDevOps();
        var aiService = ctx.ServiceProvider.GetRequiredService<ICodeReviewService>();
        Console.WriteLine($"  [Security-Clean-LiveAI] Using model: {aiService.ModelName}");

        var prInfo = new PullRequestInfo
        {
            PullRequestId = 2,
            Title = "Add string utility helper",
            Description = "Simple string formatting helper",
            CreatedBy = "dev@example.com",
            SourceBranch = "refs/heads/feature/string-utils",
            TargetBranch = "refs/heads/main",
        };

        // Clean code with no security issues — should get low/no risk
        var cleanCode = @"
namespace Utils;

public static class StringHelper
{
    /// <summary>
    /// Truncates a string to the specified max length, appending ellipsis if truncated.
    /// </summary>
    public static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value)) return value;
        return value.Length <= maxLength ? value : value[..maxLength] + ""..."";
    }

    /// <summary>
    /// Converts a string to title case.
    /// </summary>
    public static string ToTitleCase(string value)
    {
        if (string.IsNullOrEmpty(value)) return value;
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        return culture.TextInfo.ToTitleCase(value.ToLower(culture));
    }
}
";

        var fileChanges = new List<FileChange>
        {
            new FileChange
            {
                FilePath = "src/Utils/StringHelper.cs",
                ChangeType = "add",
                UnifiedDiff = cleanCode,
                ModifiedContent = cleanCode,
            },
        };

        var result = await aiService.GenerateSecurityAnalysisAsync(prInfo, fileChanges);

        Assert.IsNotNull(result, "AI should return a result even for clean code.");
        Console.WriteLine($"  Risk: {result.OverallRiskLevel}, Findings: {result.Findings.Count}");
        Console.WriteLine($"  Summary: {result.ExecutiveSummary}");

        // Clean utility code should be None or Low risk
        var riskOrder = new[] { "None", "Low", "Medium", "High", "Critical" };
        var riskIndex = Array.IndexOf(riskOrder, result.OverallRiskLevel);
        Assert.IsTrue(riskIndex <= 1,
            $"Clean utility code should be None or Low risk, was '{result.OverallRiskLevel}'.");

        // Should have zero or very few findings (AI may occasionally flag minor things)
        Assert.IsTrue(result.Findings.Count <= 1,
            $"Clean code should have 0-1 findings, found {result.Findings.Count}.");

        Console.WriteLine($"  ✓ Clean code security pass test passed.");
    }
}
