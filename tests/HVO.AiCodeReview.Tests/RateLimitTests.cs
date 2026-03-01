using System.ClientModel;
using System.ClientModel.Primitives;
using AiCodeReview.Services;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

[TestCategory("Unit")]
[TestClass]
public class RateLimitTests
{
    // ──────────────────────────────────────────────────────────────────────
    //  RateLimitHelper.TryParseRetryAfterFromMessage
    // ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TryParseRetryAfterFromMessage_StandardFormat_ParsesCorrectly()
    {
        var result = RateLimitHelper.TryParseRetryAfterFromMessage(
            "Rate limit reached. Please retry after 60 seconds.");
        Assert.AreEqual(60, result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromMessage_MixedCase_ParsesCorrectly()
    {
        var result = RateLimitHelper.TryParseRetryAfterFromMessage(
            "RETRY AFTER 45 SECONDS remaining.");
        Assert.AreEqual(45, result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromMessage_NoMatch_ReturnsNull()
    {
        var result = RateLimitHelper.TryParseRetryAfterFromMessage(
            "Something went wrong with the service.");
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromMessage_NullMessage_ReturnsNull()
    {
        Assert.IsNull(RateLimitHelper.TryParseRetryAfterFromMessage(null));
    }

    [TestMethod]
    public void TryParseRetryAfterFromMessage_EmptyMessage_ReturnsNull()
    {
        Assert.IsNull(RateLimitHelper.TryParseRetryAfterFromMessage(""));
    }

    [TestMethod]
    public void TryParseRetryAfterFromMessage_ZeroSeconds_ReturnsNull()
    {
        // 0 is not > 0, so should return null
        Assert.IsNull(RateLimitHelper.TryParseRetryAfterFromMessage(
            "retry after 0 seconds"));
    }

    [TestMethod]
    public void TryParseRetryAfterFromMessage_LargeValue_ParsesCorrectly()
    {
        var result = RateLimitHelper.TryParseRetryAfterFromMessage(
            "Please retry after 3600 seconds.");
        Assert.AreEqual(3600, result);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  RateLimitHelper.ComputeRetryDelay (with non-ClientResultException)
    // ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ComputeRetryDelay_GenericException_ReturnsDefault()
    {
        var delay = RateLimitHelper.ComputeRetryDelay(new Exception("Unknown error"));
        // Default 30s + 5s buffer = 35s
        Assert.AreEqual(
            RateLimitHelper.DefaultRetryAfterSeconds + RateLimitHelper.RetryAfterBufferSeconds,
            (int)delay.TotalSeconds);
    }

    [TestMethod]
    public void ComputeRetryDelay_GenericExceptionWithRetryMessage_ParsesFromMessage()
    {
        var delay = RateLimitHelper.ComputeRetryDelay(
            new Exception("Rate limited. Please retry after 20 seconds."));
        // 20s + 5s buffer = 25s
        Assert.AreEqual(25, (int)delay.TotalSeconds);
    }

    [TestMethod]
    public void ComputeRetryDelay_ExcessiveValue_ClampedToMax()
    {
        var delay = RateLimitHelper.ComputeRetryDelay(
            new Exception("retry after 9999 seconds"));
        // Clamped to MaxRetryAfterSeconds (120) + buffer (5) = 125s
        Assert.AreEqual(
            RateLimitHelper.MaxRetryAfterSeconds + RateLimitHelper.RetryAfterBufferSeconds,
            (int)delay.TotalSeconds);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  RateLimitHelper constants sanity checks
    // ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Constants_SaneValues()
    {
        Assert.AreEqual(5, RateLimitHelper.MaxRateLimitRetries);
        Assert.AreEqual(30, RateLimitHelper.DefaultRetryAfterSeconds);
        Assert.AreEqual(120, RateLimitHelper.MaxRetryAfterSeconds);
        Assert.AreEqual(5, RateLimitHelper.RetryAfterBufferSeconds);
        Assert.AreEqual(TimeSpan.FromMinutes(5), RateLimitHelper.MaxTotalRetryDuration);
    }

    // ──────────────────────────────────────────────────────────────────────
    //  GlobalRateLimitSignal — basic behaviour
    // ──────────────────────────────────────────────────────────────────────

    private static GlobalRateLimitSignal CreateSignal() =>
        new(LoggerFactory.Create(b => b.AddConsole().SetMinimumLevel(LogLevel.Debug))
                          .CreateLogger<GlobalRateLimitSignal>());

    [TestMethod]
    public void Signal_InitialState_NotCoolingDown()
    {
        var signal = CreateSignal();
        Assert.IsFalse(signal.IsCoolingDown);
        Assert.AreEqual(DateTimeOffset.MinValue, signal.CooldownExpiresUtc);
    }

    [TestMethod]
    public async Task Signal_WaitIfCoolingDown_ReturnsImmediatelyWhenNoCooldown()
    {
        var signal = CreateSignal();
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitIfCoolingDownAsync();
        sw.Stop();
        // Should return virtually instantly (< 100ms)
        Assert.IsTrue(sw.ElapsedMilliseconds < 100,
            $"Expected immediate return but took {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public void Signal_AfterSignalCooldown_IsCoolingDown()
    {
        var signal = CreateSignal();
        signal.SignalCooldown(TimeSpan.FromSeconds(10));
        Assert.IsTrue(signal.IsCoolingDown);
    }

    [TestMethod]
    public void Signal_CooldownExpiry_ReportsValidFutureTime()
    {
        var signal = CreateSignal();
        var before = DateTimeOffset.UtcNow;
        signal.SignalCooldown(TimeSpan.FromSeconds(30));
        var expiry = signal.CooldownExpiresUtc;

        // Expiry should be roughly 30s in the future
        Assert.IsTrue(expiry > before, "Expiry should be after call time");
        Assert.IsTrue(expiry <= before.AddSeconds(31), "Expiry should be roughly 30s out");
    }

    [TestMethod]
    public void Signal_LongerCooldownWins_KeepsFurtherExpiry()
    {
        var signal = CreateSignal();
        signal.SignalCooldown(TimeSpan.FromSeconds(60));
        var firstExpiry = signal.CooldownExpiresUtc;

        // Signal a shorter cooldown — should NOT overwrite
        signal.SignalCooldown(TimeSpan.FromSeconds(5));
        var secondExpiry = signal.CooldownExpiresUtc;

        // The longer cooldown should still be in effect
        Assert.IsTrue(secondExpiry >= firstExpiry.AddSeconds(-1),
            "Shorter cooldown should not overwrite a longer-running one");
    }

    [TestMethod]
    public void Signal_LongerCooldownReplacesShorter()
    {
        var signal = CreateSignal();
        signal.SignalCooldown(TimeSpan.FromSeconds(5));
        var firstExpiry = signal.CooldownExpiresUtc;

        // Now signal a LONGER cooldown — should override
        signal.SignalCooldown(TimeSpan.FromSeconds(120));
        var secondExpiry = signal.CooldownExpiresUtc;

        Assert.IsTrue(secondExpiry > firstExpiry.AddSeconds(100),
            "Longer cooldown should replace shorter one");
    }

    [TestMethod]
    public void Signal_ZeroDuration_Ignored()
    {
        var signal = CreateSignal();
        signal.SignalCooldown(TimeSpan.Zero);
        Assert.IsFalse(signal.IsCoolingDown);
    }

    [TestMethod]
    public void Signal_NegativeDuration_Ignored()
    {
        var signal = CreateSignal();
        signal.SignalCooldown(TimeSpan.FromSeconds(-5));
        Assert.IsFalse(signal.IsCoolingDown);
    }

    [TestMethod]
    public async Task Signal_ShortCooldown_WaitsAndReturns()
    {
        var signal = CreateSignal();
        signal.SignalCooldown(TimeSpan.FromMilliseconds(500));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitIfCoolingDownAsync();
        sw.Stop();

        // Should have waited roughly 500ms (allow 200-1500ms for test jitter / parallel load)
        Assert.IsTrue(sw.ElapsedMilliseconds >= 200,
            $"Expected wait of ~500ms but only {sw.ElapsedMilliseconds}ms");
        Assert.IsTrue(sw.ElapsedMilliseconds < 1500,
            $"Expected wait of ~500ms but took {sw.ElapsedMilliseconds}ms");
    }

    [TestMethod]
    public async Task Signal_WaitIfCoolingDown_CancellationHonoured()
    {
        var signal = CreateSignal();
        signal.SignalCooldown(TimeSpan.FromSeconds(60));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsExceptionAsync<TaskCanceledException>(
            () => signal.WaitIfCoolingDownAsync(cts.Token));
    }

    [TestMethod]
    public async Task Signal_ConcurrentSignals_KeepLongest()
    {
        var signal = CreateSignal();

        // Fire multiple signals concurrently with different durations
        var tasks = new[]
        {
            Task.Run(() => signal.SignalCooldown(TimeSpan.FromSeconds(5))),
            Task.Run(() => signal.SignalCooldown(TimeSpan.FromSeconds(60))),
            Task.Run(() => signal.SignalCooldown(TimeSpan.FromSeconds(10))),
            Task.Run(() => signal.SignalCooldown(TimeSpan.FromSeconds(30))),
        };
        await Task.WhenAll(tasks);

        Assert.IsTrue(signal.IsCoolingDown);
        // The longest (60s) should win — expiry should be roughly 60s from now
        var remaining = signal.CooldownExpiresUtc - DateTimeOffset.UtcNow;
        Assert.IsTrue(remaining.TotalSeconds > 50,
            $"Expected ~60s remaining, got {remaining.TotalSeconds:F1}s — longest cooldown should win");
    }

    // ──────────────────────────────────────────────────────────────────────
    //  RateLimitHelper.TryParseRetryAfterFromResponse
    // ──────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void TryParseRetryAfterFromResponse_IntegerSeconds_ParsesCorrectly()
    {
        var response = new MockPipelineResponse(429, new Dictionary<string, string>
        {
            ["Retry-After"] = "60"
        });
        var cex = new ClientResultException("Rate limited", response, innerException: null);

        var result = RateLimitHelper.TryParseRetryAfterFromResponse(cex);
        Assert.AreEqual(60, result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromResponse_NoHeader_ReturnsNull()
    {
        var response = new MockPipelineResponse(429, new Dictionary<string, string>());
        var cex = new ClientResultException("Rate limited", response, innerException: null);

        var result = RateLimitHelper.TryParseRetryAfterFromResponse(cex);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromResponse_EmptyHeader_ReturnsNull()
    {
        var response = new MockPipelineResponse(429, new Dictionary<string, string>
        {
            ["Retry-After"] = ""
        });
        var cex = new ClientResultException("Rate limited", response, innerException: null);

        var result = RateLimitHelper.TryParseRetryAfterFromResponse(cex);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromResponse_NonNumericHeader_ReturnsNull()
    {
        var response = new MockPipelineResponse(429, new Dictionary<string, string>
        {
            ["Retry-After"] = "not-a-number"
        });
        var cex = new ClientResultException("Rate limited", response, innerException: null);

        var result = RateLimitHelper.TryParseRetryAfterFromResponse(cex);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromResponse_NullResponse_ReturnsNull()
    {
        var cex = new ClientResultException("Rate limited", response: null, innerException: null);

        var result = RateLimitHelper.TryParseRetryAfterFromResponse(cex);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void TryParseRetryAfterFromResponse_LargeValue_ParsesCorrectly()
    {
        var response = new MockPipelineResponse(429, new Dictionary<string, string>
        {
            ["Retry-After"] = "300"
        });
        var cex = new ClientResultException("Rate limited", response, innerException: null);

        var result = RateLimitHelper.TryParseRetryAfterFromResponse(cex);
        Assert.AreEqual(300, result);
    }

    [TestMethod]
    public void ComputeRetryDelay_ClientResultExceptionWithHeader_UsesHeaderOverMessage()
    {
        // Response says 60s, message says 20s — header should win
        var response = new MockPipelineResponse(429, new Dictionary<string, string>
        {
            ["Retry-After"] = "60"
        });
        var cex = new ClientResultException("Please retry after 20 seconds", response, innerException: null);

        var delay = RateLimitHelper.ComputeRetryDelay(cex);
        // 60 + 5 buffer = 65s
        Assert.AreEqual(65, (int)delay.TotalSeconds);
    }

    [TestMethod]
    public void ComputeRetryDelay_ClientResultExceptionNoHeader_FallsBackToMessage()
    {
        // No Retry-After header, message has "retry after 45 seconds"
        var response = new MockPipelineResponse(429, new Dictionary<string, string>());
        var cex = new ClientResultException("Please retry after 45 seconds", response, innerException: null);

        var delay = RateLimitHelper.ComputeRetryDelay(cex);
        // 45 + 5 buffer = 50s
        Assert.AreEqual(50, (int)delay.TotalSeconds);
    }

    [TestMethod]
    public void ComputeRetryDelay_ClientResultExceptionNullResponse_FallsBackToMessage()
    {
        var cex = new ClientResultException("Please retry after 30 seconds", response: null, innerException: null);

        var delay = RateLimitHelper.ComputeRetryDelay(cex);
        // 30 + 5 buffer = 35s
        Assert.AreEqual(35, (int)delay.TotalSeconds);
    }

    [TestMethod]
    public void ComputeRetryDelay_ClientResultExceptionNothingParseable_ReturnsDefault()
    {
        var response = new MockPipelineResponse(429, new Dictionary<string, string>());
        var cex = new ClientResultException("Some error", response, innerException: null);

        var delay = RateLimitHelper.ComputeRetryDelay(cex);
        // Default 30 + 5 buffer = 35s
        Assert.AreEqual(35, (int)delay.TotalSeconds);
    }
}

// ──────────────────────────────────────────────────────────────────────────
//  Mock PipelineResponse for testing Retry-After header parsing
// ──────────────────────────────────────────────────────────────────────────

/// <summary>
/// Minimal <see cref="PipelineResponse"/> subclass that returns configurable
/// status and headers for unit testing <see cref="RateLimitHelper"/>.
/// </summary>
internal sealed class MockPipelineResponse : PipelineResponse
{
    private readonly int _status;
    private readonly MockPipelineResponseHeaders _headers;

    public MockPipelineResponse(int status, Dictionary<string, string> headers)
    {
        _status = status;
        _headers = new MockPipelineResponseHeaders(headers);
    }

    public override int Status => _status;
    public override string ReasonPhrase => _status == 429 ? "Too Many Requests" : "OK";
    public override Stream? ContentStream { get => null; set { } }
    public override BinaryData Content => BinaryData.FromString(string.Empty);

    protected override PipelineResponseHeaders HeadersCore => _headers;

    public override void Dispose() { }

    public override BinaryData BufferContent(CancellationToken cancellationToken = default)
        => BinaryData.FromString(string.Empty);

    public override ValueTask<BinaryData> BufferContentAsync(CancellationToken cancellationToken = default)
        => new(BinaryData.FromString(string.Empty));
}

internal sealed class MockPipelineResponseHeaders : PipelineResponseHeaders
{
    private readonly Dictionary<string, string> _headers;

    public MockPipelineResponseHeaders(Dictionary<string, string> headers)
    {
        _headers = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase);
    }

    public override IEnumerator<KeyValuePair<string, string>> GetEnumerator()
        => _headers.GetEnumerator();

    public override bool TryGetValue(string name, out string? value)
        => _headers.TryGetValue(name, out value);

    public override bool TryGetValues(string name, out IEnumerable<string>? values)
    {
        if (_headers.TryGetValue(name, out var value))
        {
            values = new[] { value };
            return true;
        }
        values = null;
        return false;
    }
}
