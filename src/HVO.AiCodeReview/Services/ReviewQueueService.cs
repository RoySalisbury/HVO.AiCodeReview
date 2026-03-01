using System.Threading.Channels;
using AiCodeReview.Models;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Services;

/// <summary>
/// Background service that processes queued review requests using a bounded worker pool.
/// Reviews are enqueued via <see cref="EnqueueAsync"/> and processed by up to
/// <see cref="ReviewQueueSettings.MaxConcurrentReviews"/> workers concurrently.
/// </summary>
public class ReviewQueueService : BackgroundService
{
    private readonly Channel<ReviewWorkItem> _channel;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IReviewSessionStore _sessionStore;
    private readonly ReviewQueueSettings _settings;
    private readonly ILogger<ReviewQueueService> _logger;

    public ReviewQueueService(
        IServiceScopeFactory scopeFactory,
        IReviewSessionStore sessionStore,
        IOptions<ReviewQueueSettings> settings,
        ILogger<ReviewQueueService> logger)
    {
        _scopeFactory = scopeFactory;
        _sessionStore = sessionStore;
        _settings = settings.Value;
        _logger = logger;

        _channel = Channel.CreateBounded<ReviewWorkItem>(
            new BoundedChannelOptions(_settings.MaxQueueDepth)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = false,
                SingleWriter = false,
            });
    }

    /// <summary>
    /// Enqueues a review for background processing.
    /// Returns false if the queue is full.
    /// </summary>
    public bool TryEnqueue(ReviewWorkItem workItem)
    {
        if (!_channel.Writer.TryWrite(workItem))
        {
            _logger.LogWarning("Review queue is full ({Depth}). Rejecting session {SessionId}.",
                _settings.MaxQueueDepth, workItem.Session.SessionId);
            return false;
        }

        _logger.LogInformation("Enqueued review session {SessionId} for PR #{PrId} ({Project}/{Repo}). Queue depth: ~{Depth}",
            workItem.Session.SessionId, workItem.Request.PullRequestId,
            workItem.Request.ProjectName, workItem.Request.RepositoryName,
            _channel.Reader.Count);

        return true;
    }

    /// <summary>
    /// The background processing loop. Spawns up to MaxConcurrentReviews workers
    /// that read from the channel concurrently.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "ReviewQueueService started. Workers: {Workers}, MaxQueue: {Queue}, MaxAiCalls: {AiCalls}, Timeout: {Timeout}m",
            _settings.MaxConcurrentReviews, _settings.MaxQueueDepth,
            _settings.MaxConcurrentAiCalls, _settings.SessionTimeoutMinutes);

        var workers = new Task[_settings.MaxConcurrentReviews];
        for (int i = 0; i < workers.Length; i++)
        {
            var workerId = i + 1;
            workers[i] = Task.Run(() => WorkerLoopAsync(workerId, stoppingToken), stoppingToken);
        }

        await Task.WhenAll(workers);
        _logger.LogInformation("ReviewQueueService stopped.");
    }

    private async Task WorkerLoopAsync(int workerId, CancellationToken stoppingToken)
    {
        _logger.LogInformation("Worker {WorkerId} started.", workerId);

        await foreach (var workItem in _channel.Reader.ReadAllAsync(stoppingToken))
        {
            if (stoppingToken.IsCancellationRequested) break;

            // Skip cancelled sessions
            if (workItem.Session.Status == ReviewSessionStatus.Cancelled)
            {
                _logger.LogInformation("Worker {WorkerId}: Skipping cancelled session {SessionId}.",
                    workerId, workItem.Session.SessionId);
                workItem.Completion.TrySetCanceled();
                continue;
            }

            try
            {
                await ProcessReviewAsync(workerId, workItem, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                _logger.LogInformation("Worker {WorkerId}: Shutting down.", workerId);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {WorkerId}: Unhandled error processing session {SessionId}.",
                    workerId, workItem.Session.SessionId);
            }
        }

        _logger.LogInformation("Worker {WorkerId} stopped.", workerId);
    }

    private async Task ProcessReviewAsync(int workerId, ReviewWorkItem workItem, CancellationToken stoppingToken)
    {
        var session = workItem.Session;
        var request = workItem.Request;

        _logger.LogInformation(
            "Worker {WorkerId}: Processing session {SessionId} for PR #{PrId}.",
            workerId, session.SessionId, request.PullRequestId);

        // Create a timeout-linked cancellation token
        using var timeoutCts = new CancellationTokenSource(
            TimeSpan.FromMinutes(_settings.SessionTimeoutMinutes));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            stoppingToken, timeoutCts.Token);

        try
        {
            // Create a new scope for each review (scoped services like orchestrator)
            using var scope = _scopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ICodeReviewOrchestrator>();

            var progress = new Progress<ReviewStatusUpdate>(update =>
            {
                _logger.LogInformation("[Worker {WorkerId}][{SessionId}][{Step}] {Message} ({Percent}%)",
                    workerId, session.SessionId, update.Step, update.Message, update.PercentComplete);
            });

            var response = await orchestrator.ExecuteReviewAsync(
                request.ProjectName,
                request.RepositoryName,
                request.PullRequestId,
                progress,
                request.ForceReview,
                request.SimulationOnly,
                request.ReviewDepth,
                request.ReviewStrategy,
                linkedCts.Token,
                session);

            // Store the response on the work item so the status endpoint can return it
            workItem.Response = response;
            workItem.Completion.TrySetResult(response);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            _logger.LogWarning(
                "Worker {WorkerId}: Session {SessionId} timed out after {Timeout}m.",
                workerId, session.SessionId, _settings.SessionTimeoutMinutes);

            session.Fail(new TimeoutException(
                $"Review timed out after {_settings.SessionTimeoutMinutes} minutes."));

            workItem.Response = new ReviewResponse
            {
                SessionId = session.SessionId,
                Status = "Timeout",
                ErrorMessage = $"Review timed out after {_settings.SessionTimeoutMinutes} minutes.",
            };
            workItem.Completion.TrySetResult(workItem.Response);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "Worker {WorkerId}: Session {SessionId} failed with error.",
                workerId, session.SessionId);

            session.Fail(ex);

            workItem.Response = new ReviewResponse
            {
                SessionId = session.SessionId,
                Status = "Error",
                ErrorMessage = ex.Message,
            };
            workItem.Completion.TrySetResult(workItem.Response);
        }
    }
}

/// <summary>
/// A work item in the review queue, pairing a session with its request
/// and providing a completion signal for status polling.
/// </summary>
public class ReviewWorkItem
{
    public required ReviewSession Session { get; init; }
    public required ReviewRequest Request { get; init; }

    /// <summary>
    /// Completed when the review finishes (success, failure, or timeout).
    /// The status endpoint can await this to return the final result.
    /// </summary>
    public TaskCompletionSource<ReviewResponse> Completion { get; } = new();

    /// <summary>
    /// The response, populated when the review completes.
    /// Null while the review is queued or in-progress.
    /// </summary>
    public ReviewResponse? Response { get; set; }
}
