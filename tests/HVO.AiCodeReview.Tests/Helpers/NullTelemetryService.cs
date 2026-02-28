using HVO.Enterprise.Telemetry.Abstractions;
using HVO.Enterprise.Telemetry.HealthChecks;

namespace AiCodeReview.Tests.Helpers;

/// <summary>
/// No-op telemetry service for unit and integration tests.
/// All operations are no-ops and metric recordings are silently ignored.
/// Optionally captures recorded metrics and events for assertion.
/// </summary>
public sealed class NullTelemetryService : ITelemetryService
{
    private readonly List<(string Name, double Value)> _metrics = new();
    private readonly List<string> _events = new();
    private readonly List<Exception> _exceptions = new();
    private readonly List<NullOperationScope> _operations = new();

    /// <summary>Metrics recorded during the test.</summary>
    public IReadOnlyList<(string Name, double Value)> RecordedMetrics => _metrics;

    /// <summary>Events tracked during the test.</summary>
    public IReadOnlyList<string> TrackedEvents => _events;

    /// <summary>Exceptions tracked during the test.</summary>
    public IReadOnlyList<Exception> TrackedException => _exceptions;

    /// <summary>Operations started during the test.</summary>
    public IReadOnlyList<NullOperationScope> Operations => _operations;

    public bool IsEnabled => true;

    public ITelemetryStatistics Statistics => new NullTelemetryStatistics();

    public IOperationScope StartOperation(string name)
    {
        var scope = new NullOperationScope(name);
        lock (_operations) { _operations.Add(scope); }
        return scope;
    }

    public void RecordMetric(string name, double value)
    {
        lock (_metrics) { _metrics.Add((name, value)); }
    }

    public void TrackEvent(string name)
    {
        lock (_events) { _events.Add(name); }
    }

    public void TrackException(Exception exception)
    {
        lock (_exceptions) { _exceptions.Add(exception); }
    }

    public void Start() { }
    public void Shutdown() { }
}

/// <summary>
/// No-op operation scope that records tags and final state for assertions.
/// </summary>
public sealed class NullOperationScope : IOperationScope
{
    private readonly Dictionary<string, object> _tags = new();

    public string Name { get; }
    public string CorrelationId => string.Empty;
    public System.Diagnostics.Activity? Activity => null;
    public TimeSpan Elapsed => TimeSpan.Zero;
    public bool Succeeded { get; private set; }
    public bool Failed { get; private set; }

    /// <summary>Tags applied to this scope.</summary>
    public IReadOnlyDictionary<string, object> Tags => _tags;

    public NullOperationScope(string name) => Name = name;

    public IOperationScope WithTag(string key, object? value)
    {
        if (value is not null)
            _tags[key] = value;
        return this;
    }

    public IOperationScope WithTags(IEnumerable<KeyValuePair<string, object?>> tags)
    {
        foreach (var kvp in tags)
        {
            if (kvp.Value is not null)
                _tags[kvp.Key] = kvp.Value;
        }
        return this;
    }

    public IOperationScope WithProperty(string key, Func<object?> valueFactory) => this;

    public IOperationScope Fail(Exception exception)
    {
        Failed = true;
        return this;
    }

    public IOperationScope Succeed()
    {
        Succeeded = true;
        return this;
    }

    public IOperationScope WithResult(object? result) => this;

    public void RecordException(Exception exception) { }

    public IOperationScope CreateChild(string name) => new NullOperationScope(name);

    public void Dispose() { }
}

/// <summary>
/// No-op telemetry statistics for testing.
/// </summary>
public sealed class NullTelemetryStatistics : ITelemetryStatistics
{
    public DateTimeOffset StartTime => DateTimeOffset.UtcNow;
    public long ActivitiesCreated => 0;
    public long ActivitiesCompleted => 0;
    public long ActiveActivities => 0;
    public long ExceptionsTracked => 0;
    public long EventsRecorded => 0;
    public long MetricsRecorded => 0;
    public int QueueDepth => 0;
    public int MaxQueueDepth => 0;
    public long ItemsEnqueued => 0;
    public long ItemsProcessed => 0;
    public long ItemsDropped => 0;
    public long ProcessingErrors => 0;
    public double AverageProcessingTimeMs => 0;
    public long CorrelationIdsGenerated => 0;
    public double CurrentErrorRate => 0;
    public double CurrentThroughput => 0;
    public IReadOnlyDictionary<string, ActivitySourceStatistics> PerSourceStatistics =>
        new Dictionary<string, ActivitySourceStatistics>();
    public TelemetryStatisticsSnapshot GetSnapshot() => new();
    public void Reset() { }
}
