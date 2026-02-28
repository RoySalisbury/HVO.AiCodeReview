namespace AiCodeReview.Services;

/// <summary>
/// Singleton signal that broadcasts a global "cooldown" when any caller
/// receives an HTTP 429 rate-limit response.  All concurrent callers wait
/// until the cooldown expires before making further API requests.
/// <para>
/// Thread-safe via <c>Interlocked</c> operations — no locks.
/// </para>
/// </summary>
public interface IGlobalRateLimitSignal
{
    /// <summary>
    /// Signal a global cooldown because a 429 was just received.
    /// All waiters should delay at least <paramref name="duration"/>.
    /// If an existing cooldown extends further into the future, it is kept.
    /// </summary>
    void SignalCooldown(TimeSpan duration);

    /// <summary>
    /// Wait until any active global cooldown has elapsed.
    /// Returns immediately if no cooldown is active.
    /// </summary>
    Task WaitIfCoolingDownAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// True when a cooldown is currently active (diagnostics / logging only).
    /// </summary>
    bool IsCoolingDown { get; }

    /// <summary>
    /// The UTC time at which the current cooldown expires
    /// (or <see cref="DateTimeOffset.MinValue"/> if none is active).
    /// </summary>
    DateTimeOffset CooldownExpiresUtc { get; }
}

/// <inheritdoc />
public class GlobalRateLimitSignal : IGlobalRateLimitSignal
{
    private readonly ILogger<GlobalRateLimitSignal> _logger;

    /// <summary>
    /// Stores the UTC tick (100-ns units) at which the cooldown expires.
    /// 0 means no cooldown active.  Updated via <c>Interlocked</c>.
    /// </summary>
    private long _cooldownExpiresUtcTicks;

    public GlobalRateLimitSignal(ILogger<GlobalRateLimitSignal> logger)
    {
        _logger = logger;
    }

    /// <inheritdoc />
    public void SignalCooldown(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
            return;

        var proposedExpiry = DateTimeOffset.UtcNow.Add(duration).UtcTicks;

        // Atomically set if our expiry extends further than the current one
        while (true)
        {
            long current = Interlocked.Read(ref _cooldownExpiresUtcTicks);
            if (proposedExpiry <= current)
                break; // existing cooldown already extends further

            if (Interlocked.CompareExchange(ref _cooldownExpiresUtcTicks, proposedExpiry, current) == current)
            {
                _logger.LogWarning(
                    "[RateLimit] Global cooldown activated: all API calls paused for {Duration}s (until {Expiry:HH:mm:ss} UTC)",
                    (int)duration.TotalSeconds,
                    new DateTimeOffset(proposedExpiry, TimeSpan.Zero));
                break;
            }
            // CAS failed (another thread updated), retry compare
        }
    }

    /// <inheritdoc />
    public async Task WaitIfCoolingDownAsync(CancellationToken cancellationToken = default)
    {
        long expiryTicks = Interlocked.Read(ref _cooldownExpiresUtcTicks);
        if (expiryTicks == 0)
            return;

        var now = DateTimeOffset.UtcNow.UtcTicks;
        if (now >= expiryTicks)
            return;

        var remaining = TimeSpan.FromTicks(expiryTicks - now);
        _logger.LogInformation(
            "[RateLimit] Waiting {Remaining:F1}s for global cooldown to expire...",
            remaining.TotalSeconds);

        await Task.Delay(remaining, cancellationToken);
    }

    /// <inheritdoc />
    public bool IsCoolingDown
    {
        get
        {
            long expiryTicks = Interlocked.Read(ref _cooldownExpiresUtcTicks);
            return expiryTicks > 0 && DateTimeOffset.UtcNow.UtcTicks < expiryTicks;
        }
    }

    /// <inheritdoc />
    public DateTimeOffset CooldownExpiresUtc
    {
        get
        {
            long ticks = Interlocked.Read(ref _cooldownExpiresUtcTicks);
            return ticks == 0
                ? DateTimeOffset.MinValue
                : new DateTimeOffset(ticks, TimeSpan.Zero);
        }
    }
}
