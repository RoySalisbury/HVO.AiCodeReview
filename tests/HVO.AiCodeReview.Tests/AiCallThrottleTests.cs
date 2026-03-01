using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for <see cref="AiCallThrottle"/> — verifies semaphore-based
/// concurrency limiting for AI inference calls.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AiCallThrottleTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Constructor
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Constructor_SetsMaxCount()
    {
        using var throttle = new AiCallThrottle(5);
        Assert.AreEqual(5, throttle.MaxCount);
        Assert.AreEqual(5, throttle.AvailableCount);
    }

    [TestMethod]
    public void Constructor_MinimumIsOne()
    {
        using var throttle = new AiCallThrottle(0);
        Assert.AreEqual(1, throttle.MaxCount);
        Assert.AreEqual(1, throttle.AvailableCount);
    }

    [TestMethod]
    public void Constructor_NegativeValue_ClampsToOne()
    {
        using var throttle = new AiCallThrottle(-5);
        Assert.AreEqual(1, throttle.MaxCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Acquire / Release
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Acquire_DecrementsAvailableCount()
    {
        using var throttle = new AiCallThrottle(3);

        await throttle.AcquireAsync();

        Assert.AreEqual(2, throttle.AvailableCount);
    }

    [TestMethod]
    public async Task Release_IncrementsAvailableCount()
    {
        using var throttle = new AiCallThrottle(3);

        await throttle.AcquireAsync();
        Assert.AreEqual(2, throttle.AvailableCount);

        throttle.Release();
        Assert.AreEqual(3, throttle.AvailableCount);
    }

    [TestMethod]
    public async Task Acquire_AllSlots_BlocksUntilRelease()
    {
        using var throttle = new AiCallThrottle(2);

        // Acquire both slots
        await throttle.AcquireAsync();
        await throttle.AcquireAsync();
        Assert.AreEqual(0, throttle.AvailableCount);

        // Third acquire should block — verify with timeout
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsExceptionAsync<OperationCanceledException>(
            () => throttle.AcquireAsync(cts.Token));
    }

    [TestMethod]
    public async Task Acquire_BlockedThenReleased_Succeeds()
    {
        using var throttle = new AiCallThrottle(1);

        await throttle.AcquireAsync();
        Assert.AreEqual(0, throttle.AvailableCount);

        // Release after a short delay
        var releaseTask = Task.Run(async () =>
        {
            await Task.Delay(50);
            throttle.Release();
        });

        // This should succeed once the release happens
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await throttle.AcquireAsync(cts.Token);

        Assert.AreEqual(0, throttle.AvailableCount);
        await releaseTask;
    }

    [TestMethod]
    public async Task Acquire_CancellationToken_Honoured()
    {
        using var throttle = new AiCallThrottle(1);
        await throttle.AcquireAsync();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        // SemaphoreSlim.WaitAsync may throw TaskCanceledException (subclass of OperationCanceledException)
        try
        {
            await throttle.AcquireAsync(cts.Token);
            Assert.Fail("Expected OperationCanceledException");
        }
        catch (OperationCanceledException)
        {
            // expected — covers both OperationCanceledException and TaskCanceledException
        }
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Interface
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ImplementsIAiCallThrottle()
    {
        using var throttle = new AiCallThrottle(1);
        Assert.IsInstanceOfType(throttle, typeof(IAiCallThrottle));
    }

    [TestMethod]
    public void ImplementsIDisposable()
    {
        var throttle = new AiCallThrottle(1);
        Assert.IsInstanceOfType(throttle, typeof(IDisposable));
        throttle.Dispose(); // Should not throw
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Concurrent usage
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ConcurrentAcquires_RespectLimit()
    {
        using var throttle = new AiCallThrottle(3);
        int maxConcurrent = 0;
        int currentConcurrent = 0;
        var tasks = new List<Task>();

        for (int i = 0; i < 10; i++)
        {
            tasks.Add(Task.Run(async () =>
            {
                await throttle.AcquireAsync();
                try
                {
                    var val = Interlocked.Increment(ref currentConcurrent);
                    InterlockedMax(ref maxConcurrent, val);
                    await Task.Delay(20); // Simulate work
                }
                finally
                {
                    Interlocked.Decrement(ref currentConcurrent);
                    throttle.Release();
                }
            }));
        }

        await Task.WhenAll(tasks);

        Assert.IsTrue(maxConcurrent <= 3,
            $"Max concurrent was {maxConcurrent}, expected <= 3");
        Assert.AreEqual(3, throttle.AvailableCount,
            "All slots should be returned after completion");
    }

    private static void InterlockedMax(ref int location, int value)
    {
        int current;
        do
        {
            current = location;
            if (value <= current) return;
        } while (Interlocked.CompareExchange(ref location, value, current) != current);
    }
}
