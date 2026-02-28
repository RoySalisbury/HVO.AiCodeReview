using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeReview.Tests;

/// <summary>
/// Benchmark tests that run the same PR review across every deployed model
/// and compare time, quality, and output.
///
/// Each test creates a disposable PR with known-bad code (security issues +
/// bugs), runs a REAL review via the specified model, and records:
///   - Wall-clock time (total, Pass 1, Pass 2)
///   - Issue counts (errors, warnings, info)
///   - Inline comment count and severity distribution
///   - Verdict and vote
///   - Summary quality (length, presence of key terms)
///
/// Run the full benchmark suite:
///   dotnet test --filter "TestCategory=Benchmark" --logger "console;verbosity=detailed"
///
/// Run a single model:
///   dotnet test --filter "FullyQualifiedName~Benchmark_gpt4o_mini"
/// </summary>
[TestClass]
[TestCategory("Benchmark")]
public class ModelBenchmarkTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Models to benchmark — (display name, provider key in appsettings)
    // ═══════════════════════════════════════════════════════════════════════

    private static readonly (string Model, string ProviderKey)[] BenchmarkModels =
    {
        ("gpt-4o-mini",  "azure-openai-mini"),       // Quick depth — 500K TPM
        ("gpt-4o",       "azure-openai"),             // Current default — 450K TPM
        ("o3-mini",      "azure-openai-o3-mini"),     // Standard depth — 1M TPM (reasoning)
        ("o4-mini",      "azure-openai-o4-mini"),     // Deep depth — 150K TPM (reasoning)
        ("gpt-5-mini",   "azure-openai-gpt5-mini"),   // Challenger for Deep — 500K TPM
    };

    // ═══════════════════════════════════════════════════════════════════════
    //  Known bad code for the benchmark PR (security + bugs combined)
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Combined security + bug issues in a single file.
    /// Same code for every model so results are directly comparable.
    /// </summary>
    private const string BenchmarkCode = @"
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BenchmarkApp;

public class OrderService
{
    // ISSUE 1: Hardcoded connection string with credentials
    private const string ConnStr =
        ""Server=prod-db.company.com;Database=Orders;User Id=sa;Password=P@ssw0rd!;"";

    // ISSUE 2: Hardcoded API key
    private readonly string _apiKey = ""sk-live-abcdef1234567890abcdef1234567890"";

    // ISSUE 3: SQL injection via string concatenation
    public decimal GetOrderTotal(string orderId)
    {
        using var conn = new SqlConnection(ConnStr);
        conn.Open();
        var sql = ""SELECT Total FROM Orders WHERE OrderId = '"" + orderId + ""'"";
        using var cmd = new SqlCommand(sql, conn);
        return (decimal)(cmd.ExecuteScalar() ?? 0m);
    }

    // ISSUE 4: Path traversal — unsanitised user input
    public byte[] GetInvoice(string fileName)
    {
        var path = Path.Combine(""/data/invoices"", fileName);
        return File.ReadAllBytes(path);
    }

    // ISSUE 5: Null dereference — FirstOrDefault can return null
    public string GetTopCustomer(List<string> customers)
    {
        var top = customers.FirstOrDefault();
        return top.ToUpper();  // NRE if list is empty
    }

    // ISSUE 6: HttpClient in a loop — socket exhaustion
    public async Task<List<string>> FetchPricesAsync(IEnumerable<string> urls)
    {
        var results = new List<string>();
        foreach (var url in urls)
        {
            var client = new HttpClient();
            results.Add(await client.GetStringAsync(url));
        }
        return results;
    }

    // ISSUE 7: Swallowed exception
    public int ParseQuantity(string input)
    {
        try { return int.Parse(input); }
        catch (Exception) { return 0; }
    }

    // ISSUE 8: Logging sensitive data
    public bool Login(string user, string password)
    {
        Console.WriteLine($""Auth attempt: user={user} pass={password}"");
        return user == ""admin"" && password == ""admin"";
    }

    // ISSUE 9: Async void — fire-and-forget loses exceptions
    public async void SendNotificationAsync(string email, string message)
    {
        using var client = new HttpClient();
        var body = string.Format(""{{0}}: {1}"", email, message);
        await client.PostAsync(""https://api.notify.com/send"",
            new StringContent(body));
    }

    // ISSUE 10: Double dispose / use-after-dispose
    public string ReadConfig()
    {
        var reader = new StreamReader(""/etc/app/config.json"");
        var content = reader.ReadToEnd();
        reader.Dispose();
        reader.Dispose();  // Double dispose
        return content;
    }
}
";

    // ═══════════════════════════════════════════════════════════════════════
    //  Benchmark result record
    // ═══════════════════════════════════════════════════════════════════════

    private record BenchmarkResult(
        string Model,
        TimeSpan Duration,
        string Status,
        string? Verdict,
        int? Vote,
        int IssueCount,
        int ErrorCount,
        int WarningCount,
        int InfoCount,
        int InlineCommentCount,
        int SummaryLength,
        string? Recommendation,
        string? ErrorMessage,
        int PromptTokens = 0,
        int CompletionTokens = 0,
        int TotalTokens = 0,
        long AiDurationMs = 0,
        decimal? EstimatedCost = null);

    // ═══════════════════════════════════════════════════════════════════════
    //  Individual model benchmark tests (Standard depth)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(600_000)]
    public async Task Benchmark_gpt4o_mini() => await RunBenchmark("gpt-4o-mini", "azure-openai-mini");

    [TestMethod]
    [Timeout(600_000)]
    public async Task Benchmark_gpt4o() => await RunBenchmark("gpt-4o", "azure-openai");

    [TestMethod]
    [Timeout(600_000)]
    public async Task Benchmark_o3_mini() => await RunBenchmark("o3-mini", "azure-openai-o3-mini");

    [TestMethod]
    [Timeout(600_000)]
    public async Task Benchmark_o4_mini() => await RunBenchmark("o4-mini", "azure-openai-o4-mini");

    [TestMethod]
    [Timeout(600_000)]
    public async Task Benchmark_gpt5_mini() => await RunBenchmark("gpt-5-mini", "azure-openai-gpt5-mini");

    // ═══════════════════════════════════════════════════════════════════════
    //  Full comparison — runs ALL models sequentially, prints table
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(3_600_000)] // 60 min — running 5 models sequentially
    public async Task Benchmark_AllModels_Standard()
        => await RunAllModelsAtDepth(ReviewDepth.Standard);

    [TestMethod]
    [Timeout(3_600_000)]
    public async Task Benchmark_AllModels_Quick()
        => await RunAllModelsAtDepth(ReviewDepth.Quick);

    [TestMethod]
    [Timeout(3_600_000)]
    public async Task Benchmark_AllModels_Deep()
        => await RunAllModelsAtDepth(ReviewDepth.Deep);

    private async Task RunAllModelsAtDepth(ReviewDepth depth)
    {
        Console.WriteLine();
        Console.WriteLine($"╔═══════════════════════════════════════════════════════════════╗");
        Console.WriteLine($"║  BENCHMARK: All models at {depth} depth");
        Console.WriteLine($"╚═══════════════════════════════════════════════════════════════╝");

        var results = new List<BenchmarkResult>();

        foreach (var (model, providerKey) in BenchmarkModels)
        {
            Console.WriteLine();
            Console.WriteLine($"═══ Starting {depth} benchmark for {model} ═══");

            try
            {
                var result = await RunBenchmark(model, providerKey, depth: depth, printDetails: false);
                results.Add(result);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  ✗ {model} FAILED: {ex.Message}");
                results.Add(new BenchmarkResult(
                    model, TimeSpan.Zero, "FAILED", null, null,
                    0, 0, 0, 0, 0, 0, null, ex.Message));
            }

            // Small delay between models to avoid rate-limit pressure
            await Task.Delay(5000);
        }

        // ── Print comparison table ──────────────────────────────────────
        Console.WriteLine();
        Console.WriteLine($"  ▶ Depth: {depth}");
        PrintComparisonTable(results);
        PrintMarkdownTable(results);

        // At least one model should have succeeded
        Assert.IsTrue(results.Any(r => r.Status != "FAILED"),
            "At least one model should complete successfully.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Core benchmark runner
    // ═══════════════════════════════════════════════════════════════════════

    private static async Task<BenchmarkResult> RunBenchmark(
        string model, string providerKey,
        ReviewDepth depth = ReviewDepth.Standard,
        bool printDetails = true)
    {
        var sw = Stopwatch.StartNew();

        if (printDetails)
        {
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
            Console.WriteLine($"  Benchmark: {model} (provider: {providerKey}, depth: {depth})");
            Console.WriteLine("═══════════════════════════════════════════════════════════════");
        }

        // Build config that routes ALL depths to the target provider.
        // This ensures the orchestrator always uses the model we want to benchmark,
        // regardless of the depth level passed to ExecuteReviewAsync.
        var config = new ConfigurationBuilder()
            .AddConfiguration(TestServiceBuilder.LoadConfig())
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiProvider:DepthModels:Quick"] = providerKey,
                ["AiProvider:DepthModels:Standard"] = providerKey,
                ["AiProvider:DepthModels:Deep"] = providerKey,
            })
            .Build();

        await using var ctx = TestServiceBuilder.BuildWithRealAi(config: config);

        // Create a disposable PR with known-bad code
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;

        if (printDetails)
            Console.WriteLine($"  PR #{prId} in {repo} — pushing benchmark code...");

        // Push the benchmark code
        await pr.PushNewCommitAsync("OrderService.cs", BenchmarkCode);
        await Task.Delay(3000); // let ADO index the change

        // Activate the PR (move out of draft)
        await pr.SetDraftStatusAsync(false);
        await Task.Delay(2000);

        // Pick the right strategy for the depth level
        var strategy = depth == ReviewDepth.Quick
            ? ReviewStrategy.FileByFile   // Quick skips Pass 2 anyway
            : ReviewStrategy.FileByFile;

        if (printDetails)
            Console.WriteLine($"  [{sw.Elapsed:mm\\:ss\\.ff}] Starting AI review with {model} at {depth} depth...");

        // Run the review
        ReviewResponse result;
        try
        {
            result = await ctx.Orchestrator.ExecuteReviewAsync(
                ctx.Project, repo, prId,
                reviewDepth: depth,
                reviewStrategy: strategy);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Console.WriteLine($"  ✗ Review FAILED after {sw.Elapsed:mm\\:ss\\.ff}: {ex.Message}");
            return new BenchmarkResult(
                model, sw.Elapsed, "FAILED", null, null,
                0, 0, 0, 0, 0, 0, null, ex.Message);
        }

        sw.Stop();

        // ── Calculate estimated cost from model adapter pricing ─────────
        var adapterResolver = new ModelAdapterResolver(
            NullLogger<ModelAdapterResolver>.Instance);
        var adapter = adapterResolver.Resolve(model);
        var promptTok = result.PromptTokens ?? 0;
        var complTok = result.CompletionTokens ?? 0;
        var cost = adapter.CalculateCost(promptTok, complTok);

        var benchResult = new BenchmarkResult(
            Model: model,
            Duration: sw.Elapsed,
            Status: result.Status,
            Verdict: result.Verdict,
            Vote: result.Vote,
            IssueCount: result.IssueCount,
            ErrorCount: result.ErrorCount,
            WarningCount: result.WarningCount,
            InfoCount: result.InfoCount,
            InlineCommentCount: result.InlineComments?.Count ?? 0,
            SummaryLength: result.Summary?.Length ?? 0,
            Recommendation: result.Recommendation,
            ErrorMessage: result.ErrorMessage,
            PromptTokens: promptTok,
            CompletionTokens: complTok,
            TotalTokens: result.TotalTokens ?? 0,
            AiDurationMs: result.AiDurationMs ?? 0,
            EstimatedCost: cost);

        if (printDetails)
            PrintSingleResult(benchResult, result);

        return benchResult;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Output formatting
    // ═══════════════════════════════════════════════════════════════════════

    private static void PrintSingleResult(BenchmarkResult bench, ReviewResponse full)
    {
        Console.WriteLine();
        Console.WriteLine("───────────────────────────────────────────────────────────────");
        Console.WriteLine($"  Model:           {bench.Model}");
        Console.WriteLine($"  Time:            {bench.Duration:mm\\:ss\\.ff}");
        Console.WriteLine($"  Status:          {bench.Status}");
        Console.WriteLine($"  Verdict:         {bench.Verdict}");
        Console.WriteLine($"  Vote:            {bench.Vote}");
        Console.WriteLine($"  Recommendation:  {bench.Recommendation}");
        Console.WriteLine($"  Issues:          {bench.IssueCount} (E:{bench.ErrorCount} W:{bench.WarningCount} I:{bench.InfoCount})");
        Console.WriteLine($"  Inline Comments: {bench.InlineCommentCount}");
        Console.WriteLine($"  Summary Length:  {bench.SummaryLength} chars");
        Console.WriteLine($"  Tokens:          {bench.PromptTokens:N0} prompt + {bench.CompletionTokens:N0} completion = {bench.TotalTokens:N0} total");
        Console.WriteLine($"  AI Time:         {bench.AiDurationMs:N0}ms");
        Console.WriteLine($"  Est. Cost:       {(bench.EstimatedCost.HasValue ? $"${bench.EstimatedCost.Value:F6}" : "N/A (no pricing data)")}");
        Console.WriteLine("───────────────────────────────────────────────────────────────");

        if (full.InlineComments?.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine($"  Inline Comments ({full.InlineComments.Count}):");
            foreach (var c in full.InlineComments)
            {
                Console.WriteLine($"    [{c.Severity.ToUpper()}] L{c.StartLine}-{c.EndLine}: {c.Comment[..Math.Min(c.Comment.Length, 150)]}");
            }
        }

        if (!string.IsNullOrEmpty(full.Summary))
        {
            Console.WriteLine();
            Console.WriteLine("  Summary (first 500 chars):");
            Console.WriteLine($"  {full.Summary[..Math.Min(full.Summary.Length, 500)]}");
        }
        Console.WriteLine();
    }

    private static void PrintComparisonTable(List<BenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine("  MODEL BENCHMARK COMPARISON");
        Console.WriteLine("═══════════════════════════════════════════════════════════════════════════════════════════════════");
        Console.WriteLine();

        // Header
        Console.WriteLine($"  {"Model",-15} {"Time",-10} {"Status",-12} {"Vote",4}  {"Issues",6}  {"E",3} {"W",3} {"I",3}  {"Inline",6}  {"Prompt",8} {"Compl",8} {"Total",8}  {"AI ms",7}  {"Cost",9}");
        Console.WriteLine($"  {new string('─', 15)} {new string('─', 10)} {new string('─', 12)} {new string('─', 4)}  {new string('─', 6)}  {new string('─', 3)} {new string('─', 3)} {new string('─', 3)}  {new string('─', 6)}  {new string('─', 8)} {new string('─', 8)} {new string('─', 8)}  {new string('─', 7)}  {new string('─', 9)}");

        foreach (var r in results)
        {
            var time = r.Status == "FAILED" ? "FAILED" : r.Duration.ToString(@"mm\:ss\.ff");
            var voteStr = r.Vote?.ToString() ?? "—";
            var costStr = r.EstimatedCost.HasValue ? $"${r.EstimatedCost.Value:F4}" : "N/A";

            Console.WriteLine($"  {r.Model,-15} {time,-10} {r.Status,-12} {voteStr,4}  {r.IssueCount,6}  {r.ErrorCount,3} {r.WarningCount,3} {r.InfoCount,3}  {r.InlineCommentCount,6}  {r.PromptTokens,8:N0} {r.CompletionTokens,8:N0} {r.TotalTokens,8:N0}  {r.AiDurationMs,7:N0}  {costStr,9}");
        }

        Console.WriteLine();

        // Quality score (simple heuristic: issues found out of 10 known issues)
        Console.WriteLine("  Quality Score (issues found / 10 known issues):");
        Console.WriteLine($"  {new string('─', 40)}");
        foreach (var r in results.Where(r => r.Status != "FAILED"))
        {
            var score = Math.Min(r.IssueCount, 10);
            var bar = new string('█', score) + new string('░', 10 - score);
            Console.WriteLine($"  {r.Model,-15} [{bar}] {score}/10  ({r.Duration:mm\\:ss})");
        }
        Console.WriteLine();
    }

    private static void PrintMarkdownTable(List<BenchmarkResult> results)
    {
        Console.WriteLine();
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  MARKDOWN TABLE (copy/paste for docs)");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine();

        var sb = new StringBuilder();
        sb.AppendLine("| Model | Time | Vote | Issues | Errors | Warnings | Info | Inline | Prompt Tok | Compl Tok | Total Tok | AI ms | Est. Cost |");
        sb.AppendLine("|-------|------|------|--------|--------|----------|------|--------|------------|-----------|-----------|-------|-----------|");

        foreach (var r in results)
        {
            var time = r.Status == "FAILED" ? "FAILED" : r.Duration.ToString(@"mm\:ss");
            var vote = r.Vote?.ToString() ?? "—";
            var cost = r.EstimatedCost.HasValue ? $"${r.EstimatedCost.Value:F4}" : "N/A";

            sb.AppendLine($"| {r.Model} | {time} | {vote} | {r.IssueCount} | {r.ErrorCount} | {r.WarningCount} | {r.InfoCount} | {r.InlineCommentCount} | {r.PromptTokens:N0} | {r.CompletionTokens:N0} | {r.TotalTokens:N0} | {r.AiDurationMs:N0} | {cost} |");
        }

        Console.WriteLine(sb.ToString());
    }
}
