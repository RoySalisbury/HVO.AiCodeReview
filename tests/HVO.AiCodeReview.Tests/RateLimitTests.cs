using System.ClientModel;
using AiCodeReview.Services;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

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
        signal.SignalCooldown(TimeSpan.FromMilliseconds(200));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await signal.WaitIfCoolingDownAsync();
        sw.Stop();

        // Should have waited roughly 200ms (allow 100-600ms for test jitter)
        Assert.IsTrue(sw.ElapsedMilliseconds >= 100,
            $"Expected wait of ~200ms but only {sw.ElapsedMilliseconds}ms");
        Assert.IsTrue(sw.ElapsedMilliseconds < 600,
            $"Expected wait of ~200ms but took {sw.ElapsedMilliseconds}ms");
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
}
