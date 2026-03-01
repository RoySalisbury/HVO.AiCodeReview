using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using HVO.Enterprise.Telemetry.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for the race condition fix in AppendReviewHistoryAsync (#29).
/// Uses a mock HTTP handler with artificial delay to widen the read-modify-write
/// window, proving that the per-PR semaphore serializes concurrent writes.
/// </summary>
[TestCategory("Integration")]
[TestClass]
public class RaceConditionTests
{
    /// <summary>
    /// Mock HTTP handler that stores PR properties in memory and adds configurable
    /// delay on GET requests to widen the race window.
    /// </summary>
    private sealed class DelayingMockHandler : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, string> _properties = new();
        private readonly TimeSpan _getDelay;

        /// <summary>How many GET requests were made (for observability).</summary>
        public int GetCount;
        /// <summary>How many PATCH requests were made.</summary>
        public int PatchCount;

        public DelayingMockHandler(TimeSpan getDelay)
        {
            _getDelay = getDelay;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.AbsoluteUri;

            // PR properties endpoint
            if (url.Contains("/properties"))
            {
                if (request.Method == HttpMethod.Get)
                {
                    Interlocked.Increment(ref GetCount);

                    // Artificial delay to widen the race window
                    await Task.Delay(_getDelay, cancellationToken);

                    // Return current stored history (or empty)
                    var historyJson = _properties.GetValueOrDefault("ReviewHistory", "[]");

                    var responseBody = JsonSerializer.Serialize(new
                    {
                        value = new Dictionary<string, object>
                        {
                            ["AiCodeReview.ReviewHistory"] = new { _value = historyJson },
                        },
                    });

                    // The real API uses "$value" property name — simulate that
                    responseBody = responseBody.Replace("_value", "$value");

                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(responseBody, Encoding.UTF8, "application/json"),
                    };
                }

                if (request.Method.Method == "PATCH")
                {
                    Interlocked.Increment(ref PatchCount);

                    // Parse the patch body to extract the history value
                    var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(body);
                    var ops = doc.RootElement;
                    foreach (var op in ops.EnumerateArray())
                    {
                        if (op.GetProperty("path").GetString()?.Contains("ReviewHistory") == true)
                        {
                            var value = op.GetProperty("value").GetString()!;
                            _properties["ReviewHistory"] = value;
                        }
                    }

                    return new HttpResponseMessage(HttpStatusCode.OK);
                }
            }

            // Identity / connection data endpoint
            if (url.Contains("_apis/connectionData"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"authenticatedUser":{"id":"test-identity-id"}}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        /// <summary>Returns the current stored history entries.</summary>
        public List<ReviewHistoryEntry> GetStoredHistory()
        {
            var json = _properties.GetValueOrDefault("ReviewHistory", "[]");
            return JsonSerializer.Deserialize<List<ReviewHistoryEntry>>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        }
    }

    private static AzureDevOpsService CreateServiceWithHandler(
        DelayingMockHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://dev.azure.com/TestOrg/"),
        };
        httpClient.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(Encoding.ASCII.GetBytes(":fake-pat")));

        var settings = Options.Create(new AzureDevOpsSettings
        {
            Organization = "TestOrg",
            PersonalAccessToken = "fake-pat",
        });

        var logger = LoggerFactory
            .Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug))
            .CreateLogger<AzureDevOpsService>();

        return new AzureDevOpsService(httpClient, settings, new NullTelemetryService(), logger);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Without the lock, concurrent appends would overwrite each other.
    //  With the lock, all entries are preserved.
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentAppends_SamePR_AllEntriesPreserved()
    {
        // Arrange: 200ms GET delay to make the race window obvious
        var handler = new DelayingMockHandler(getDelay: TimeSpan.FromMilliseconds(200));
        using var svc = CreateServiceWithHandler(handler);

        const string project = "TestProject";
        const string repo = "TestRepo";
        const int prId = 42;
        const int concurrentWrites = 5;

        var entries = Enumerable.Range(1, concurrentWrites).Select(i => new ReviewHistoryEntry
        {
            ReviewNumber = i,
            ReviewedAtUtc = DateTime.UtcNow,
            Action = $"Review #{i}",
            Verdict = "Approved",
            SourceCommit = $"commit-{i}",
            Iteration = i,
        }).ToArray();

        // Act: fire all appends concurrently
        var tasks = entries.Select(e =>
            svc.AppendReviewHistoryAsync(project, repo, prId, e));
        await Task.WhenAll(tasks);

        // Assert: all entries should be preserved (no overwrites)
        var history = handler.GetStoredHistory();

        Console.WriteLine($"  GET calls: {handler.GetCount}, PATCH calls: {handler.PatchCount}");
        Console.WriteLine($"  History entries: {history.Count}");
        foreach (var h in history)
            Console.WriteLine($"    #{h.ReviewNumber} — {h.Action}");

        Assert.AreEqual(concurrentWrites, history.Count,
            $"Expected {concurrentWrites} entries but got {history.Count} — " +
            "concurrent writes likely overwrote each other.");

        // Verify all review numbers are present
        var reviewNumbers = history.Select(h => h.ReviewNumber).OrderBy(n => n).ToList();
        CollectionAssert.AreEqual(
            Enumerable.Range(1, concurrentWrites).ToList(),
            reviewNumbers,
            "All review numbers should be present (no data loss).");
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task ConcurrentAppends_DifferentPRs_Independent()
    {
        // Different PRs should NOT block each other — verify parallelism still works
        var handler = new DelayingMockHandler(getDelay: TimeSpan.FromMilliseconds(100));
        using var svc = CreateServiceWithHandler(handler);

        const string project = "TestProject";
        const string repo = "TestRepo";

        // Fire appends to 3 different PRs concurrently
        var tasks = new[]
        {
            svc.AppendReviewHistoryAsync(project, repo, 100, new ReviewHistoryEntry
            {
                ReviewNumber = 1, Action = "PR-100", Verdict = "OK", ReviewedAtUtc = DateTime.UtcNow,
            }),
            svc.AppendReviewHistoryAsync(project, repo, 200, new ReviewHistoryEntry
            {
                ReviewNumber = 1, Action = "PR-200", Verdict = "OK", ReviewedAtUtc = DateTime.UtcNow,
            }),
            svc.AppendReviewHistoryAsync(project, repo, 300, new ReviewHistoryEntry
            {
                ReviewNumber = 1, Action = "PR-300", Verdict = "OK", ReviewedAtUtc = DateTime.UtcNow,
            }),
        };

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Task.WhenAll(tasks);
        sw.Stop();

        Console.WriteLine($"  3 different PRs completed in {sw.ElapsedMilliseconds}ms");
        Console.WriteLine($"  GET calls: {handler.GetCount}, PATCH calls: {handler.PatchCount}");

        // All 3 should complete — and since they're different PRs, they run concurrently
        // If they were serialized by the same lock, total time would be ~300ms+
        // Concurrent execution should complete in ~100-150ms
        Assert.AreEqual(3, handler.PatchCount, "All 3 PATCHes should complete.");

        // NOTE: We can't strictly assert timing since CI can be slow,
        // but we verify that all writes succeeded independently.
    }

    [TestMethod]
    [Timeout(30_000)]
    public async Task SequentialAppends_EntriesAccumulate()
    {
        // Basic sanity: sequential appends should always work
        var handler = new DelayingMockHandler(getDelay: TimeSpan.Zero);
        using var svc = CreateServiceWithHandler(handler);

        const string project = "TestProject";
        const string repo = "TestRepo";
        const int prId = 99;

        for (int i = 1; i <= 3; i++)
        {
            await svc.AppendReviewHistoryAsync(project, repo, prId, new ReviewHistoryEntry
            {
                ReviewNumber = i,
                ReviewedAtUtc = DateTime.UtcNow,
                Action = $"Review #{i}",
                Verdict = "Approved",
            });
        }

        var history = handler.GetStoredHistory();
        Assert.AreEqual(3, history.Count, "Sequential appends should accumulate.");
        Assert.AreEqual(1, history[0].ReviewNumber);
        Assert.AreEqual(2, history[1].ReviewNumber);
        Assert.AreEqual(3, history[2].ReviewNumber);

        Console.WriteLine("  ✓ Sequential appends accumulate correctly.");
    }
}
