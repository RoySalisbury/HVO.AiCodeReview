using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.AspNetCore.Mvc;

namespace AiCodeReview.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ReviewController : ControllerBase
{
    private readonly ICodeReviewOrchestrator _orchestrator;
    private readonly IAzureDevOpsService _devOpsService;
    private readonly ILogger<ReviewController> _logger;

    public ReviewController(
        ICodeReviewOrchestrator orchestrator,
        IAzureDevOpsService devOpsService,
        ILogger<ReviewController> logger)
    {
        _orchestrator = orchestrator;
        _devOpsService = devOpsService;
        _logger = logger;
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
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> PostReview([FromBody] ReviewRequest request)
    {
        _logger.LogInformation(
            "Review requested: {Project}/{Repo} PR #{PrId}",
            request.ProjectName, request.RepositoryName, request.PullRequestId);

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
            progress);

        if (response.Status == "Error")
        {
            _logger.LogError("Review failed for PR #{PrId}: {Error}",
                request.PullRequestId, response.ErrorMessage);
            return StatusCode(500, response);
        }

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
    /// Health check endpoint.
    /// </summary>
    [HttpGet("health")]
    public IActionResult Health()
    {
        return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
    }
}
