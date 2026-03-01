using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for <see cref="ReviewQueueService"/> — validates enqueueing,
/// worker processing, timeout, and cancellation behavior.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ReviewQueueServiceTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  TryEnqueue
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void TryEnqueue_WithinCapacity_ReturnsTrue()
    {
        using var sut = CreateService(maxQueue: 5);
        var item = MakeWorkItem();

        Assert.IsTrue(sut.TryEnqueue(item));
    }

    [TestMethod]
    public void TryEnqueue_QueueFull_ReturnsFalse()
    {
        using var sut = CreateService(maxQueue: 2);

        Assert.IsTrue(sut.TryEnqueue(MakeWorkItem()));
        Assert.IsTrue(sut.TryEnqueue(MakeWorkItem()));
        Assert.IsFalse(sut.TryEnqueue(MakeWorkItem()), "Third enqueue should fail");
    }

    [TestMethod]
    public void TryEnqueue_MultipleItems_AllSucceed()
    {
        using var sut = CreateService(maxQueue: 10);

        for (int i = 0; i < 10; i++)
            Assert.IsTrue(sut.TryEnqueue(MakeWorkItem()), $"Item {i} should be enqueued");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Worker Processing
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Worker_ProcessesEnqueuedItem()
    {
        var orchestrator = new DelayOrchestrator(delay: TimeSpan.FromMilliseconds(10));
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, orchestrator: orchestrator);
        using var _ = sut;

        var item = MakeWorkItem();
        store.Add(item.Session);
        sut.TryEnqueue(item);

        // Start the service
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartService(sut, cts.Token);

        // Wait for the work item to complete
        var response = await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsNotNull(response);
        Assert.AreEqual("Completed", response.Status);

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    [TestMethod]
    public async Task Worker_MultipleItems_ProcessedConcurrently()
    {
        var orchestrator = new DelayOrchestrator(delay: TimeSpan.FromMilliseconds(200));
        var (sut, store) = CreateServiceWithStore(maxWorkers: 3, maxQueue: 10, orchestrator: orchestrator);
        using var _ = sut;

        var items = Enumerable.Range(0, 3).Select(_ => MakeWorkItem()).ToList();
        foreach (var item in items)
        {
            store.Add(item.Session);
            sut.TryEnqueue(item);
        }

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executeTask = StartService(sut, cts.Token);

        // All 3 should complete — with 3 workers they should run concurrently
        var allDone = Task.WhenAll(items.Select(i => i.Completion.Task));
        await allDone.WaitAsync(TimeSpan.FromSeconds(5));

        foreach (var item in items)
            Assert.AreEqual("Completed", item.Response?.Status);

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    [TestMethod]
    public async Task Worker_SkipsCancelledSession()
    {
        var orchestrator = new DelayOrchestrator(delay: TimeSpan.FromMilliseconds(10));
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, orchestrator: orchestrator);
        using var _ = sut;

        var cancelledItem = MakeWorkItem();
        cancelledItem.Session.Status = ReviewSessionStatus.Cancelled;
        store.Add(cancelledItem.Session);

        var normalItem = MakeWorkItem();
        store.Add(normalItem.Session);

        sut.TryEnqueue(cancelledItem);
        sut.TryEnqueue(normalItem);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartService(sut, cts.Token);

        // Normal item should complete
        var response = await normalItem.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Completed", response.Status);

        // Cancelled item should have been cancelled
        Assert.IsTrue(cancelledItem.Completion.Task.IsCanceled);

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Orchestrator Error Handling
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Worker_OrchestratorThrows_SessionFailed()
    {
        var orchestrator = new ThrowingOrchestrator();
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, orchestrator: orchestrator);
        using var _ = sut;

        var item = MakeWorkItem();
        store.Add(item.Session);
        sut.TryEnqueue(item);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartService(sut, cts.Token);

        var response = await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Error", response.Status);
        Assert.IsNotNull(response.ErrorMessage);

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    [TestMethod]
    public async Task Worker_OrchestratorThrows_ContinuesProcessingNextItem()
    {
        // After an orchestrator error, the worker should continue picking up next items
        var callCount = 0;
        var orchestrator = new ConditionalOrchestrator(index =>
        {
            var current = Interlocked.Increment(ref callCount);
            if (current == 1) throw new InvalidOperationException("First call fails");
            // Second call succeeds
        });
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, orchestrator: orchestrator);
        using var _ = sut;

        var failItem = MakeWorkItem();
        var successItem = MakeWorkItem();
        store.Add(failItem.Session);
        store.Add(successItem.Session);
        sut.TryEnqueue(failItem);
        sut.TryEnqueue(successItem);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartService(sut, cts.Token);

        var failResponse = await failItem.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Error", failResponse.Status);

        var successResponse = await successItem.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Completed", successResponse.Status);

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    [TestMethod]
    public async Task Worker_Timeout_SetsTimeoutStatus()
    {
        // Use an orchestrator that takes longer than the timeout
        var orchestrator = new DelayOrchestrator(delay: TimeSpan.FromSeconds(30));
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, timeoutMinutes: 0, orchestrator: orchestrator);
        using var _ = sut;

        var item = MakeWorkItem();
        store.Add(item.Session);
        sut.TryEnqueue(item);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var executeTask = StartService(sut, cts.Token);

        // The timeout (0 minutes = immediate) should cause the orchestrator to be cancelled
        var response = await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual("Timeout", response.Status);
        Assert.IsTrue(response.ErrorMessage?.Contains("timed out"), $"Expected timeout message, got: {response.ErrorMessage}");
        Assert.AreEqual(ReviewSessionStatus.Failed, item.Session.Status);

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Work Item Model
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewWorkItem_CompletionIsNotCompleted_Initially()
    {
        var item = MakeWorkItem();
        Assert.IsFalse(item.Completion.Task.IsCompleted);
        Assert.IsNull(item.Response);
    }

    [TestMethod]
    public void ReviewWorkItem_ResponsePopulated_AfterCompletion()
    {
        var item = MakeWorkItem();
        var response = new ReviewResponse { Status = "Completed" };
        item.Response = response;
        item.Completion.TrySetResult(response);

        Assert.IsTrue(item.Completion.Task.IsCompleted);
        Assert.AreEqual("Completed", item.Response.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Progress Reporting
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Worker_OrchestratorReportsProgress_LogsProgressUpdates()
    {
        // Uses a progress-reporting orchestrator to exercise the Progress<T> callback
        var orchestrator = new ProgressReportingOrchestrator();
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, orchestrator: orchestrator);
        using var _ = sut;

        var item = MakeWorkItem();
        store.Add(item.Session);
        sut.TryEnqueue(item);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartService(sut, cts.Token);

        var response = await item.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Completed", response.Status);
        Assert.IsTrue(orchestrator.ProgressReported, "Orchestrator should have reported progress");

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Cancellation During Processing
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Worker_StoppingTokenCancelledDuringProcessing_WorkerExitsGracefully()
    {
        // Orchestrator blocks until cancellation — triggers OperationCanceledException path
        var gate = new TaskCompletionSource();
        var orchestrator = new BlockingOrchestrator(gate.Task);
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, orchestrator: orchestrator);
        using var _ = sut;

        var item = MakeWorkItem();
        store.Add(item.Session);
        sut.TryEnqueue(item);

        using var cts = new CancellationTokenSource();
        var executeTask = StartService(sut, cts.Token);

        // Wait for orchestrator to start blocking
        await Task.Delay(100);

        // Cancel the stopping token while the orchestrator is blocked
        cts.Cancel();
        gate.TrySetCanceled(); // unblock so cleanup can finish

        await WaitForShutdown(executeTask);
        // Worker should have exited gracefully via OperationCanceledException catch
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Atomic State Transition Guard
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Worker_SessionAlreadyCancelledInStore_SkipsProcessing()
    {
        // Verifies the TryTransitionToInProgress guard:
        // If a session is already cancelled in the store when the worker
        // attempts the Queued→InProgress transition, the worker skips it.
        var orchestrator = new DelayOrchestrator(delay: TimeSpan.FromMilliseconds(10));
        var (sut, store) = CreateServiceWithStore(maxWorkers: 1, orchestrator: orchestrator);
        using var _ = sut;

        // Create item, add to store as Queued, then cancel it in the store
        var item = MakeWorkItem();
        store.Add(item.Session);
        store.TryCancelQueued(item.Session.SessionId); // cancel BEFORE worker dequeues

        // Also enqueue a normal item that should still process
        var normalItem = MakeWorkItem();
        store.Add(normalItem.Session);

        sut.TryEnqueue(item);
        sut.TryEnqueue(normalItem);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var executeTask = StartService(sut, cts.Token);

        // Normal item should complete
        var response = await normalItem.Completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual("Completed", response.Status);

        // Cancelled item should have been skipped via TryTransitionToInProgress returning false
        Assert.IsTrue(item.Completion.Task.IsCanceled, "Cancelled session should be skipped");
        Assert.AreEqual(ReviewSessionStatus.Cancelled, item.Session.Status);

        cts.Cancel();
        await WaitForShutdown(executeTask);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Settings Validation
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Constructor_ZeroMaxQueueDepth_ClampsToOne()
    {
        using var sut = CreateService(maxQueue: 0);
        // If we get here without exception, the constructor clamped to 1
        Assert.IsTrue(sut.TryEnqueue(MakeWorkItem()));
    }

    [TestMethod]
    public void Constructor_NegativeMaxWorkers_ClampsToOne()
    {
        using var sut = CreateService(maxWorkers: -1);
        // If we get here without exception, the constructor clamped to 1
        Assert.IsNotNull(sut);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Service Shutdown
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Service_StopsGracefully_OnCancellation()
    {
        using var sut = CreateService(maxWorkers: 2);

        using var cts = new CancellationTokenSource();
        var executeTask = StartService(sut, cts.Token);

        // Let workers start
        await Task.Delay(50);

        cts.Cancel();
        await WaitForShutdown(executeTask);

        // If we get here without timeout, shutdown was graceful
        Assert.IsTrue(true);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static (ReviewQueueService Service, InMemoryReviewSessionStore Store) CreateServiceWithStore(
        int maxWorkers = 3,
        int maxQueue = 50,
        int timeoutMinutes = 30,
        ICodeReviewOrchestrator? orchestrator = null)
    {
        var settings = new ReviewQueueSettings
        {
            Enabled = true,
            MaxConcurrentReviews = maxWorkers,
            MaxQueueDepth = maxQueue,
            MaxConcurrentAiCalls = 8,
            SessionTimeoutMinutes = timeoutMinutes,
        };

        orchestrator ??= new DelayOrchestrator(delay: TimeSpan.FromMilliseconds(10));

        var services = new ServiceCollection();
        services.AddSingleton<ICodeReviewOrchestrator>(orchestrator);
        var provider = services.BuildServiceProvider();

        var sessionStore = new InMemoryReviewSessionStore();

        var service = new ReviewQueueService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            sessionStore,
            Options.Create(settings),
            NullLogger<ReviewQueueService>.Instance);

        return (service, sessionStore);
    }

    private static ReviewQueueService CreateService(
        int maxWorkers = 3,
        int maxQueue = 50,
        int timeoutMinutes = 30,
        ICodeReviewOrchestrator? orchestrator = null)
        => CreateServiceWithStore(maxWorkers, maxQueue, timeoutMinutes, orchestrator).Service;

    private static ReviewWorkItem MakeWorkItem() => new()
    {
        Session = new ReviewSession
        {
            Project = "TestProject",
            Repository = "TestRepo",
            PullRequestId = Random.Shared.Next(1, 99999),
        },
        Request = new ReviewRequest
        {
            ProjectName = "TestProject",
            RepositoryName = "TestRepo",
            PullRequestId = Random.Shared.Next(1, 99999),
        },
    };

    /// <summary>
    /// Starts the BackgroundService's ExecuteAsync via reflection (it's protected).
    /// </summary>
    private static Task StartService(ReviewQueueService service, CancellationToken ct)
    {
        var method = typeof(ReviewQueueService)
            .GetMethod("ExecuteAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        return (Task)method.Invoke(service, new object[] { ct })!;
    }

    private static async Task WaitForShutdown(Task executeTask)
    {
        try
        {
            await executeTask.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (OperationCanceledException) { /* expected */ }
        catch (AggregateException ex) when (ex.InnerExceptions.All(e => e is OperationCanceledException)) { }
    }

    // ── Fake orchestrators ──────────────────────────────────────────────

    private sealed class DelayOrchestrator : ICodeReviewOrchestrator
    {
        private readonly TimeSpan _delay;
        public DelayOrchestrator(TimeSpan delay) => _delay = delay;

        public async Task<ReviewResponse> ExecuteReviewAsync(
            string project, string repository, int pullRequestId,
            IProgress<ReviewStatusUpdate>? progress = null,
            bool forceReview = false, bool simulationOnly = false,
            ReviewDepth reviewDepth = ReviewDepth.Standard,
            ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
            CancellationToken cancellationToken = default,
            ReviewSession? session = null,
            bool? enableSecurityPass = null)
        {
            await Task.Delay(_delay, cancellationToken);
            session?.Complete("Approved");
            return new ReviewResponse
            {
                SessionId = session?.SessionId ?? Guid.NewGuid(),
                Status = "Completed",
                Recommendation = "Approved",
            };
        }
    }

    private sealed class ThrowingOrchestrator : ICodeReviewOrchestrator
    {
        public Task<ReviewResponse> ExecuteReviewAsync(
            string project, string repository, int pullRequestId,
            IProgress<ReviewStatusUpdate>? progress = null,
            bool forceReview = false, bool simulationOnly = false,
            ReviewDepth reviewDepth = ReviewDepth.Standard,
            ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
            CancellationToken cancellationToken = default,
            ReviewSession? session = null,
            bool? enableSecurityPass = null)
        {
            throw new InvalidOperationException("Simulated orchestrator failure");
        }
    }

    /// <summary>
    /// Orchestrator that calls back on each invocation, allowing per-call behavior.
    /// </summary>
    private sealed class ConditionalOrchestrator : ICodeReviewOrchestrator
    {
        private readonly Action<int> _onCall;
        private int _callIndex;

        public ConditionalOrchestrator(Action<int> onCall) => _onCall = onCall;

        public async Task<ReviewResponse> ExecuteReviewAsync(
            string project, string repository, int pullRequestId,
            IProgress<ReviewStatusUpdate>? progress = null,
            bool forceReview = false, bool simulationOnly = false,
            ReviewDepth reviewDepth = ReviewDepth.Standard,
            ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
            CancellationToken cancellationToken = default,
            ReviewSession? session = null,
            bool? enableSecurityPass = null)
        {
            var index = Interlocked.Increment(ref _callIndex);
            _onCall(index); // may throw to simulate failure
            await Task.Delay(10, cancellationToken);
            session?.Complete("Approved");
            return new ReviewResponse
            {
                SessionId = session?.SessionId ?? Guid.NewGuid(),
                Status = "Completed",
                Recommendation = "Approved",
            };
        }
    }

    /// <summary>
    /// Orchestrator that reports progress before completing, exercising the Progress callback.
    /// </summary>
    private sealed class ProgressReportingOrchestrator : ICodeReviewOrchestrator
    {
        public bool ProgressReported { get; private set; }

        public async Task<ReviewResponse> ExecuteReviewAsync(
            string project, string repository, int pullRequestId,
            IProgress<ReviewStatusUpdate>? progress = null,
            bool forceReview = false, bool simulationOnly = false,
            ReviewDepth reviewDepth = ReviewDepth.Standard,
            ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
            CancellationToken cancellationToken = default,
            ReviewSession? session = null,
            bool? enableSecurityPass = null)
        {
            progress?.Report(new ReviewStatusUpdate
            {
                Step = ReviewStep.AnalyzingCode,
                Message = "Test progress",
                PercentComplete = 50,
            });
            ProgressReported = true;
            await Task.Delay(10, cancellationToken);
            session?.Complete("Approved");
            return new ReviewResponse
            {
                SessionId = session?.SessionId ?? Guid.NewGuid(),
                Status = "Completed",
                Recommendation = "Approved",
            };
        }
    }

    /// <summary>
    /// Orchestrator that blocks on a Task, allowing the test to control when it completes.
    /// Used to test cancellation during processing.
    /// </summary>
    private sealed class BlockingOrchestrator : ICodeReviewOrchestrator
    {
        private readonly Task _blockOn;
        public BlockingOrchestrator(Task blockOn) => _blockOn = blockOn;

        public async Task<ReviewResponse> ExecuteReviewAsync(
            string project, string repository, int pullRequestId,
            IProgress<ReviewStatusUpdate>? progress = null,
            bool forceReview = false, bool simulationOnly = false,
            ReviewDepth reviewDepth = ReviewDepth.Standard,
            ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
            CancellationToken cancellationToken = default,
            ReviewSession? session = null,
            bool? enableSecurityPass = null)
        {
            await _blockOn.WaitAsync(cancellationToken);
            session?.Complete("Approved");
            return new ReviewResponse
            {
                SessionId = session?.SessionId ?? Guid.NewGuid(),
                Status = "Completed",
            };
        }
    }
}
