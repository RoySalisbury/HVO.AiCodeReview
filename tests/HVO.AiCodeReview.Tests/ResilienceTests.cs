using System.Net;
using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Azure DevOps HTTP resilience (Polly-based retry policies).
/// These tests validate the configured resilience pipeline using a custom
/// HTTP message handler stub that controls HTTP responses, verifying retry
/// behavior and handling of Retry-After semantics.
/// </summary>
[TestClass]
public class ResilienceTests
{
    /// <summary>Valid labels JSON for HasReviewTagAsync.</summary>
    private static HttpResponseMessage OkLabelsResponse() =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"count\": 0, \"value\": []}")
        };

    // ── Retry on transient 5xx ──────────────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Retry_TransientServerError_RetriesAndSucceeds()
    {
        // Arrange: fail twice with 503, then succeed
        var handler = new FakeResponseHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
            OkLabelsResponse());

        var devOps = BuildServiceWithResilience(handler);

        // Act — HasReviewTagAsync is the simplest GET call
        var result = await devOps.HasReviewTagAsync("TestProject", "TestRepo", 1);

        // Assert — should have retried through the 503s
        Assert.IsFalse(result, "No tags expected in empty array");
        Assert.AreEqual(3, handler.CallCount, "Expected 2 retries + 1 success = 3 total calls");
    }

    // ── Retry on 429 (Too Many Requests) ────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Retry_TooManyRequests_RetriesAndSucceeds()
    {
        // Arrange: 429 once, then succeed
        var rateLimitResponse = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        rateLimitResponse.Headers.Add("Retry-After", "1");

        var handler = new FakeResponseHandler(rateLimitResponse, OkLabelsResponse());

        var devOps = BuildServiceWithResilience(handler);

        var result = await devOps.HasReviewTagAsync("TestProject", "TestRepo", 1);

        Assert.IsFalse(result);
        Assert.AreEqual(2, handler.CallCount, "Expected 1 retry + 1 success = 2 total calls");
    }

    // ── Retry on 408 (Request Timeout) ──────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Retry_RequestTimeout_RetriesAndSucceeds()
    {
        var handler = new FakeResponseHandler(
            new HttpResponseMessage(HttpStatusCode.RequestTimeout),
            OkLabelsResponse());

        var devOps = BuildServiceWithResilience(handler);

        var result = await devOps.HasReviewTagAsync("TestProject", "TestRepo", 1);

        Assert.IsFalse(result);
        Assert.AreEqual(2, handler.CallCount);
    }

    // ── No retry on 4xx (non-transient) ─────────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public async Task NoRetry_ClientError_FailsImmediately()
    {
        var handler = new FakeResponseHandler(
            new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                Content = new StringContent("")
            });

        var devOps = BuildServiceWithResilience(handler);

        // Act & Assert — 404 is not transient, shouldn't retry, should throw
        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => devOps.HasReviewTagAsync("TestProject", "TestRepo", 1),
            "Expected HttpRequestException for non-transient 404");

        Assert.AreEqual(1, handler.CallCount, "Non-transient 404 should not be retried");
    }

    // ── Exhausted retries surface exception ─────────────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Retry_AllRetriesExhausted_ThrowsException()
    {
        // Arrange: return 503 for every attempt (more than max retries)
        var handler = new FakeResponseHandler(Enumerable
            .Range(0, 20)
            .Select(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable))
            .ToArray());

        var devOps = BuildServiceWithResilience(handler);

        // Act & Assert — should eventually fail after max retries
        await Assert.ThrowsExceptionAsync<HttpRequestException>(
            () => devOps.HasReviewTagAsync("TestProject", "TestRepo", 1),
            "Expected HttpRequestException after retries exhausted");

        // With MaxRetryAttempts = 5, we expect exactly 1 initial attempt + 5 retries = 6 total calls
        Assert.AreEqual(6, handler.CallCount, $"Expected exactly 6 attempts (1 initial + 5 retries), got {handler.CallCount}");
    }

    // ── Multiple consecutive 429s with Retry-After ──────────────────────

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Retry_Multiple429s_EventuallySucceeds()
    {
        var r1 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        r1.Headers.Add("Retry-After", "1");
        var r2 = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        r2.Headers.Add("Retry-After", "1");

        var handler = new FakeResponseHandler(r1, r2, OkLabelsResponse());

        var devOps = BuildServiceWithResilience(handler);
        var result = await devOps.HasReviewTagAsync("TestProject", "TestRepo", 1);

        Assert.IsFalse(result);
        Assert.AreEqual(3, handler.CallCount, "Expected 2 retries + 1 success = 3 total calls");
    }

    // ── Verify DI wiring produces a working IAzureDevOpsService ─────────

    [TestMethod]
    [TestCategory("Unit")]
    public void DI_ResilienceHandler_Registers()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.Configure<AzureDevOpsSettings>(o =>
        {
            o.Organization = "TestOrg";
            o.PersonalAccessToken = "fake-pat";
        });

        services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>(client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
        })
        .AddStandardResilienceHandler(options =>
        {
            options.Retry.MaxRetryAttempts = 5;
            options.Retry.Delay = TimeSpan.FromSeconds(2);
            options.Retry.BackoffType = Polly.DelayBackoffType.Exponential;
            options.Retry.UseJitter = true;

            options.CircuitBreaker.SamplingDuration = TimeSpan.FromMinutes(2);
            options.CircuitBreaker.FailureRatio = 0.9;
            options.CircuitBreaker.MinimumThroughput = 5;
            options.CircuitBreaker.BreakDuration = TimeSpan.FromSeconds(15);

            options.TotalRequestTimeout.Timeout = TimeSpan.FromMinutes(3);
            options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(60);
        });

        var sp = services.BuildServiceProvider();
        var devOps = sp.GetService<IAzureDevOpsService>();

        Assert.IsNotNull(devOps, "IAzureDevOpsService should be resolvable with resilience handler");
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Builds an <see cref="IAzureDevOpsService"/> with the standard resilience handler
    /// and a custom <see cref="HttpMessageHandler"/> for controlled responses.
    /// Uses minimal retry delays for fast test execution.
    /// </summary>
    private static IAzureDevOpsService BuildServiceWithResilience(FakeResponseHandler handler)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.Configure<AzureDevOpsSettings>(o =>
        {
            o.Organization = "TestOrg";
            o.PersonalAccessToken = "fake-pat";
        });

        services.AddHttpClient<IAzureDevOpsService, AzureDevOpsService>()
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddStandardResilienceHandler(options =>
            {
                // Minimal delays for fast tests
                options.Retry.MaxRetryAttempts = 5;
                options.Retry.Delay = TimeSpan.FromMilliseconds(10);
                options.Retry.BackoffType = Polly.DelayBackoffType.Constant;
                options.Retry.UseJitter = false;

                options.CircuitBreaker.SamplingDuration = TimeSpan.FromSeconds(30);
                options.CircuitBreaker.FailureRatio = 0.9;
                options.CircuitBreaker.MinimumThroughput = 10; // high threshold for tests
                options.CircuitBreaker.BreakDuration = TimeSpan.FromMilliseconds(500);

                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(10);
            });

        var sp = services.BuildServiceProvider();
        return sp.GetRequiredService<IAzureDevOpsService>();
    }

    /// <summary>
    /// An <see cref="HttpMessageHandler"/> that returns a sequence of canned responses.
    /// When the sequence is exhausted, repeats the last response.
    /// Tracks the number of calls for assertion.
    /// </summary>
    private class FakeResponseHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage[] _responses;
        private int _callIndex;

        public int CallCount => _callIndex;

        public FakeResponseHandler(params HttpResponseMessage[] responses)
        {
            _responses = responses;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _callIndex) - 1;
            var response = index < _responses.Length
                ? _responses[index]
                : _responses[^1]; // repeat last

            // Ensure the response is associated with the request
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
