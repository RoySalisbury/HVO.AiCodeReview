using AiCodeReview.Controllers;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using HVO.Enterprise.Telemetry.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for <see cref="ReviewController"/> — validates request handling,
/// error mapping, and metric recording without hitting real services.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ReviewControllerTests
{
    private FakeOrchestrator _orchestrator = null!;
    private FakeDevOpsService _devOps = null!;
    private ReviewController _controller = null!;

    [TestInitialize]
    public void Setup()
    {
        _orchestrator = new FakeOrchestrator();
        _devOps = new FakeDevOpsService();
        _controller = new ReviewController(
            _orchestrator,
            _devOps,
            new NullTelemetryService(),
            NullLogger<ReviewController>.Instance,
            Options.Create(new ReviewQueueSettings()));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PostReview — success
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PostReview_SuccessfulReview_ReturnsOk()
    {
        _orchestrator.Response = new ReviewResponse { Status = "Completed", Recommendation = "Approved" };
        var request = CreateRequest();

        var result = await _controller.PostReview(request, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        var ok = (OkObjectResult)result;
        var response = (ReviewResponse)ok.Value!;
        Assert.AreEqual("Completed", response.Status);
    }

    [TestMethod]
    public async Task PostReview_SuccessfulReview_CallsOrchestrator()
    {
        _orchestrator.Response = new ReviewResponse { Status = "Completed" };
        var request = CreateRequest(project: "MyProject", repo: "MyRepo", prId: 42);

        await _controller.PostReview(request, CancellationToken.None);

        Assert.AreEqual("MyProject", _orchestrator.LastProject);
        Assert.AreEqual("MyRepo", _orchestrator.LastRepository);
        Assert.AreEqual(42, _orchestrator.LastPullRequestId);
    }

    [TestMethod]
    public async Task PostReview_ForceReviewFlag_PassedToOrchestrator()
    {
        _orchestrator.Response = new ReviewResponse { Status = "Completed" };
        var request = CreateRequest();
        request.ForceReview = true;

        await _controller.PostReview(request, CancellationToken.None);

        Assert.IsTrue(_orchestrator.LastForceReview);
    }

    [TestMethod]
    public async Task PostReview_SimulationFlag_PassedToOrchestrator()
    {
        _orchestrator.Response = new ReviewResponse { Status = "Completed" };
        var request = CreateRequest();
        request.SimulationOnly = true;

        await _controller.PostReview(request, CancellationToken.None);

        Assert.IsTrue(_orchestrator.LastSimulationOnly);
    }

    [TestMethod]
    public async Task PostReview_ReviewDepth_PassedToOrchestrator()
    {
        _orchestrator.Response = new ReviewResponse { Status = "Completed" };
        var request = CreateRequest();
        request.ReviewDepth = ReviewDepth.Deep;

        await _controller.PostReview(request, CancellationToken.None);

        Assert.AreEqual(ReviewDepth.Deep, _orchestrator.LastReviewDepth);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PostReview — error
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PostReview_ErrorResponse_Returns500()
    {
        _orchestrator.Response = new ReviewResponse
        {
            Status = "Error",
            ErrorMessage = "Something went wrong"
        };
        var request = CreateRequest();

        var result = await _controller.PostReview(request, CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(ObjectResult));
        var obj = (ObjectResult)result;
        Assert.AreEqual(500, obj.StatusCode);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetMetrics — success
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetMetrics_ValidParams_ReturnsOk()
    {
        _devOps.SeedPullRequest("proj", "repo", new PullRequestInfo
        {
            PullRequestId = 1,
            Title = "Test PR"
        });

        var result = await _controller.GetMetrics("proj", "repo", 1);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        var ok = (OkObjectResult)result;
        var response = ok.Value as ReviewMetricsResponse;
        Assert.IsNotNull(response);
        Assert.AreEqual(1, response.PullRequestId);
    }

    [TestMethod]
    public async Task GetMetrics_AggregatesTokenStats()
    {
        _devOps.SeedPullRequest("proj", "repo", new PullRequestInfo { PullRequestId = 1, Title = "Test" });
        // Seed history entries with token usage
        await _devOps.AppendReviewHistoryAsync("proj", "repo", 1, new ReviewHistoryEntry
        {
            ReviewedAtUtc = DateTime.UtcNow,
            PromptTokens = 100,
            CompletionTokens = 50,
            TotalTokens = 150,
            AiDurationMs = 500,
            TotalDurationMs = 1000
        });
        await _devOps.AppendReviewHistoryAsync("proj", "repo", 1, new ReviewHistoryEntry
        {
            ReviewedAtUtc = DateTime.UtcNow,
            PromptTokens = 200,
            CompletionTokens = 100,
            TotalTokens = 300,
            AiDurationMs = 600,
            TotalDurationMs = 1200
        });

        var result = await _controller.GetMetrics("proj", "repo", 1);

        var ok = (OkObjectResult)result;
        var response = (ReviewMetricsResponse)ok.Value!;
        Assert.AreEqual(300, response.TotalPromptTokens);
        Assert.AreEqual(150, response.TotalCompletionTokens);
        Assert.AreEqual(450, response.TotalTokensUsed);
        Assert.AreEqual(1100, response.TotalAiDurationMs);
        Assert.AreEqual(2200, response.TotalDurationMs);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetMetrics — validation
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetMetrics_EmptyProject_ReturnsBadRequest()
    {
        var result = await _controller.GetMetrics("", "repo", 1);
        Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public async Task GetMetrics_NullRepository_ReturnsBadRequest()
    {
        var result = await _controller.GetMetrics("proj", null!, 1);
        Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public async Task GetMetrics_ZeroPrId_ReturnsBadRequest()
    {
        var result = await _controller.GetMetrics("proj", "repo", 0);
        Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
    }

    [TestMethod]
    public async Task GetMetrics_NegativePrId_ReturnsBadRequest()
    {
        var result = await _controller.GetMetrics("proj", "repo", -1);
        Assert.IsInstanceOfType(result, typeof(BadRequestObjectResult));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Health — success
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Health_Connected_ReturnsHealthy()
    {
        var result = await _controller.Health(CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(ObjectResult));
        var obj = (ObjectResult)result;
        Assert.AreEqual(200, obj.StatusCode);
        var dict = (Dictionary<string, object>)obj.Value!;
        Assert.AreEqual("healthy", dict["status"]);
        Assert.AreEqual("connected", dict["azureDevOps"]);
    }

    [TestMethod]
    public async Task Health_DevOpsUnreachable_ReturnsDegraded()
    {
        var failingDevOps = new FailingDevOpsService();
        var controller = new ReviewController(
            _orchestrator,
            failingDevOps,
            new NullTelemetryService(),
            NullLogger<ReviewController>.Instance,
            Options.Create(new ReviewQueueSettings()));

        var result = await controller.Health(CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(ObjectResult));
        var obj = (ObjectResult)result;
        Assert.AreEqual(503, obj.StatusCode);
        var dict = (Dictionary<string, object>)obj.Value!;
        Assert.AreEqual("degraded", dict["status"]);
        Assert.AreEqual("unreachable", dict["azureDevOps"]);
    }

    [TestMethod]
    public async Task Health_NullIdentity_ReportsUnknown()
    {
        var nullIdentityDevOps = new NullIdentityDevOpsService();
        var controller = new ReviewController(
            _orchestrator,
            nullIdentityDevOps,
            new NullTelemetryService(),
            NullLogger<ReviewController>.Instance,
            Options.Create(new ReviewQueueSettings()));

        var result = await controller.Health(CancellationToken.None);

        var obj = (ObjectResult)result;
        Assert.AreEqual(200, obj.StatusCode);
        var dict = (Dictionary<string, object>)obj.Value!;
        Assert.AreEqual("healthy", dict["status"]);
        Assert.AreEqual("identity-unknown", dict["azureDevOps"]);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static ReviewRequest CreateRequest(
        string project = "TestProject",
        string repo = "TestRepo",
        int prId = 1) => new()
    {
        ProjectName = project,
        RepositoryName = repo,
        PullRequestId = prId
    };

    /// <summary>
    /// Minimal fake orchestrator that records calls and returns a preset response.
    /// </summary>
    private sealed class FakeOrchestrator : ICodeReviewOrchestrator
    {
        public ReviewResponse Response { get; set; } = new() { Status = "Completed" };
        public string? LastProject { get; private set; }
        public string? LastRepository { get; private set; }
        public int LastPullRequestId { get; private set; }
        public bool LastForceReview { get; private set; }
        public bool LastSimulationOnly { get; private set; }
        public ReviewDepth LastReviewDepth { get; private set; }

        public Task<ReviewResponse> ExecuteReviewAsync(
            string project, string repository, int pullRequestId,
            IProgress<ReviewStatusUpdate>? progress = null,
            bool forceReview = false, bool simulationOnly = false,
            ReviewDepth reviewDepth = ReviewDepth.Standard,
            ReviewStrategy reviewStrategy = ReviewStrategy.FileByFile,
            CancellationToken cancellationToken = default,
            ReviewSession? session = null)
        {
            LastProject = project;
            LastRepository = repository;
            LastPullRequestId = pullRequestId;
            LastForceReview = forceReview;
            LastSimulationOnly = simulationOnly;
            LastReviewDepth = reviewDepth;
            return Task.FromResult(Response);
        }
    }

    /// <summary>
    /// Fake DevOps service that throws on ResolveServiceIdentityAsync (simulating connectivity failure).
    /// </summary>
    private sealed class FailingDevOpsService : FakeDevOpsService
    {
        public override Task<string?> ResolveServiceIdentityAsync()
            => throw new HttpRequestException("Connection refused");
    }

    /// <summary>
    /// Fake DevOps service that returns null identity.
    /// </summary>
    private sealed class NullIdentityDevOpsService : FakeDevOpsService
    {
        public override Task<string?> ResolveServiceIdentityAsync()
            => Task.FromResult<string?>(null);
    }
}
