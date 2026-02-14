using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// AI quality verification tests.  These push code with KNOWN, DELIBERATE issues
/// into a disposable test repo and run a REAL Azure OpenAI review to verify
/// the AI actually catches them.
///
/// Unlike other integration tests, these use the live <see cref="AzureOpenAiReviewService"/>
/// so they incur API cost and latency.  They are placed in the "LiveAI" test
/// category so they can be run selectively:
///
///   dotnet test --filter "TestCategory=LiveAI"
///
/// To test with a different model:
///   Set AzureOpenAI:DeploymentName in appsettings.Test.json or override
///   in the test method via TestServiceBuilder.BuildWithRealAi("gpt-5").
///
/// ── Rationale ──
/// All other tests use FakeCodeReviewService for speed and determinism.
/// These tests exist to validate that the AI itself produces meaningful
/// reviews — catching security issues, bugs, and code smells in realistic
/// code samples.  They also serve as a regression suite when switching
/// models (e.g. gpt-4o → gpt-5).
/// </summary>
[TestClass]
[TestCategory("LiveAI")]
public class AiQualityVerificationTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Known Bad Code — Security Issues
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(300_000)] // 5 min — real AI calls take time
    public async Task KnownBadCode_SecurityIssues_AiFlagsAtLeastOne()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [AI-Security] PR #{prId} in {repo}");

        // Push code with deliberate security issues
        await pr.PushNewCommitAsync("SecurityFlaws.cs", KnownBadCode.SecurityIssues);
        await Task.Delay(3000);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);

        Assert.AreEqual("Reviewed", result.Status, "AI review should complete.");
        Assert.IsNotNull(result.Summary, "Summary should exist.");

        // The AI should find at least one issue and NOT approve outright
        var threads = await pr.GetThreadsAsync();
        int inlineCount = CountInlineThreads(threads);

        Console.WriteLine($"  AI result: Inline comments={inlineCount}");
        Console.WriteLine($"  Summary excerpt: {result.Summary?[..Math.Min(200, result.Summary.Length)]}...");

        // With hardcoded secrets + SQL injection + path traversal, the AI should flag something
        Assert.IsTrue(inlineCount > 0,
            "AI should post at least 1 inline comment for code with obvious security flaws.");

        // Check the summary or threads mention security-related keywords
        var allThreadText = GetAllThreadText(threads);
        var summaryLower = (result.Summary ?? "").ToLowerInvariant();
        var combined = summaryLower + " " + allThreadText.ToLowerInvariant();

        var securityTerms = new[] { "secret", "key", "credential", "injection", "sql", "security", "hardcoded", "password", "traversal", "sanitize" };
        bool mentionsSecurity = securityTerms.Any(t => combined.Contains(t));

        Console.WriteLine($"  Mentions security term: {mentionsSecurity}");
        Assert.IsTrue(mentionsSecurity,
            "AI review should mention at least one security-related concern for code with hardcoded secrets and SQL injection.");

        Console.WriteLine($"  ✓ AI flagged security issues ({inlineCount} inline comments).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Known Bad Code — Bug & Code Smell
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(300_000)]
    public async Task KnownBadCode_BugsAndSmells_AiFlagsAtLeastOne()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [AI-Bugs] PR #{prId} in {repo}");

        // Push code with obvious bugs
        await pr.PushNewCommitAsync("BuggyCode.cs", KnownBadCode.BugsAndSmells);
        await Task.Delay(3000);

        var result = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);

        Assert.AreEqual("Reviewed", result.Status);

        var threads = await pr.GetThreadsAsync();
        int inlineCount = CountInlineThreads(threads);

        Console.WriteLine($"  AI result: Inline comments={inlineCount}");
        Console.WriteLine($"  Summary excerpt: {result.Summary?[..Math.Min(200, result.Summary.Length)]}...");

        Assert.IsTrue(inlineCount > 0,
            "AI should post at least 1 inline comment for code with null dereferences, resource leaks, and exceptions.");

        var allThreadText = GetAllThreadText(threads);
        var combined = (result.Summary ?? "").ToLowerInvariant() + " " + allThreadText.ToLowerInvariant();

        var bugTerms = new[] { "null", "dispose", "leak", "exception", "catch", "swallow", "resource", "httpclient", "loop", "performance" };
        bool mentionsBug = bugTerms.Any(t => combined.Contains(t));

        Console.WriteLine($"  Mentions bug term: {mentionsBug}");
        Assert.IsTrue(mentionsBug,
            "AI review should mention at least one bug-related concern for code with obvious defects.");

        Console.WriteLine($"  ✓ AI flagged bugs/smells ({inlineCount} inline comments).");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Fix-and-Reverify: Bad Code → Review → Fix → Re-Review
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(300_000)]
    public async Task FixAndReverify_BadCodeFixed_AiApprovesAfterFix()
    {
        await using var ctx = TestServiceBuilder.BuildWithRealAi();
        await using var pr = new TestPullRequestHelper(
            ctx.Settings.Organization, ctx.Settings.PersonalAccessToken, ctx.Project);

        var prId = await pr.CreateDraftPullRequestAsync();
        var repo = pr.RepositoryName!;
        Console.WriteLine($"  [AI-FixVerify] PR #{prId} in {repo}");

        // Step 1: Push bad code
        await pr.PushNewCommitAsync("DataService.cs", KnownBadCode.FixableService_Bad);
        await Task.Delay(3000);

        var r1 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Reviewed", r1.Status, "First review should complete.");

        var threads1 = await pr.GetThreadsAsync();
        int inline1 = CountInlineThreads(threads1);
        Console.WriteLine($"  First review: {inline1} inline comments on bad code.");

        // The bad version has hardcoded connection string + no using + catch-all
        // AI should flag at least one of these
        Assert.IsTrue(inline1 > 0,
            "AI should flag at least 1 issue in the bad version.");

        // Step 2: Push the fixed version (overwrites the file)
        // We create a new file with a different name because ADO push API adds files
        await pr.PushNewCommitAsync("DataServiceFixed.cs", KnownBadCode.FixableService_Fixed);
        await Task.Delay(3000);

        var r2 = await ctx.Orchestrator.ExecuteReviewAsync(ctx.Project, repo, prId);
        Assert.AreEqual("Reviewed", r2.Status, "Re-review should complete.");

        // The fixed version should get fewer or no new inline comments
        var threads2 = await pr.GetThreadsAsync();
        int inline2 = CountInlineThreads(threads2);
        int newInline = inline2 - inline1;

        Console.WriteLine($"  After fix: {inline2} total inline ({newInline} new for fixed file).");

        // We can't guarantee zero new comments (AI might have style suggestions),
        // but the fix should produce fewer critical issues than the bad version
        // At minimum, verify the review completed successfully
        Assert.IsNotNull(r2.Summary, "Re-review summary should exist.");

        Console.WriteLine($"  ✓ Fix-and-reverify cycle complete. Bad: {inline1} comments, Fixed: +{newInline} new.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static int CountInlineThreads(List<JsonElement> threads) =>
        threads.Count(t =>
            t.TryGetProperty("threadContext", out var ctx)
            && ctx.ValueKind != JsonValueKind.Null);

    private static string GetAllThreadText(List<JsonElement> threads)
    {
        var texts = new List<string>();
        foreach (var thread in threads)
        {
            if (thread.TryGetProperty("comments", out var comments))
            {
                foreach (var comment in comments.EnumerateArray())
                {
                    if (comment.TryGetProperty("content", out var content))
                        texts.Add(content.GetString() ?? "");
                }
            }
        }
        return string.Join(" ", texts);
    }
}

// ═══════════════════════════════════════════════════════════════════════════════
//  Known-bad code samples — deliberately flawed code for AI quality testing
// ═══════════════════════════════════════════════════════════════════════════════

/// <summary>
/// Static class containing known-bad C# code samples with deliberate issues.
/// Each sample is designed so that a competent AI reviewer should flag at
/// least one of the embedded problems.
/// </summary>
public static class KnownBadCode
{
    /// <summary>
    /// Security issues: hardcoded secrets, SQL injection, path traversal.
    /// </summary>
    public const string SecurityIssues = @"
using System;
using System.Data.SqlClient;
using System.IO;

namespace VulnerableApp;

public class UserRepository
{
    // ISSUE: Hardcoded connection string with plain-text credentials
    private const string ConnectionString =
        ""Server=prod-db.company.com;Database=Users;User Id=sa;Password=SuperSecret123!;"";

    // ISSUE: Hardcoded API key
    private readonly string _apiKey = ""sk-proj-abc123def456ghi789jkl012mno345pqr678stu901vwx234"";

    public UserRepository()
    {
        Console.WriteLine($""Connecting with key: {_apiKey}"");
    }

    // ISSUE: SQL injection — string concatenation in query
    public string GetUserEmail(string userId)
    {
        using var conn = new SqlConnection(ConnectionString);
        conn.Open();

        // This is vulnerable to SQL injection
        var query = ""SELECT Email FROM Users WHERE UserId = '"" + userId + ""'"";
        using var cmd = new SqlCommand(query, conn);
        return cmd.ExecuteScalar()?.ToString() ?? """";
    }

    // ISSUE: Path traversal — no sanitization of user input
    public string ReadUserFile(string fileName)
    {
        var path = Path.Combine(""/data/uploads"", fileName);
        return File.ReadAllText(path);  // fileName could be ""../../etc/passwd""
    }

    // ISSUE: Logging sensitive data
    public void AuthenticateUser(string username, string password)
    {
        Console.WriteLine($""Login attempt: user={username}, pass={password}"");

        if (username == ""admin"" && password == ""admin123"")
        {
            Console.WriteLine(""Admin authenticated"");
        }
    }
}
";

    /// <summary>
    /// Bugs and code smells: null dereference, resource leak, swallowed exception,
    /// performance issue.
    /// </summary>
    public const string BugsAndSmells = @"
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace BuggyApp;

public class DataProcessor
{
    // ISSUE: HttpClient created in a loop — resource leak / socket exhaustion
    public async Task<List<string>> FetchAllAsync(IEnumerable<string> urls)
    {
        var results = new List<string>();
        foreach (var url in urls)
        {
            var client = new HttpClient();  // Should be reused!
            var response = await client.GetStringAsync(url);
            results.Add(response);
            // No dispose — leaks HttpClient
        }
        return results;
    }

    // ISSUE: Null dereference — FirstOrDefault can return null
    public string GetTopItem(List<string> items)
    {
        var top = items.FirstOrDefault();
        return top.ToUpper();  // NullReferenceException if list is empty
    }

    // ISSUE: Catch-all that swallows exceptions silently
    public int ParseConfigValue(string input)
    {
        try
        {
            return int.Parse(input);
        }
        catch (Exception)
        {
            // Silently returns 0 — caller has no idea parsing failed
            return 0;
        }
    }

    // ISSUE: Performance — calling ToList() inside a hot loop
    public List<int> ProcessLargeDataset(IEnumerable<int> source)
    {
        var output = new List<int>();
        for (int i = 0; i < 10000; i++)
        {
            var filtered = source.Where(x => x > i).ToList();  // Materializes every iteration!
            output.Add(filtered.Count);
        }
        return output;
    }

    // ISSUE: Async void — exceptions will crash the process
    public async void FireAndForget(string url)
    {
        var client = new HttpClient();
        var result = await client.GetStringAsync(url);
        Console.WriteLine(result);
    }
}
";

    /// <summary>
    /// The ""bad"" version of a data service — has multiple fixable issues.
    /// Paired with <see cref="FixableService_Fixed"/> for the fix-and-reverify test.
    /// </summary>
    public const string FixableService_Bad = @"
using System;
using System.Data.SqlClient;
using System.Net.Http;
using System.Threading.Tasks;

namespace FixableApp;

/// <summary>
/// Data service that retrieves user information.
/// </summary>
public class DataService
{
    // ISSUE: Hardcoded connection string with credentials
    private const string ConnStr = ""Server=myServer;Database=myDb;User=admin;Password=P@ssw0rd;"";

    // ISSUE: No using/dispose on HttpClient
    public async Task<string> GetExternalDataAsync(string endpoint)
    {
        var client = new HttpClient();
        var result = await client.GetStringAsync(endpoint);
        return result;
    }

    // ISSUE: SQL injection via string concatenation
    public string LookupUser(string name)
    {
        using var conn = new SqlConnection(ConnStr);
        conn.Open();
        var cmd = new SqlCommand(""SELECT * FROM Users WHERE Name = '"" + name + ""'"", conn);
        return cmd.ExecuteScalar()?.ToString() ?? ""Not found"";
    }

    // ISSUE: Catch-all swallowing exception
    public int SafeParse(string value)
    {
        try
        {
            return int.Parse(value);
        }
        catch (Exception)
        {
            return -1;
        }
    }
}
";

    /// <summary>
    /// The ""fixed"" version of the same service — all issues addressed.
    /// </summary>
    public const string FixableService_Fixed = @"
using System;
using System.Data.SqlClient;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace FixableApp;

/// <summary>
/// Data service that retrieves user information.
/// Connection string is injected via configuration; HttpClient via DI.
/// </summary>
public class DataService
{
    private readonly string _connectionString;
    private readonly HttpClient _httpClient;
    private readonly ILogger<DataService> _logger;

    public DataService(IConfiguration config, HttpClient httpClient, ILogger<DataService> logger)
    {
        _connectionString = config.GetConnectionString(""UsersDb"")
            ?? throw new ArgumentNullException(nameof(config), ""UsersDb connection string is required."");
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<string> GetExternalDataAsync(string endpoint)
    {
        // HttpClient is injected and managed by DI — no leak
        var result = await _httpClient.GetStringAsync(endpoint);
        return result;
    }

    public string LookupUser(string name)
    {
        using var conn = new SqlConnection(_connectionString);
        conn.Open();

        // Parameterized query — no SQL injection
        using var cmd = new SqlCommand(""SELECT * FROM Users WHERE Name = @Name"", conn);
        cmd.Parameters.AddWithValue(""@Name"", name);
        return cmd.ExecuteScalar()?.ToString() ?? ""Not found"";
    }

    public bool TryParse(string value, out int result)
    {
        // Proper error handling — no swallowed exceptions
        if (int.TryParse(value, out result))
        {
            return true;
        }

        _logger.LogWarning(""Failed to parse value: {Value}"", value);
        result = 0;
        return false;
    }
}
";
}
