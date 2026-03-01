namespace AiCodeReview.Services;

/// <summary>
/// System-wide throttle for concurrent AI inference calls.
/// Prevents 429 rate-limit cascades by limiting how many Azure OpenAI
/// calls can be in-flight simultaneously across all active reviews.
/// </summary>
public interface IAiCallThrottle
{
    /// <summary>
    /// Acquires a permit to make an AI call. Blocks until a slot is available
    /// or the cancellation token fires.
    /// </summary>
    Task AcquireAsync(CancellationToken cancellationToken = default);

    /// <summary>Releases a previously acquired permit.</summary>
    void Release();

    /// <summary>Number of permits currently available.</summary>
    int AvailableCount { get; }

    /// <summary>Maximum number of concurrent permits.</summary>
    int MaxCount { get; }
}

/// <summary>
/// Default implementation backed by <see cref="SemaphoreSlim"/>.
/// Registered as a singleton in DI.
/// </summary>
public class AiCallThrottle : IAiCallThrottle, IDisposable
{
    private readonly SemaphoreSlim _semaphore;

    public AiCallThrottle(int maxConcurrent)
    {
        if (maxConcurrent < 1) maxConcurrent = 1;
        _semaphore = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        MaxCount = maxConcurrent;
    }

    /// <inheritdoc />
    public async Task AcquireAsync(CancellationToken cancellationToken = default)
        => await _semaphore.WaitAsync(cancellationToken);

    /// <inheritdoc />
    public void Release() => _semaphore.Release();

    /// <inheritdoc />
    public int AvailableCount => _semaphore.CurrentCount;

    /// <inheritdoc />
    public int MaxCount { get; }

    public void Dispose() => _semaphore.Dispose();
}
