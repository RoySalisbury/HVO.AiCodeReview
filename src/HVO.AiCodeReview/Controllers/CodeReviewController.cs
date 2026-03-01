using AiCodeReview.Models;
using AiCodeReview.Services;
using HVO.Enterprise.Telemetry.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReviewController : ControllerBase
{
    private readonly ICodeReviewOrchestrator _orchestrator;
    private readonly IDevOpsService _devOpsService;
    private readonly ILogger<ReviewController> _logger;
    private readonly ITelemetryService _telemetry;
    private readonly ReviewQueueService? _queueService;
    private readonly IReviewSessionStore? _sessionStore;
    private readonly ReviewQueueSettings _queueSettings;

    public ReviewController(
        ICodeReviewOrchestrator orchestrator,
        IDevOpsService devOpsService,
        ITelemetryService telemetry,
        ILogger<ReviewController> logger,
        IOptions<ReviewQueueSettings> queueSettings,
        ReviewQueueService? queueService = null,
        IReviewSessionStore? sessionStore = null)
    {
        _orchestrator = orchestrator;
        _devOpsService = devOpsService;
        _telemetry = telemetry;
        _logger = logger;
        _queueSettings = queueSettings.Value;
        _queueService = queueService;
        _sessionStore = sessionStore;
    }

    /// <summary>
    /// Execute an AI code review for a pull request.
    /// The service will analyze the PR diff, post inline comments and a summary,
    /// and add itself as a reviewer with a vote.
    /// </summary>
    /// <param name="request">Project, repository, and PR ID to review.</param>
    /// <returns>Review result with status, recommendation, and summary.</returns>
    [HttpPost]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> PostReview([FromBody] ReviewRequest request, CancellationToken cancellationToken)
    {
        using var opScope = _telemetry.StartOperation("API.PostReview")
            .WithTag("pr.id", request.PullRequestId)
            .WithTag("pr.project", request.ProjectName)
            .WithTag("pr.repository", request.RepositoryName)
            .WithTag("review.depth", request.ReviewDepth.ToString())
            .WithTag("review.simulation", request.SimulationOnly);

        // Create a session to track this review end-to-end
        var session = new ReviewSession
        {
            Project = request.ProjectName,
            Repository = request.RepositoryName,
            PullRequestId = request.PullRequestId,
            ForceReview = request.ForceReview,
            SimulationOnly = request.SimulationOnly,
            ReviewDepth = request.ReviewDepth,
            ReviewStrategy = request.ReviewStrategy,
        };

        _logger.LogInformation(
            "Review requested: {Project}/{Repo} PR #{PrId}{Simulation} [Depth={Depth}, Strategy={Strategy}] SessionId={SessionId}",
            request.ProjectName, request.RepositoryName, request.PullRequestId,
            request.SimulationOnly ? " [SIMULATION]" : "",
            request.ReviewDepth, request.ReviewStrategy, session.SessionId);

        // ── Queued mode: enqueue and return 202 Accepted ────────────────
        if (_queueSettings.Enabled && _queueService != null && _sessionStore != null)
        {
            _sessionStore.Add(session);

            var workItem = new ReviewWorkItem
            {
                Session = session,
                Request = request,
            };

            if (!_queueService.TryEnqueue(workItem))
            {
                session.Fail(new InvalidOperationException("Queue is full"));
                opScope.WithTag("review.outcome", "QueueFull").Fail(new InvalidOperationException("Queue full"));
                return StatusCode(503, new ReviewResponse
                {
                    SessionId = session.SessionId,
                    Status = "QueueFull",
                    ErrorMessage = $"Review queue is full ({_queueSettings.MaxQueueDepth} max). Try again later.",
                });
            }

            opScope.WithTag("review.outcome", "Queued").Succeed();
            _telemetry.RecordMetric("api.reviews_queued", 1);

            var statusUrl = Url.Action(nameof(GetStatus), new { sessionId = session.SessionId })
                            ?? $"/api/review/status/{session.SessionId}";

            return Accepted(statusUrl, new ReviewResponse
            {
                SessionId = session.SessionId,
                Status = "Queued",
                Summary = "Review has been queued for processing.",
            });
        }

        // ── Synchronous mode: execute immediately ───────────────────────

        // V1: Use a logging-only progress handler. In V2, this would be an SSE stream writer.
        var progress = new Progress<ReviewStatusUpdate>(update =>
        {
            _logger.LogInformation("[{Step}] {Message} ({Percent}%)",
                update.Step, update.Message, update.PercentComplete);
        });

        var response = await _orchestrator.ExecuteReviewAsync(
            request.ProjectName,
            request.RepositoryName,
            request.PullRequestId,
            progress,
            request.ForceReview,
            request.SimulationOnly,
            request.ReviewDepth,
            request.ReviewStrategy,
            cancellationToken,
            session);

        if (response.Status == "Error")
        {
            _logger.LogError("Review failed for PR #{PrId}: {Error}",
                request.PullRequestId, response.ErrorMessage);
            opScope.WithTag("review.outcome", "Error").Fail(new InvalidOperationException(response.ErrorMessage));
            _telemetry.RecordMetric("api.review_errors", 1);
            return StatusCode(500, response);
        }

        opScope.WithTag("review.outcome", response.Status).Succeed();
        _telemetry.RecordMetric("api.reviews_completed", 1);
        return Ok(response);
    }

    /// <summary>
    /// Get review metrics and full review history for a pull request.
    /// All data comes from PR properties — not affected by tag deletion.
    /// </summary>
    [HttpGet("metrics")]
    [ProducesResponseType(typeof(ReviewMetricsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetMetrics(
        [FromQuery] string project,
        [FromQuery] string repository,
        [FromQuery] int pullRequestId)
    {
        if (string.IsNullOrWhiteSpace(project) || string.IsNullOrWhiteSpace(repository) || pullRequestId <= 0)
        {
            _logger.LogWarning("Invalid metrics request: project='{Project}', repository='{Repository}', pullRequestId={PrId}",
                project, repository, pullRequestId);
            return BadRequest("project, repository, and pullRequestId are required.");
        }

        _logger.LogInformation("Metrics requested for {Project}/{Repo} PR #{PrId}",
            project, repository, pullRequestId);

        var metadata = await _devOpsService.GetReviewMetadataAsync(project, repository, pullRequestId);
        var history = await _devOpsService.GetReviewHistoryAsync(project, repository, pullRequestId);

        var response = new ReviewMetricsResponse
        {
            PullRequestId = pullRequestId,
            ReviewCount = metadata.ReviewCount,
            LastReviewedAtUtc = metadata.ReviewedAtUtc,
            LastSourceCommit = metadata.LastReviewedSourceCommit,
            LastIteration = metadata.LastReviewedIteration,
            WasDraft = metadata.WasDraft,
            VoteSubmitted = metadata.VoteSubmitted,
            History = history,
            // Aggregate token stats
            TotalPromptTokens = history.Where(h => h.PromptTokens.HasValue).Sum(h => h.PromptTokens!.Value),
            TotalCompletionTokens = history.Where(h => h.CompletionTokens.HasValue).Sum(h => h.CompletionTokens!.Value),
            TotalTokensUsed = history.Where(h => h.TotalTokens.HasValue).Sum(h => h.TotalTokens!.Value),
            TotalAiDurationMs = history.Where(h => h.AiDurationMs.HasValue).Sum(h => h.AiDurationMs!.Value),
            TotalDurationMs = history.Where(h => h.TotalDurationMs.HasValue).Sum(h => h.TotalDurationMs!.Value),
        };

        return Ok(response);
    }

    /// <summary>
    /// Health check endpoint. Verifies the service is running and can reach Azure DevOps.
    /// </summary>
    [HttpGet("health")]
    public async Task<IActionResult> Health(CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, object>
        {
            ["status"] = "healthy",
            ["timestamp"] = DateTime.UtcNow,
        };

        try
        {
            // Verify Azure DevOps connectivity by resolving the service identity
            var identity = await _devOpsService.ResolveServiceIdentityAsync();
            result["azureDevOps"] = identity != null ? "connected" : "identity-unknown";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Azure DevOps health check failed");
            result["status"] = "degraded";
            result["azureDevOps"] = "unreachable";
        }

        if (_queueSettings.Enabled && _sessionStore != null)
        {
            result["queue"] = new
            {
                enabled = true,
                queued = _sessionStore.QueuedCount,
                inProgress = _sessionStore.InProgressCount,
                total = _sessionStore.Count,
            };
        }

        var statusCode = result["status"] is "healthy" ? 200 : 503;
        return StatusCode(statusCode, result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Queue Endpoints (only functional when queue is enabled)
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Get the status of a queued/in-progress/completed review session.
    /// </summary>
    [HttpGet("status/{sessionId:guid}")]
    [ProducesResponseType(typeof(ReviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public IActionResult GetStatus(Guid sessionId)
    {
        if (_sessionStore == null)
            return NotFound(new { error = "Queue is not enabled." });

        var session = _sessionStore.Get(sessionId);
        if (session == null)
            return NotFound(new { error = $"Session {sessionId} not found." });

        var response = new ReviewResponse
        {
            SessionId = session.SessionId,
            Status = session.Status.ToString(),
        };

        // If the session is completed/failed, include details
        if (session.Status is ReviewSessionStatus.Completed or ReviewSessionStatus.Failed)
        {
            response.ErrorMessage = session.ErrorMessage;
            response.Recommendation = session.Verdict;
            response.Vote = session.Vote;
        }

        return Ok(response);
    }

    /// <summary>
    /// List all queued and in-progress review sessions.
    /// </summary>
    [HttpGet("queue")]
    [ProducesResponseType(typeof(QueueStatusResponse), StatusCodes.Status200OK)]
    public IActionResult GetQueue()
    {
        if (_sessionStore == null)
        {
            return Ok(new QueueStatusResponse
            {
                Enabled = false,
                Sessions = Array.Empty<QueuedSessionInfo>(),
            });
        }

        var active = _sessionStore.GetActive();

        return Ok(new QueueStatusResponse
        {
            Enabled = _queueSettings.Enabled,
            MaxConcurrentReviews = _queueSettings.MaxConcurrentReviews,
            MaxQueueDepth = _queueSettings.MaxQueueDepth,
            QueuedCount = _sessionStore.QueuedCount,
            InProgressCount = _sessionStore.InProgressCount,
            Sessions = active.Select(s => new QueuedSessionInfo
            {
                SessionId = s.SessionId,
                PullRequestId = s.PullRequestId,
                Project = s.Project,
                Repository = s.Repository,
                Status = s.Status.ToString(),
                RequestedAtUtc = s.RequestedAtUtc,
            }).ToArray(),
        });
    }

    /// <summary>
    /// Cancel a queued (not yet started) review session.
    /// The cancelled work item remains in the Channel until a worker dequeues it,
    /// at which point it is skipped. This means queue capacity is not immediately
    /// reclaimed on cancel — a known trade-off for simplicity.
    /// </summary>
    [HttpDelete("{sessionId:guid}")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public IActionResult CancelReview(Guid sessionId)
    {
        if (_sessionStore == null)
            return NotFound(new { error = "Queue is not enabled." });

        var session = _sessionStore.Get(sessionId);
        if (session == null)
            return NotFound(new { error = $"Session {sessionId} not found." });

        if (session.Status != ReviewSessionStatus.Queued)
            return Conflict(new { error = $"Session is {session.Status} and cannot be cancelled. Only Queued sessions can be cancelled." });

        if (_sessionStore.TryCancelQueued(sessionId))
        {
            _logger.LogInformation("Cancelled queued session {SessionId}.", sessionId);
            return Ok(new { status = "Cancelled", sessionId });
        }

        return Conflict(new { error = "Session could not be cancelled (race condition — may have started processing)." });
    }
}
