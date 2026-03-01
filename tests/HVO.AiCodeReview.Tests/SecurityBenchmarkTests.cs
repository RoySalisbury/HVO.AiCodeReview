using System.Diagnostics;
using System.Text;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeReview.Tests;

/// <summary>
/// Benchmarks the security analysis pass across every deployed model.
///
/// Unlike <see cref="ModelBenchmarkTests"/>, these tests do NOT need a real
/// Azure DevOps PR — they call <see cref="ICodeReviewService.GenerateSecurityAnalysisAsync"/>
/// directly with synthetic code, making them faster and cheaper (single AI call
/// per model, no DevOps API overhead).
///
/// Scoring is based on:
///   - Security findings detected (out of known planted vulnerabilities)
///   - CWE reference accuracy (correct CWE IDs for known issues)
///   - OWASP Top 10 category coverage
///   - Overall risk level appropriateness
///   - Token usage and estimated cost
///   - Latency
///
/// Run the full security benchmark:
///   dotnet test --filter "TestCategory=Benchmark&amp;FullyQualifiedName~SecurityBenchmark"
///
/// Run a single model:
///   dotnet test --filter "FullyQualifiedName~SecurityBenchmark_gpt4o_mini"
/// </summary>
[TestClass]
[TestCategory("Benchmark")]
public class SecurityBenchmarkTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Models to benchmark — same list as ModelBenchmarkTests
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly (string Model, string ProviderKey)[] BenchmarkModels =
    {
        ("gpt-4o-mini",  "azure-openai-mini"),
        ("gpt-4o",       "azure-openai"),
        ("o3-mini",      "azure-openai-o3-mini"),
        ("o4-mini",      "azure-openai-o4-mini"),
        ("gpt-5-mini",   "azure-openai-gpt5-mini"),
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  Known vulnerability families planted in the test code
    //  Each family has: display name, expected CWE IDs, search terms
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly VulnerabilityFamily[] ExpectedFamilies =
    {
        new("Hardcoded Credentials",
            new[] { "CWE-798", "CWE-259" },
            new[] { "hardcoded", "credential", "secret", "password", "api key", "cwe-798", "cwe-259" }),

        new("SQL Injection",
            new[] { "CWE-89" },
            new[] { "sql injection", "injection", "concatenat", "parameterized", "cwe-89" }),

        new("Path Traversal",
            new[] { "CWE-22" },
            new[] { "path traversal", "traversal", "directory", "sanitiz", "cwe-22" }),

        new("Sensitive Data Logging",
            new[] { "CWE-532", "CWE-200" },
            new[] { "logging", "log", "sensitive", "password", "cwe-532", "cwe-200" }),

        new("Hardcoded API Key",
            new[] { "CWE-798" },
            new[] { "api key", "api_key", "hardcoded", "secret", "cwe-798" }),
    };

    private record VulnerabilityFamily(string Name, string[] ExpectedCweIds, string[] SearchTerms);

    // ═══════════════════════════════════════════════════════════════════════
    //  Benchmark result record
    // ═══════════════════════════════════════════════════════════════════════

    private record SecurityBenchmarkResult(
        string Model,
        TimeSpan Duration,
        string Status,
        string OverallRiskLevel,
        int FindingCount,
        int FamiliesDetected,
        int TotalFamilies,
        int CorrectCweCount,
        int FindingsWithCwe,
        int FindingsWithOwasp,
        int FindingsWithRemediation,
        int PromptTokens,
        int CompletionTokens,
        int TotalTokens,
        long AiDurationMs,
        decimal? EstimatedCost,
        string? ErrorMessage);

    // ═══════════════════════════════════════════════════════════════════════
    //  Per-model test methods
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(180_000)]
    public async Task SecurityBenchmark_gpt4o_mini()
        => await RunSingleBenchmark("gpt-4o-mini", "azure-openai-mini");

    [TestMethod]
    [Timeout(180_000)]
    public async Task SecurityBenchmark_gpt4o()
        => await RunSingleBenchmark("gpt-4o", "azure-openai");

    [TestMethod]
    [Timeout(180_000)]
    public async Task SecurityBenchmark_o3_mini()
        => await RunSingleBenchmark("o3-mini", "azure-openai-o3-mini");

    [TestMethod]
    [Timeout(180_000)]
    public async Task SecurityBenchmark_o4_mini()
        => await RunSingleBenchmark("o4-mini", "azure-openai-o4-mini");

    [TestMethod]
    [Timeout(180_000)]
    public async Task SecurityBenchmark_gpt5_mini()
        => await RunSingleBenchmark("gpt-5-mini", "azure-openai-gpt5-mini");

    // ═══════════════════════════════════════════════════════════════════════
    //  All-models comparison
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(1_800_000)] // 30 min — 5 models sequentially
    public async Task SecurityBenchmark_AllModels()
    {
        Console.WriteLine();
        Console.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║  SECURITY BENCHMARK: All models                              ║");
        Console.WriteLine("╚═══════════════════════════════════════════════════════════════╝");

        var results = new List<SecurityBenchmarkResult>();

        foreach (var (model, providerKey) in BenchmarkModels)
        {
            Console.WriteLine();
            Console.WriteLine($"═══ Starting security benchmark for {model} ═══");

            try
            {
                var result = await RunBenchmark(model, providerKey, printDetails: false);
                results.Add(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {model} FAILED: {ex.Message}");
                results.Add(new SecurityBenchmarkResult(
                    model, TimeSpan.Zero, "FAILED", "N/A",
                    0, 0, ExpectedFamilies.Length, 0, 0, 0, 0,
                    0, 0, 0, 0, null, ex.Message));
            }

            // Small delay between models to avoid rate-limit collisions
            await Task.Delay(5_000);
        }

        // ── Print comparison tables ─────────────────────────────────────
        Console.WriteLine();
        PrintComparisonTable(results);
        PrintQualityScoreboard(results);
        PrintMarkdownTable(results);

        Assert.IsTrue(results.Any(r => r.Status != "FAILED"),
            "At least one model should complete successfully.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Core benchmark runner
    // ═══════════════════════════════════════════════════════════════════════

    private async Task RunSingleBenchmark(string model, string providerKey)
    {
        var result = await RunBenchmark(model, providerKey, printDetails: true);

        // Basic sanity — model should detect at least something
        Assert.AreNotEqual("FAILED", result.Status,
            $"Benchmark for {model} should not fail: {result.ErrorMessage}");

        Assert.IsTrue(result.FindingCount > 0,
            $"{model} should detect at least one security finding.");
    }

    private static async Task<SecurityBenchmarkResult> RunBenchmark(
        string model, string providerKey, bool printDetails = true)
    {
        var sw = Stopwatch.StartNew();

        if (printDetails)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine($"  Security Benchmark: {model} (provider: {providerKey})");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }

        // Route the security pass (and all passes) to the target model
        var config = new ConfigurationBuilder()
            .AddConfiguration(TestServiceBuilder.LoadConfig())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:DepthModels:Quick"] = providerKey,
                ["AiProvider:DepthModels:Standard"] = providerKey,
                ["AiProvider:DepthModels:Deep"] = providerKey,
                ["AiProvider:SecurityPassEnabled"] = "true",
            })
            .Build();

        // Real AI + Fake DevOps — no DevOps repo or PR needed
        await using var ctx = TestServiceBuilder.BuildWithRealAiAndFakeDevOps(config: config);

        var aiService = ctx.ServiceProvider.GetRequiredService<ICodeReviewService>();

        if (printDetails)
            Console.WriteLine($"  Model: {aiService.ModelName}");

        // Synthetic PR info
        var prInfo = new PullRequestInfo
        {
            PullRequestId = 1,
            Title = "Add user repository with database access",
            Description = "Implements user lookup, file access, and authentication",
            CreatedBy = "dev@example.com",
            SourceBranch = "refs/heads/feature/user-repo",
            TargetBranch = "refs/heads/main",
        };

        // Known-bad code with planted security vulnerabilities
        var fileChanges = new List<FileChange>
        {
            new()
            {
                FilePath = "src/SecurityFlaws.cs",
                ChangeType = "add",
                UnifiedDiff = KnownBadCode.SecurityIssues,
                ModifiedContent = KnownBadCode.SecurityIssues,
            },
        };

        if (printDetails)
            Console.WriteLine($"  [{sw.Elapsed:mm\\:ss\\.ff}] Calling GenerateSecurityAnalysisAsync...");

        // ── Run the security analysis ───────────────────────────────────
        SecurityAnalysisResult? result;
        try
        {
            result = await aiService.GenerateSecurityAnalysisAsync(prInfo, fileChanges);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ✗ FAILED after {sw.Elapsed:mm\\:ss\\.ff}: {ex.Message}");
            return new SecurityBenchmarkResult(
                model, sw.Elapsed, "FAILED", "N/A",
                0, 0, ExpectedFamilies.Length, 0, 0, 0, 0,
                0, 0, 0, 0, null, ex.Message);
        }

        sw.Stop();

        if (result is null)
        {
            Console.WriteLine($"  ✗ AI returned null after {sw.Elapsed:mm\\:ss\\.ff}");
            return new SecurityBenchmarkResult(
                model, sw.Elapsed, "NULL", "N/A",
                0, 0, ExpectedFamilies.Length, 0, 0, 0, 0,
                0, 0, 0, 0, null, "AI returned null");
        }

        // ── Score: vulnerability family detection ───────────────────────
        var allText = string.Join(" ", result.Findings.Select(f =>
            $"{f.Description} {f.CweId} {f.OwaspCategory} {f.Remediation}")).ToLowerInvariant();

        int familiesDetected = 0;
        int correctCweCount = 0;

        foreach (var family in ExpectedFamilies)
        {
            bool detected = family.SearchTerms.Any(t => allText.Contains(t));
            if (detected) familiesDetected++;

            // Check if any finding has the expected CWE ID
            foreach (var expectedCwe in family.ExpectedCweIds)
            {
                if (result.Findings.Any(f =>
                    !string.IsNullOrEmpty(f.CweId) &&
                    f.CweId.Equals(expectedCwe, StringComparison.OrdinalIgnoreCase)))
                {
                    correctCweCount++;
                    break; // one correct CWE per family is enough
                }
            }
        }

        // ── Score: quality metrics ──────────────────────────────────────
        int findingsWithCwe = result.Findings.Count(f => !string.IsNullOrEmpty(f.CweId));
        int findingsWithOwasp = result.Findings.Count(f => !string.IsNullOrEmpty(f.OwaspCategory));
        int findingsWithRemediation = result.Findings.Count(f => !string.IsNullOrEmpty(f.Remediation));

        // ── Cost estimation ─────────────────────────────────────────────
        var adapterResolver = new ModelAdapterResolver(
            NullLogger<ModelAdapterResolver>.Instance);
        var adapter = adapterResolver.Resolve(model);
        var promptTok = result.PromptTokens ?? 0;
        var complTok = result.CompletionTokens ?? 0;
        var cost = adapter.CalculateCost(promptTok, complTok);

        var benchResult = new SecurityBenchmarkResult(
            Model: model,
            Duration: sw.Elapsed,
            Status: "OK",
            OverallRiskLevel: result.OverallRiskLevel,
            FindingCount: result.Findings.Count,
            FamiliesDetected: familiesDetected,
            TotalFamilies: ExpectedFamilies.Length,
            CorrectCweCount: correctCweCount,
            FindingsWithCwe: findingsWithCwe,
            FindingsWithOwasp: findingsWithOwasp,
            FindingsWithRemediation: findingsWithRemediation,
            PromptTokens: promptTok,
            CompletionTokens: complTok,
            TotalTokens: result.TotalTokens ?? 0,
            AiDurationMs: result.AiDurationMs ?? 0,
            EstimatedCost: cost,
            ErrorMessage: null);

        if (printDetails)
            PrintSingleResult(benchResult, result);

        return benchResult;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Output formatting
    // ═══════════════════════════════════════════════════════════════════════

    private static void PrintSingleResult(SecurityBenchmarkResult bench, SecurityAnalysisResult full)
    {
        Console.WriteLine();
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        Console.WriteLine($"  Model:                {bench.Model}");
        Console.WriteLine($"  Time:                 {bench.Duration:mm\\:ss\\.ff}");
        Console.WriteLine($"  Overall Risk:         {bench.OverallRiskLevel}");
        Console.WriteLine($"  Findings:             {bench.FindingCount}");
        Console.WriteLine($"  Families Detected:    {bench.FamiliesDetected}/{bench.TotalFamilies}");
        Console.WriteLine($"  Correct CWE IDs:     {bench.CorrectCweCount}/{bench.TotalFamilies}");
        Console.WriteLine($"  Findings with CWE:   {bench.FindingsWithCwe}/{bench.FindingCount}");
        Console.WriteLine($"  Findings with OWASP: {bench.FindingsWithOwasp}/{bench.FindingCount}");
        Console.WriteLine($"  With Remediation:     {bench.FindingsWithRemediation}/{bench.FindingCount}");
        Console.WriteLine($"  Tokens:               {bench.PromptTokens:N0} prompt + {bench.CompletionTokens:N0} completion = {bench.TotalTokens:N0} total");
        Console.WriteLine($"  AI Time:              {bench.AiDurationMs:N0}ms");
        Console.WriteLine($"  Est. Cost:            {(bench.EstimatedCost.HasValue ? $"${bench.EstimatedCost.Value:F6}" : "N/A")}");
        Console.WriteLine("───────────────────────────────────────────────────────────────");

        Console.WriteLine();
        Console.WriteLine($"  Executive Summary:");
        Console.WriteLine($"  {full.ExecutiveSummary[..Math.Min(full.ExecutiveSummary.Length, 500)]}");

        if (full.Findings.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Findings ({full.Findings.Count}):");
            foreach (var f in full.Findings)
            {
                var cwe = !string.IsNullOrEmpty(f.CweId) ? $"[{f.CweId}] " : "";
                var owasp = !string.IsNullOrEmpty(f.OwaspCategory) ? $" ({f.OwaspCategory})" : "";
                Console.WriteLine($"    {cwe}{f.Description}{owasp}");
                Console.WriteLine($"      File: {f.FilePath}:{f.LineNumber}");
                Console.WriteLine($"      Fix:  {f.Remediation[..Math.Min(f.Remediation.Length, 150)]}");
            }
        }
        Console.WriteLine();
    }

    private static void PrintComparisonTable(List<SecurityBenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  SECURITY BENCHMARK COMPARISON");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine();

        Console.WriteLine($"  {"Model",-15} {"Time",-10} {"Risk",-10} {"Finds",5} {"Fams",4}/{ExpectedFamilies.Length} {"CWE✓",4} {"CWE%",5} {"OWASP%",6} {"Remed%",6}  {"Prompt",8} {"Compl",8} {"Total",8}  {"AI ms",7}  {"Cost",9}");
        Console.WriteLine($"  {Dashes(15)} {Dashes(10)} {Dashes(10)} {Dashes(5)} {Dashes(5)} {Dashes(4)} {Dashes(5)} {Dashes(6)} {Dashes(6)}  {Dashes(8)} {Dashes(8)} {Dashes(8)}  {Dashes(7)}  {Dashes(9)}");

        foreach (var r in results)
        {
            var time = r.Status is "FAILED" or "NULL" ? r.Status : r.Duration.ToString(@"mm\:ss\.ff");
            var costStr = r.EstimatedCost.HasValue ? $"${r.EstimatedCost.Value:F4}" : "N/A";
            var cwePct = r.FindingCount > 0 ? $"{100 * r.FindingsWithCwe / r.FindingCount}%" : "—";
            var owaspPct = r.FindingCount > 0 ? $"{100 * r.FindingsWithOwasp / r.FindingCount}%" : "—";
            var remedPct = r.FindingCount > 0 ? $"{100 * r.FindingsWithRemediation / r.FindingCount}%" : "—";

            Console.WriteLine($"  {r.Model,-15} {time,-10} {r.OverallRiskLevel,-10} {r.FindingCount,5} {r.FamiliesDetected,4}/{r.TotalFamilies} {r.CorrectCweCount,4} {cwePct,5} {owaspPct,6} {remedPct,6}  {r.PromptTokens,8:N0} {r.CompletionTokens,8:N0} {r.TotalTokens,8:N0}  {r.AiDurationMs,7:N0}  {costStr,9}");
        }

        Console.WriteLine();
    }

    private static void PrintQualityScoreboard(List<SecurityBenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("  QUALITY SCOREBOARD");
        Console.WriteLine($"  {Dashes(60)}");
        Console.WriteLine();

        // Composite score: weighted sum
        //   - Family detection:  40 pts (8 pts per family)
        //   - Correct CWE IDs:   20 pts (4 pts per family)
        //   - Risk level:        15 pts (Critical/High on known-bad code = full marks)
        //   - CWE coverage:      10 pts (% of findings with CWE)
        //   - OWASP coverage:    10 pts (% of findings with OWASP)
        //   - Remediation:        5 pts (% of findings with remediation)
        //  Total possible:      100 pts

        Console.WriteLine($"  {"Model",-15}  {"Fams",5}  {"CWE✓",5}  {"Risk",5}  {"CWE%",5}  {"OWASP%",6}  {"Remed",5}  {"TOTAL",6}  {"Grade",5}");
        Console.WriteLine($"  {Dashes(15)}  {Dashes(5)}  {Dashes(5)}  {Dashes(5)}  {Dashes(5)}  {Dashes(6)}  {Dashes(5)}  {Dashes(6)}  {Dashes(5)}");

        foreach (var r in results.Where(r => r.Status == "OK"))
        {
            // Family detection (0-40)
            double famScore = 40.0 * r.FamiliesDetected / r.TotalFamilies;

            // Correct CWE IDs (0-20)
            double cweAccuracy = 20.0 * r.CorrectCweCount / r.TotalFamilies;

            // Risk level appropriateness (0-15)
            double riskScore = r.OverallRiskLevel switch
            {
                "Critical" => 15,
                "High" => 13,
                "Medium" => 8,
                "Low" => 3,
                "None" => 0,
                _ => 0
            };

            // CWE coverage % (0-10)
            double cweCoverage = r.FindingCount > 0
                ? 10.0 * r.FindingsWithCwe / r.FindingCount
                : 0;

            // OWASP coverage % (0-10)
            double owaspCoverage = r.FindingCount > 0
                ? 10.0 * r.FindingsWithOwasp / r.FindingCount
                : 0;

            // Remediation coverage % (0-5)
            double remedScore = r.FindingCount > 0
                ? 5.0 * r.FindingsWithRemediation / r.FindingCount
                : 0;

            double total = famScore + cweAccuracy + riskScore + cweCoverage + owaspCoverage + remedScore;

            var grade = total switch
            {
                >= 90 => "A+",
                >= 80 => "A",
                >= 70 => "B+",
                >= 60 => "B",
                >= 50 => "C",
                >= 40 => "D",
                _ => "F"
            };

            Console.WriteLine($"  {r.Model,-15}  {famScore,5:F1}  {cweAccuracy,5:F1}  {riskScore,5:F1}  {cweCoverage,5:F1}  {owaspCoverage,6:F1}  {remedScore,5:F1}  {total,6:F1}  {grade,5}");
        }

        // Visual bar chart
        Console.WriteLine();
        Console.WriteLine("  Detection Coverage (families found / 5 known):");
        Console.WriteLine($"  {Dashes(50)}");
        foreach (var r in results.Where(r => r.Status == "OK"))
        {
            var filled = r.FamiliesDetected;
            var empty = r.TotalFamilies - filled;
            var bar = new string('█', filled) + new string('░', empty);
            Console.WriteLine($"  {r.Model,-15} [{bar}] {filled}/{r.TotalFamilies}  ({r.Duration:mm\\:ss})");
        }

        Console.WriteLine();
    }

    private static void PrintMarkdownTable(List<SecurityBenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  MARKDOWN TABLE (copy/paste for docs)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine();

        var sb = new StringBuilder();
        sb.AppendLine("| Model | Time | Risk | Findings | Families | CWE Accuracy | CWE% | OWASP% | Remediation% | Prompt Tok | Compl Tok | Total Tok | AI ms | Est. Cost |");
        sb.AppendLine("|-------|------|------|----------|----------|-------------|------|--------|-------------|------------|-----------|-----------|-------|-----------|");

        foreach (var r in results)
        {
            var time = r.Status is "FAILED" or "NULL" ? r.Status : r.Duration.ToString(@"mm\:ss");
            var cost = r.EstimatedCost.HasValue ? $"${r.EstimatedCost.Value:F4}" : "N/A";
            var cwePct = r.FindingCount > 0 ? $"{100 * r.FindingsWithCwe / r.FindingCount}%" : "—";
            var owaspPct = r.FindingCount > 0 ? $"{100 * r.FindingsWithOwasp / r.FindingCount}%" : "—";
            var remedPct = r.FindingCount > 0 ? $"{100 * r.FindingsWithRemediation / r.FindingCount}%" : "—";

            sb.AppendLine($"| {r.Model} | {time} | {r.OverallRiskLevel} | {r.FindingCount} | {r.FamiliesDetected}/{r.TotalFamilies} | {r.CorrectCweCount}/{r.TotalFamilies} | {cwePct} | {owaspPct} | {remedPct} | {r.PromptTokens:N0} | {r.CompletionTokens:N0} | {r.TotalTokens:N0} | {r.AiDurationMs:N0} | {cost} |");
        }

        Console.WriteLine(sb.ToString());
    }

    private static string Dashes(int n) => new('─', n);
}
