using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using HVO.Enterprise.Telemetry.Abstractions;
using HVO.Enterprise.Telemetry.Correlation;
using Microsoft.Extensions.DependencyInjection;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for HVO.Enterprise.Telemetry integration (#39).
/// Verifies DI registration, correlation context, NullTelemetryService
/// instrumentation capture, and operation scope lifecycle.
/// </summary>
[TestClass]
public class TelemetryIntegrationTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  DI Registration
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void DI_ResolvesITelemetryService()
    {
        var ctx = TestServiceBuilder.BuildFullyFake();
        var telemetry = ctx.ServiceProvider.GetService<ITelemetryService>();
        Assert.IsNotNull(telemetry, "ITelemetryService should be registered in DI.");
    }

    [TestMethod]
    public void DI_ResolvesOrchestrator_WithTelemetry()
    {
        var ctx = TestServiceBuilder.BuildFullyFake();
        var orchestrator = ctx.ServiceProvider.GetService<CodeReviewOrchestrator>();
        Assert.IsNotNull(orchestrator, "CodeReviewOrchestrator should resolve with telemetry injected.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  NullTelemetryService (test double) behaviour
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void NullTelemetry_RecordMetric_Captures()
    {
        var svc = new NullTelemetryService();
        svc.RecordMetric("test.metric", 42.0);

        Assert.AreEqual(1, svc.RecordedMetrics.Count);
        Assert.AreEqual("test.metric", svc.RecordedMetrics[0].Name);
        Assert.AreEqual(42.0, svc.RecordedMetrics[0].Value);
    }

    [TestMethod]
    public void NullTelemetry_TrackEvent_Captures()
    {
        var svc = new NullTelemetryService();
        svc.TrackEvent("test.event");

        Assert.AreEqual(1, svc.TrackedEvents.Count);
        Assert.AreEqual("test.event", svc.TrackedEvents[0]);
    }

    [TestMethod]
    public void NullTelemetry_TrackException_Captures()
    {
        var svc = new NullTelemetryService();
        var ex = new InvalidOperationException("boom");
        svc.TrackException(ex);

        Assert.AreEqual(1, svc.TrackedException.Count);
        Assert.AreSame(ex, svc.TrackedException[0]);
    }

    [TestMethod]
    public void NullTelemetry_StartOperation_CapturesScope()
    {
        var svc = new NullTelemetryService();

        using var scope = svc.StartOperation("TestOp");
        scope.WithTag("key1", "value1")
             .WithTag("key2", 42);

        Assert.AreEqual(1, svc.Operations.Count);
        Assert.AreEqual("TestOp", svc.Operations[0].Name);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  NullOperationScope lifecycle
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void OperationScope_WithTag_StoresTags()
    {
        var scope = new NullOperationScope("test");
        scope.WithTag("pr.id", 123)
             .WithTag("file.path", "src/Foo.cs");

        Assert.AreEqual(2, scope.Tags.Count);
        Assert.AreEqual(123, scope.Tags["pr.id"]);
        Assert.AreEqual("src/Foo.cs", scope.Tags["file.path"]);
    }

    [TestMethod]
    public void OperationScope_Succeed_SetsFlag()
    {
        var scope = new NullOperationScope("test");
        Assert.IsFalse(scope.Succeeded);

        scope.Succeed();
        Assert.IsTrue(scope.Succeeded);
        Assert.IsFalse(scope.Failed);
    }

    [TestMethod]
    public void OperationScope_Fail_SetsFlag()
    {
        var scope = new NullOperationScope("test");
        Assert.IsFalse(scope.Failed);

        scope.Fail(new Exception("error"));
        Assert.IsTrue(scope.Failed);
        Assert.IsFalse(scope.Succeeded);
    }

    [TestMethod]
    public void OperationScope_CreateChild_ReturnsNewScope()
    {
        var parent = new NullOperationScope("parent");
        var child = parent.CreateChild("child");

        Assert.IsNotNull(child);
        Assert.AreEqual("child", child.Name);
        Assert.AreNotSame(parent, child);
    }

    [TestMethod]
    public void OperationScope_FluentChaining_Works()
    {
        var scope = new NullOperationScope("chain-test");

        var result = scope
            .WithTag("a", 1)
            .WithTag("b", 2)
            .Succeed();

        Assert.AreSame(scope, result, "Fluent methods should return the same scope.");
        Assert.AreEqual(2, scope.Tags.Count);
        Assert.IsTrue(scope.Succeeded);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Correlation Context
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void CorrelationContext_BeginScope_SetsAndRestoresCurrent()
    {
        var originalValue = CorrelationContext.Current;

        using (CorrelationContext.BeginScope("test-correlation-id-123"))
        {
            Assert.AreEqual("test-correlation-id-123", CorrelationContext.Current,
                "Current should be set inside scope.");
        }

        Assert.AreEqual(originalValue, CorrelationContext.Current,
            "Current should be restored after scope disposal.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Orchestrator telemetry integration (fully-fake, no external calls)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(30_000)]
    public async Task Orchestrator_Review_RecordsTelemetryMetrics()
    {
        var fakeDevOps = new FakeDevOpsService();
        var fakeAi = new FakeCodeReviewService();
        var ctx = TestServiceBuilder.BuildFullyFake(fakeAi, fakeDevOps);
        await using var _ = ctx;

        var telemetry = ctx.ServiceProvider.GetRequiredService<ITelemetryService>() as NullTelemetryService;
        Assert.IsNotNull(telemetry, "Should resolve NullTelemetryService for inspection.");

        // Execute a simulated review
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "TestProject", "TestRepo", 1,
            simulationOnly: true);

        // Verify operation scopes were created
        Assert.IsTrue(telemetry.Operations.Count > 0,
            "At least one operation scope should have been started.");

        var orchestrateOp = telemetry.Operations
            .FirstOrDefault(o => o.Name == "OrchestrateReview");
        Assert.IsNotNull(orchestrateOp, "OrchestrateReview operation scope should exist.");
        Assert.IsTrue(orchestrateOp.Succeeded, "OrchestrateReview should have succeeded.");

        // Verify metrics were recorded
        Assert.IsTrue(telemetry.RecordedMetrics.Count > 0,
            "Some telemetry metrics should have been recorded.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  NullTelemetryStatistics
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void NullTelemetryStatistics_ReturnsDefaults()
    {
        var svc = new NullTelemetryService();
        var stats = svc.Statistics;

        Assert.IsNotNull(stats, "Statistics should not be null.");
        Assert.AreEqual(0, stats.ActivitiesCreated);
        Assert.AreEqual(0, stats.ExceptionsTracked);
        Assert.AreEqual(0, stats.QueueDepth);

        var snapshot = stats.GetSnapshot();
        Assert.IsNotNull(snapshot, "Snapshot should not be null.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Thread safety
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    [Timeout(10_000)]
    public async Task NullTelemetry_ConcurrentRecording_ThreadSafe()
    {
        var svc = new NullTelemetryService();

        var tasks = Enumerable.Range(0, 100).Select(i => Task.Run(() =>
        {
            svc.RecordMetric($"metric.{i}", i);
            svc.TrackEvent($"event.{i}");
            using var scope = svc.StartOperation($"op.{i}");
            scope.WithTag("index", i).Succeed();
        }));

        await Task.WhenAll(tasks);

        Assert.AreEqual(100, svc.RecordedMetrics.Count, "All 100 metrics should be captured.");
        Assert.AreEqual(100, svc.TrackedEvents.Count, "All 100 events should be captured.");
        Assert.AreEqual(100, svc.Operations.Count, "All 100 operations should be captured.");
    }
}
