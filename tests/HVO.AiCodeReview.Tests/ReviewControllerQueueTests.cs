using AiCodeReview.Controllers;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using HVO.Enterprise.Telemetry.Abstractions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for queue-related endpoints on <see cref="ReviewController"/>:
/// queued PostReview (202), GetStatus, GetQueue, CancelReview, Health with queue stats.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class ReviewControllerQueueTests
{
    private FakeOrchestrator _orchestrator = null!;
    private FakeDevOpsService _devOps = null!;
    private InMemoryReviewSessionStore _sessionStore = null!;

    [TestInitialize]
    public void Setup()
    {
        _orchestrator = new FakeOrchestrator();
        _devOps = new FakeDevOpsService();
        _sessionStore = new InMemoryReviewSessionStore();
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PostReview — Queued mode
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PostReview_QueueEnabled_Returns202()
    {
        var queueService = CreateQueueService();
        var controller = CreateController(queueEnabled: true, queueService: queueService);

        var result = await controller.PostReview(CreateRequest(), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(AcceptedResult));
        var accepted = (AcceptedResult)result;
        Assert.AreEqual(202, accepted.StatusCode);

        var response = (ReviewResponse)accepted.Value!;
        Assert.AreEqual("Queued", response.Status);
        Assert.AreNotEqual(Guid.Empty, response.SessionId);
    }

    [TestMethod]
    public async Task PostReview_QueueEnabled_AddsToSessionStore()
    {
        var queueService = CreateQueueService();
        var controller = CreateController(queueEnabled: true, queueService: queueService);

        await controller.PostReview(CreateRequest(), CancellationToken.None);

        Assert.AreEqual(1, _sessionStore.Count);
    }

    [TestMethod]
    public async Task PostReview_QueueFull_Returns503()
    {
        var queueService = CreateQueueService(maxQueue: 1);
        var controller = CreateController(queueEnabled: true, queueService: queueService);

        // Fill the queue
        await controller.PostReview(CreateRequest(), CancellationToken.None);

        // Next one should fail
        var result = await controller.PostReview(CreateRequest(), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(ObjectResult));
        var obj = (ObjectResult)result;
        Assert.AreEqual(503, obj.StatusCode);

        var response = (ReviewResponse)obj.Value!;
        Assert.AreEqual("QueueFull", response.Status);
    }

    [TestMethod]
    public async Task PostReview_QueueDisabled_ReturnsOk_Synchronously()
    {
        _orchestrator.Response = new ReviewResponse { Status = "Completed" };
        var controller = CreateController(queueEnabled: false);

        var result = await controller.PostReview(CreateRequest(), CancellationToken.None);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        var ok = (OkObjectResult)result;
        var response = (ReviewResponse)ok.Value!;
        Assert.AreEqual("Completed", response.Status);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetStatus
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GetStatus_KnownSession_ReturnsOk()
    {
        var session = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 };
        _sessionStore.Add(session);

        var controller = CreateController(queueEnabled: true);
        var result = controller.GetStatus(session.SessionId);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        var ok = (OkObjectResult)result;
        var response = (ReviewResponse)ok.Value!;
        Assert.AreEqual(session.SessionId, response.SessionId);
        Assert.AreEqual("Queued", response.Status);
    }

    [TestMethod]
    public void GetStatus_CompletedSession_IncludesDetails()
    {
        var session = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 };
        session.Complete("Approved", vote: 10);
        _sessionStore.Add(session);

        var controller = CreateController(queueEnabled: true);
        var result = controller.GetStatus(session.SessionId);

        var ok = (OkObjectResult)result;
        var response = (ReviewResponse)ok.Value!;
        Assert.AreEqual("Completed", response.Status);
        Assert.AreEqual("Approved", response.Recommendation);
        Assert.AreEqual(10, response.Vote);
    }

    [TestMethod]
    public void GetStatus_FailedSession_IncludesError()
    {
        var session = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 };
        session.Fail(new InvalidOperationException("Something broke"));
        _sessionStore.Add(session);

        var controller = CreateController(queueEnabled: true);
        var result = controller.GetStatus(session.SessionId);

        var ok = (OkObjectResult)result;
        var response = (ReviewResponse)ok.Value!;
        Assert.AreEqual("Failed", response.Status);
        Assert.AreEqual("Something broke", response.ErrorMessage);
    }

    [TestMethod]
    public void GetStatus_UnknownSession_Returns404()
    {
        var controller = CreateController(queueEnabled: true);
        var result = controller.GetStatus(Guid.NewGuid());

        Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
    }

    [TestMethod]
    public void GetStatus_QueueDisabled_Returns404()
    {
        var controller = CreateController(queueEnabled: false);
        var result = controller.GetStatus(Guid.NewGuid());

        Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetQueue
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GetQueue_Enabled_ReturnsActiveSessions()
    {
        _sessionStore.Add(new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 });
        _sessionStore.Add(new ReviewSession { Project = "P", Repository = "R", PullRequestId = 2 });

        var controller = CreateController(queueEnabled: true);
        var result = controller.GetQueue();

        var ok = (OkObjectResult)result;
        var response = (QueueStatusResponse)ok.Value!;

        Assert.IsTrue(response.Enabled);
        Assert.AreEqual(2, response.QueuedCount);
        Assert.AreEqual(2, response.Sessions.Count);
    }

    [TestMethod]
    public void GetQueue_Disabled_ReturnsDisabledStatus()
    {
        var controller = CreateController(queueEnabled: false);
        var result = controller.GetQueue();

        var ok = (OkObjectResult)result;
        var response = (QueueStatusResponse)ok.Value!;

        Assert.IsFalse(response.Enabled);
        Assert.AreEqual(0, response.Sessions.Count);
    }

    [TestMethod]
    public void GetQueue_MixedStatuses_OnlyShowsActive()
    {
        var queued = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 };
        var inProgress = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 2 };
        inProgress.Start();
        var completed = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 3 };
        completed.Complete();

        _sessionStore.Add(queued);
        _sessionStore.Add(inProgress);
        _sessionStore.Add(completed);

        var controller = CreateController(queueEnabled: true);
        var result = controller.GetQueue();

        var ok = (OkObjectResult)result;
        var response = (QueueStatusResponse)ok.Value!;

        Assert.AreEqual(1, response.QueuedCount);
        Assert.AreEqual(1, response.InProgressCount);
        Assert.AreEqual(2, response.Sessions.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CancelReview
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void CancelReview_QueuedSession_ReturnsOk()
    {
        var session = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 };
        _sessionStore.Add(session);

        var controller = CreateController(queueEnabled: true);
        var result = controller.CancelReview(session.SessionId);

        Assert.IsInstanceOfType(result, typeof(OkObjectResult));
        Assert.AreEqual(ReviewSessionStatus.Cancelled, session.Status);
    }

    [TestMethod]
    public void CancelReview_InProgressSession_Returns409()
    {
        var session = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 };
        session.Start();
        _sessionStore.Add(session);

        var controller = CreateController(queueEnabled: true);
        var result = controller.CancelReview(session.SessionId);

        Assert.IsInstanceOfType(result, typeof(ConflictObjectResult));
    }

    [TestMethod]
    public void CancelReview_UnknownSession_Returns404()
    {
        var controller = CreateController(queueEnabled: true);
        var result = controller.CancelReview(Guid.NewGuid());

        Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
    }

    [TestMethod]
    public void CancelReview_QueueDisabled_Returns404()
    {
        var controller = CreateController(queueEnabled: false);
        var result = controller.CancelReview(Guid.NewGuid());

        Assert.IsInstanceOfType(result, typeof(NotFoundObjectResult));
    }

    [TestMethod]
    public void CancelReview_CompletedSession_Returns409()
    {
        var session = new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 };
        session.Complete();
        _sessionStore.Add(session);

        var controller = CreateController(queueEnabled: true);
        var result = controller.CancelReview(session.SessionId);

        Assert.IsInstanceOfType(result, typeof(ConflictObjectResult));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Health — with queue stats
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Health_QueueEnabled_IncludesQueueStats()
    {
        _sessionStore.Add(new ReviewSession { Project = "P", Repository = "R", PullRequestId = 1 });
        _sessionStore.Add(new ReviewSession { Project = "P", Repository = "R", PullRequestId = 2 });

        var controller = CreateController(queueEnabled: true);
        var result = await controller.Health(CancellationToken.None);

        var obj = (ObjectResult)result;
        Assert.AreEqual(200, obj.StatusCode);
        var dict = (Dictionary<string, object>)obj.Value!;
        Assert.IsTrue(dict.ContainsKey("queue"), "Should include queue stats");
    }

    [TestMethod]
    public async Task Health_QueueDisabled_NoQueueStats()
    {
        var controller = CreateController(queueEnabled: false);
        var result = await controller.Health(CancellationToken.None);

        var obj = (ObjectResult)result;
        var dict = (Dictionary<string, object>)obj.Value!;
        Assert.IsFalse(dict.ContainsKey("queue"), "Should NOT include queue stats when disabled");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private ReviewController CreateController(
        bool queueEnabled,
        ReviewQueueService? queueService = null)
    {
        var settings = new ReviewQueueSettings
        {
            Enabled = queueEnabled,
            MaxConcurrentReviews = 3,
            MaxQueueDepth = 50,
        };

        var controller = new ReviewController(
            _orchestrator,
            _devOps,
            new NullTelemetryService(),
            NullLogger<ReviewController>.Instance,
            Options.Create(settings),
            queueEnabled ? queueService : null,
            queueEnabled ? _sessionStore : null);

        // Set up a minimal ControllerContext so Url.Action works
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        controller.Url = new FakeUrlHelper();

        return controller;
    }

    private ReviewQueueService CreateQueueService(int maxQueue = 50)
    {
        var settings = new ReviewQueueSettings
        {
            Enabled = true,
            MaxConcurrentReviews = 3,
            MaxQueueDepth = maxQueue,
            MaxConcurrentAiCalls = 8,
            SessionTimeoutMinutes = 30,
        };

        var services = new ServiceCollection();
        services.AddSingleton<ICodeReviewOrchestrator>(_orchestrator);
        var provider = services.BuildServiceProvider();

        return new ReviewQueueService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _sessionStore,
            Options.Create(settings),
            NullLogger<ReviewQueueService>.Instance);
    }

    private static ReviewRequest CreateRequest(
        string project = "TestProject",
        string repo = "TestRepo",
        int prId = 1) => new()
        {
            ProjectName = project,
            RepositoryName = repo,
            PullRequestId = prId,
        };

    /// <summary>Minimal IUrlHelper that returns a simple string for Action.</summary>
    private sealed class FakeUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new ActionContext
        {
            HttpContext = new DefaultHttpContext(),
            RouteData = new Microsoft.AspNetCore.Routing.RouteData(),
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor(),
        };

        public string? Action(UrlActionContext urlActionContext)
            => $"/api/review/status/{urlActionContext?.Values?.GetType().GetProperty("sessionId")?.GetValue(urlActionContext.Values)}";

        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => true;
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class FakeOrchestrator : ICodeReviewOrchestrator
    {
        public ReviewResponse Response { get; set; } = new() { Status = "Completed" };

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
            return Task.FromResult(Response);
        }
    }
}
