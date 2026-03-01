using System.Net;
using System.Text;
using System.Text.Json;
using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using HVO.Enterprise.Telemetry.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for <see cref="AzureDevOpsService"/> using a mock <see cref="HttpMessageHandler"/>
/// to intercept HTTP calls and return fake responses, covering key parsing and branch logic.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AzureDevOpsServiceHttpTests
{
    private const string Org = "TestOrg";
    private const string Project = "TestProject";
    private const string Repo = "TestRepo";
    private const int PrId = 42;

    // ═══════════════════════════════════════════════════════════════════
    //  GetPullRequestAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetPullRequestAsync_ParsesAllFields()
    {
        var json = JsonSerializer.Serialize(new
        {
            pullRequestId = 42,
            title = "Test PR",
            description = "A test description",
            sourceRefName = "refs/heads/feature",
            targetRefName = "refs/heads/main",
            createdBy = new { displayName = "Test User" },
            creationDate = "2026-01-15T10:30:00Z",
            status = "active",
            isDraft = true,
            lastMergeSourceCommit = new { commitId = "abc123" },
            lastMergeTargetCommit = new { commitId = "def456" },
            reviewers = new[]
            {
                new { id = "r1", displayName = "Reviewer 1", vote = 10 }
            }
        });

        var svc = CreateService(json);
        var pr = await svc.GetPullRequestAsync(Project, Repo, PrId);

        Assert.AreEqual(42, pr.PullRequestId);
        Assert.AreEqual("Test PR", pr.Title);
        Assert.AreEqual("A test description", pr.Description);
        Assert.AreEqual("refs/heads/feature", pr.SourceBranch);
        Assert.AreEqual("refs/heads/main", pr.TargetBranch);
        Assert.AreEqual("Test User", pr.CreatedBy);
        Assert.AreEqual("active", pr.Status);
        Assert.IsTrue(pr.IsDraft);
        Assert.AreEqual("abc123", pr.LastMergeSourceCommit);
        Assert.AreEqual("def456", pr.LastMergeTargetCommit);
        Assert.AreEqual(1, pr.Reviewers.Count);
        Assert.AreEqual("Reviewer 1", pr.Reviewers[0].DisplayName);
        Assert.AreEqual(10, pr.Reviewers[0].Vote);
    }

    [TestMethod]
    public async Task GetPullRequestAsync_NoDraft_DefaultsFalse()
    {
        var json = JsonSerializer.Serialize(new
        {
            pullRequestId = 42,
            title = "PR",
            sourceRefName = "refs/heads/f",
            targetRefName = "refs/heads/m",
            createdBy = new { displayName = "u" },
            creationDate = "2026-01-15T10:00:00Z",
            status = "active",
            // No isDraft, no lastMerge*, no reviewers
        });

        var svc = CreateService(json);
        var pr = await svc.GetPullRequestAsync(Project, Repo, PrId);

        Assert.IsFalse(pr.IsDraft);
        Assert.AreEqual("", pr.LastMergeSourceCommit);
        Assert.AreEqual(0, pr.Reviewers.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  HasReviewTagAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task HasReviewTagAsync_TagPresent_ReturnsTrue()
    {
        var json = JsonSerializer.Serialize(new
        {
            value = new[] { new { name = "ai-reviewed" } }
        });

        var svc = CreateService(json);
        var result = await svc.HasReviewTagAsync(Project, Repo, PrId);
        Assert.IsTrue(result);
    }

    [TestMethod]
    public async Task HasReviewTagAsync_TagAbsent_ReturnsFalse()
    {
        var json = JsonSerializer.Serialize(new
        {
            value = new[] { new { name = "other-tag" } }
        });

        var svc = CreateService(json);
        var result = await svc.HasReviewTagAsync(Project, Repo, PrId);
        Assert.IsFalse(result);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetReviewMetadataAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetReviewMetadataAsync_ReturnsMetadataFromProperties()
    {
        // ADO stores PR properties as { "value": { "Key": { "$type":"System.String", "$value":"..." } } }
        var reviewedAt = new DateTime(2026, 1, 15, 10, 0, 0, DateTimeKind.Utc);
        var json = $$"""
        {
            "value": {
                "AiCodeReview.ReviewCount": { "$type": "System.String", "$value": "2" },
                "AiCodeReview.LastSourceCommit": { "$type": "System.String", "$value": "abc123" },
                "AiCodeReview.LastIteration": { "$type": "System.String", "$value": "3" },
                "AiCodeReview.VoteSubmitted": { "$type": "System.String", "$value": "True" },
                "AiCodeReview.ReviewedAtUtc": { "$type": "System.String", "$value": "{{reviewedAt:O}}" }
            }
        }
        """;

        var svc = CreateService(json);
        var meta = await svc.GetReviewMetadataAsync(Project, Repo, PrId);

        Assert.AreEqual(2, meta.ReviewCount);
        Assert.AreEqual("abc123", meta.LastReviewedSourceCommit);
        Assert.AreEqual(3, meta.LastReviewedIteration);
        Assert.IsTrue(meta.VoteSubmitted);
    }

    [TestMethod]
    public async Task GetReviewMetadataAsync_NoProperty_ReturnsDefault()
    {
        var json = JsonSerializer.Serialize(new { value = new Dictionary<string, object>() });

        var svc = CreateService(json);
        var meta = await svc.GetReviewMetadataAsync(Project, Repo, PrId);

        Assert.AreEqual(0, meta.ReviewCount);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetIterationCountAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetIterationCountAsync_ReturnsCount()
    {
        var json = JsonSerializer.Serialize(new
        {
            count = 5,
            value = new[]
            {
                new { id = 1 }, new { id = 2 }, new { id = 3 },
                new { id = 4 }, new { id = 5 }
            }
        });

        var svc = CreateService(json);
        var count = await svc.GetIterationCountAsync(Project, Repo, PrId);
        Assert.AreEqual(5, count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PostCommentThreadAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PostCommentThreadAsync_Success()
    {
        var handler = new FakeHandler((req) =>
        {
            Assert.IsTrue(req.RequestUri!.ToString().Contains("threads"));
            Assert.AreEqual(HttpMethod.Post, req.Method);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        var svc = CreateService(handler);
        await svc.PostCommentThreadAsync(Project, Repo, PrId, "Review comment");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PostInlineCommentThreadAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task PostInlineCommentThreadAsync_IncludesThreadContext()
    {
        var handler = new FakeHandler((req) =>
        {
            var body = req.Content!.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("threadContext"), "Should include thread context");
            Assert.IsTrue(body.Contains("src/File.cs"), "Should include file path");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        var svc = CreateService(handler);
        await svc.PostInlineCommentThreadAsync(Project, Repo, PrId,
            "src/File.cs", 10, 15, "Issue found here");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  AddReviewerAsync (vote)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AddReviewerAsync_PutsVote()
    {
        int callCount = 0;
        var handler = new FakeHandler((req) =>
        {
            callCount++;
            if (req.RequestUri!.ToString().Contains("connectionData"))
            {
                // Identity resolution (must include providerDisplayName)
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"authenticatedUser":{"id":"identity-123","providerDisplayName":"Test Bot"}}""",
                        Encoding.UTF8, "application/json"),
                };
            }
            Assert.AreEqual(HttpMethod.Put, req.Method);
            Assert.IsTrue(req.RequestUri.ToString().Contains("reviewers"));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        });

        var svc = CreateService(handler);
        await svc.AddReviewerAsync(Project, Repo, PrId, 5);
        Assert.IsTrue(callCount >= 2, "Should call identity + reviewer endpoints");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetLinkedWorkItemIdsAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetLinkedWorkItemIdsAsync_ParsesIds()
    {
        var json = JsonSerializer.Serialize(new
        {
            value = new[] { new { id = "100" }, new { id = "200" } }
        });

        var svc = CreateService(json);
        var ids = await svc.GetLinkedWorkItemIdsAsync(Project, Repo, PrId);

        Assert.AreEqual(2, ids.Count);
        Assert.IsTrue(ids.Contains(100));
        Assert.IsTrue(ids.Contains(200));
    }

    [TestMethod]
    public async Task GetLinkedWorkItemIdsAsync_HttpError_ReturnsEmpty()
    {
        var handler = new FakeHandler((_) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var svc = CreateService(handler);
        var ids = await svc.GetLinkedWorkItemIdsAsync(Project, Repo, PrId);

        Assert.AreEqual(0, ids.Count, "Should return empty list on error");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetWorkItemAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetWorkItemAsync_ParsesFields()
    {
        var json = JsonSerializer.Serialize(new
        {
            id = 100,
            fields = new Dictionary<string, string>
            {
                ["System.WorkItemType"] = "User Story",
                ["System.Title"] = "Login Feature",
                ["System.State"] = "Active",
                ["System.Description"] = "<p>A login feature</p>",
                ["Microsoft.VSTS.Common.AcceptanceCriteria"] = "<li>Login form shown</li>",
            }
        });

        var svc = CreateService(json);
        var wi = await svc.GetWorkItemAsync(Project, 100);

        Assert.IsNotNull(wi);
        Assert.AreEqual(100, wi.Id);
        Assert.AreEqual("User Story", wi.WorkItemType);
        Assert.AreEqual("Login Feature", wi.Title);
        Assert.AreEqual("Active", wi.State);
        Assert.IsTrue(wi.Description!.Contains("A login feature"));
        Assert.IsTrue(wi.AcceptanceCriteria!.Contains("Login form shown"));
    }

    [TestMethod]
    public async Task GetWorkItemAsync_HttpError_ReturnsNull()
    {
        var handler = new FakeHandler((_) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var svc = CreateService(handler);
        var wi = await svc.GetWorkItemAsync(Project, 999);

        Assert.IsNull(wi);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CountReviewSummaryCommentsAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task CountReviewSummaryCommentsAsync_CountsOnlySummaryThreads()
    {
        var json = JsonSerializer.Serialize(new
        {
            value = new object[]
            {
                // Summary thread (no threadContext, starts with "## Code Review")
                new
                {
                    comments = new[]
                    {
                        new { content = "## Code Review\nSummary here" }
                    }
                },
                // Regular thread (no threadContext, doesn't start with ##)
                new
                {
                    comments = new[]
                    {
                        new { content = "Regular comment" }
                    }
                },
                // Inline thread (has threadContext) — should be skipped
                new
                {
                    threadContext = new { filePath = "a.cs" },
                    comments = new[]
                    {
                        new { content = "## Code Review inline" }
                    }
                },
                // Re-review summary
                new
                {
                    comments = new[]
                    {
                        new { content = "## Re-Review\nSecond review" }
                    }
                },
            }
        });

        var svc = CreateService(json);
        var count = await svc.CountReviewSummaryCommentsAsync(Project, Repo, PrId);

        Assert.AreEqual(2, count, "Should count Code Review + Re-Review summaries, skip inline and non-summary");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UpdatePrDescriptionAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task UpdatePrDescriptionAsync_SendsPatch()
    {
        var handler = new FakeHandler((req) =>
        {
            Assert.AreEqual("PATCH", req.Method.Method);
            var body = req.Content!.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("New description"), body);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        await svc.UpdatePrDescriptionAsync(Project, Repo, PrId, "New description");
    }

    [TestMethod]
    public async Task UpdatePrDescriptionAsync_FailureLogsWarning()
    {
        var handler = new FakeHandler((_) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Access denied"),
            });

        var svc = CreateService(handler);
        // Should not throw — just logs a warning
        await svc.UpdatePrDescriptionAsync(Project, Repo, PrId, "desc");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  AddReviewTagAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AddReviewTagAsync_SendsPost()
    {
        var handler = new FakeHandler((req) =>
        {
            Assert.AreEqual(HttpMethod.Post, req.Method);
            Assert.IsTrue(req.RequestUri!.ToString().Contains("labels"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        await svc.AddReviewTagAsync(Project, Repo, PrId);
    }

    [TestMethod]
    public async Task AddReviewTagAsync_FailureLogsWarning()
    {
        var handler = new FakeHandler((_) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Forbidden"),
            });

        var svc = CreateService(handler);
        // Should not throw
        await svc.AddReviewTagAsync(Project, Repo, PrId);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ResolveServiceIdentityAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ResolveServiceIdentityAsync_ConfiguredId_ReturnsIt()
    {
        var settings = new AzureDevOpsSettings
        {
            Organization = Org,
            PersonalAccessToken = "fake",
            ServiceAccountIdentityId = "configured-id-123",
        };

        var svc = CreateService("{}", settings);
        var id = await svc.ResolveServiceIdentityAsync();
        Assert.AreEqual("configured-id-123", id);
    }

    [TestMethod]
    public async Task ResolveServiceIdentityAsync_AutoDiscovery_ParsesFromApi()
    {
        var handler = new FakeHandler((req) =>
        {
            if (req.RequestUri!.ToString().Contains("connectionData"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"authenticatedUser":{"id":"discovered-id-456","providerDisplayName":"Bot Account"}}""",
                        Encoding.UTF8, "application/json"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var settings = new AzureDevOpsSettings
        {
            Organization = Org,
            PersonalAccessToken = "fake",
            // No ServiceAccountIdentityId — triggers auto-discovery
        };

        var svc = CreateService(handler, settings);
        var id = await svc.ResolveServiceIdentityAsync();
        Assert.AreEqual("discovered-id-456", id);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetWorkItemCommentsAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetWorkItemCommentsAsync_ParsesComments()
    {
        var json = JsonSerializer.Serialize(new
        {
            comments = new[]
            {
                new
                {
                    text = "First comment",
                    createdBy = new { displayName = "Author1" },
                    createdDate = "2026-01-15T10:00:00Z",
                },
                new
                {
                    text = "Second comment",
                    createdBy = new { displayName = "Author2" },
                    createdDate = "2026-01-16T10:00:00Z",
                }
            }
        });

        var svc = CreateService(json);
        var comments = await svc.GetWorkItemCommentsAsync(Project, 100);

        Assert.AreEqual(2, comments.Count);
        Assert.AreEqual("First comment", comments[0].Text);
        Assert.AreEqual("Author1", comments[0].Author);
    }

    [TestMethod]
    public async Task GetWorkItemCommentsAsync_HttpFailure_ReturnsEmpty()
    {
        var handler = new FakeHandler((_) =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        var svc = CreateService(handler);
        var comments = await svc.GetWorkItemCommentsAsync(Project, 100);
        Assert.AreEqual(0, comments.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SetReviewMetadataAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task SetReviewMetadataAsync_SendsPatch()
    {
        var handler = new FakeHandler((req) =>
        {
            Assert.AreEqual("PATCH", req.Method.Method);
            Assert.IsTrue(req.RequestUri!.ToString().Contains("properties"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        await svc.SetReviewMetadataAsync(Project, Repo, PrId, new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow,
            LastReviewedSourceCommit = "abc",
            LastReviewedIteration = 1,
            VoteSubmitted = true,
        });
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Constructor
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Constructor_SetsAuthHeader()
    {
        var handler = new FakeHandler((_) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HttpClient(handler);
        var settings = new AzureDevOpsSettings { Organization = Org, PersonalAccessToken = "my-pat" };

        _ = new AzureDevOpsService(
            client,
            Options.Create(settings),
            new NullTelemetryService(),
            LoggerFactory.Create(b => { }).CreateLogger<AzureDevOpsService>());

        Assert.IsNotNull(client.DefaultRequestHeaders.Authorization);
        Assert.AreEqual("Basic", client.DefaultRequestHeaders.Authorization.Scheme);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetPullRequestChangesAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetPullRequestChangesAsync_ParsesFileChanges()
    {
        int callIndex = 0;
        var handler = new FakeHandler((req) =>
        {
            callIndex++;
            var url = req.RequestUri!.ToString();

            // 1. Iterations endpoint
            if (url.Contains("/iterations") && !url.Contains("/changes"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"value":[{"id":1},{"id":2}]}""",
                        Encoding.UTF8, "application/json"),
                };
            }

            // 2. Changes for last iteration
            if (url.Contains("/iterations/2/changes"))
            {
                var json = JsonSerializer.Serialize(new
                {
                    changeEntries = new[]
                    {
                        new { item = new { path = "/src/Foo.cs", isFolder = false }, changeType = "edit" },
                        new { item = new { path = "/src/Bar.cs", isFolder = false }, changeType = "add" },
                        new { item = new { path = "/images/logo.png", isFolder = false }, changeType = "add" },
                        new { item = new { path = "/src/folder", isFolder = true }, changeType = "edit" },
                    }
                });
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json"),
                };
            }

            // 3. File content requests — return plain text
            if (url.Contains("/items?"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("line1\nline2\nline3", Encoding.UTF8, "text/plain"),
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        var prInfo = new PullRequestInfo
        {
            LastMergeSourceCommit = "src-commit",
            LastMergeTargetCommit = "tgt-commit",
        };

        var changes = await svc.GetPullRequestChangesAsync(Project, Repo, PrId, prInfo);

        // Should skip folder and binary file (.png)
        Assert.AreEqual(2, changes.Count, "Should have 2 file changes (folder + binary skipped)");
        Assert.AreEqual("/src/Foo.cs", changes[0].FilePath);
        Assert.AreEqual("edit", changes[0].ChangeType);
        Assert.AreEqual("/src/Bar.cs", changes[1].FilePath);
        Assert.AreEqual("add", changes[1].ChangeType);
    }

    [TestMethod]
    public async Task GetPullRequestChangesAsync_AddedFile_HasChangedLineRanges()
    {
        var handler = new FakeHandler((req) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/iterations") && !url.Contains("/changes"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":[{"id":1}]}""", Encoding.UTF8, "application/json"),
                };
            }
            if (url.Contains("/iterations/1/changes"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"changeEntries":[{"item":{"path":"/new.cs","isFolder":false},"changeType":"add"}]}""",
                        Encoding.UTF8, "application/json"),
                };
            }
            if (url.Contains("/items?"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("a\nb\nc", Encoding.UTF8, "text/plain"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        var changes = await svc.GetPullRequestChangesAsync(Project, Repo, PrId,
            new PullRequestInfo { LastMergeSourceCommit = "abc" });

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual("add", changes[0].ChangeType);
        Assert.IsNotNull(changes[0].ChangedLineRanges);
        Assert.AreEqual(1, changes[0].ChangedLineRanges!.Count);
        Assert.AreEqual((1, 3), changes[0].ChangedLineRanges![0]);
    }

    [TestMethod]
    public async Task GetPullRequestChangesAsync_DeletedFile_NoContentFetch()
    {
        var handler = new FakeHandler((req) =>
        {
            var url = req.RequestUri!.ToString();
            if (url.Contains("/iterations") && !url.Contains("/changes"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""{"value":[{"id":1}]}""", Encoding.UTF8, "application/json"),
                };
            }
            if (url.Contains("/iterations/1/changes"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"changeEntries":[{"item":{"path":"/old.cs","isFolder":false},"changeType":"delete"}]}""",
                        Encoding.UTF8, "application/json"),
                };
            }
            if (url.Contains("/items?"))
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("original content\nline2", Encoding.UTF8, "text/plain"),
                };
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        var changes = await svc.GetPullRequestChangesAsync(Project, Repo, PrId,
            new PullRequestInfo { LastMergeTargetCommit = "tgt" });

        Assert.AreEqual(1, changes.Count);
        Assert.AreEqual("delete", changes[0].ChangeType);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetExistingReviewThreadsAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetExistingReviewThreadsAsync_ParsesInlineThreads()
    {
        var json = """
        {
            "value": [
                {
                    "id": 1, "status": 1,
                    "threadContext": {
                        "filePath": "/src/Foo.cs",
                        "rightFileStart": { "line": 10, "offset": 1 },
                        "rightFileEnd": { "line": 15, "offset": 1 }
                    },
                    "comments": [
                        { "content": "**Bug.** Null reference possible _[AiCodeReview]_", "commentType": 1 },
                        { "content": "Fixed it", "commentType": 1, "author": { "displayName": "Dev" }, "publishedDate": "2026-01-15T10:00:00Z", "isDeleted": false }
                    ]
                },
                {
                    "id": 2, "status": 4,
                    "comments": [{ "content": "**Note**: General feedback", "commentType": 1 }]
                },
                {
                    "id": 3, "status": 1,
                    "threadContext": {
                        "filePath": "/src/Bar.cs",
                        "rightFileStart": { "line": 1, "offset": 1 },
                        "rightFileEnd": { "line": 1, "offset": 1 }
                    },
                    "comments": [{ "content": "Regular comment", "commentType": 1 }]
                }
            ]
        }
        """;

        var svc = CreateService(json);
        var threads = await svc.GetExistingReviewThreadsAsync(Project, Repo, PrId, "AiCodeReview");

        Assert.AreEqual(1, threads.Count, "Should find only the inline AI thread");
        Assert.AreEqual("/src/Foo.cs", threads[0].FilePath);
        Assert.AreEqual(10, threads[0].StartLine);
        Assert.AreEqual(15, threads[0].EndLine);
        Assert.IsTrue(threads[0].IsAiGenerated);
        Assert.AreEqual("Bug", threads[0].LeadIn);
        Assert.AreEqual(1, threads[0].Replies.Count);
        Assert.AreEqual("Dev", threads[0].Replies[0].Author);
        Assert.AreEqual("Fixed it", threads[0].Replies[0].Content);
    }

    [TestMethod]
    public async Task GetExistingReviewThreadsAsync_SkipsDeletedAndSystemReplies()
    {
        var json = """
        {
            "value": [{
                "id": 10, "status": 1,
                "threadContext": {
                    "filePath": "/a.cs",
                    "rightFileStart": { "line": 5, "offset": 1 },
                    "rightFileEnd": { "line": 5, "offset": 1 }
                },
                "comments": [
                    { "content": "**Concern.** Issue here", "commentType": 1 },
                    { "content": "System message", "commentType": 2 },
                    { "content": "Deleted reply", "commentType": 1, "isDeleted": true },
                    { "content": "", "commentType": 1 },
                    { "content": "Real human reply", "commentType": 1, "author": { "displayName": "Human" }, "publishedDate": "2026-01-15T10:00:00Z" }
                ]
            }]
        }
        """;

        var svc = CreateService(json);
        var threads = await svc.GetExistingReviewThreadsAsync(Project, Repo, PrId);

        Assert.AreEqual(1, threads.Count);
        Assert.AreEqual(1, threads[0].Replies.Count, "Should skip system, deleted, and empty replies");
        Assert.AreEqual("Real human reply", threads[0].Replies[0].Content);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetReviewHistoryAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetReviewHistoryAsync_DeserializesEntries()
    {
        var historyJson = JsonSerializer.Serialize(new[]
        {
            new { ReviewNumber = 1, Recommendation = "Approve", SourceCommit = "aaa" },
            new { ReviewNumber = 2, Recommendation = "Needs Work", SourceCommit = "bbb" },
        });

        var json = $$"""
        {
            "value": {
                "AiCodeReview.ReviewHistory": { "$type": "System.String", "$value": {{JsonSerializer.Serialize(historyJson)}} }
            }
        }
        """;

        var svc = CreateService(json);
        var history = await svc.GetReviewHistoryAsync(Project, Repo, PrId);

        Assert.AreEqual(2, history.Count);
        Assert.AreEqual(1, history[0].ReviewNumber);
        Assert.AreEqual(2, history[1].ReviewNumber);
    }

    [TestMethod]
    public async Task GetReviewHistoryAsync_NoProperty_ReturnsEmpty()
    {
        var json = """{"value":{}}""";

        var svc = CreateService(json);
        var history = await svc.GetReviewHistoryAsync(Project, Repo, PrId);

        Assert.AreEqual(0, history.Count);
    }

    [TestMethod]
    public async Task GetReviewHistoryAsync_InvalidJson_ReturnsEmpty()
    {
        var json = """
        {
            "value": {
                "AiCodeReview.ReviewHistory": { "$type": "System.String", "$value": "not valid json" }
            }
        }
        """;

        var svc = CreateService(json);
        var history = await svc.GetReviewHistoryAsync(Project, Repo, PrId);

        Assert.AreEqual(0, history.Count, "Should gracefully handle invalid JSON");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  AppendReviewHistoryAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task AppendReviewHistoryAsync_ReadModifyWrite()
    {
        bool patchSent = false;
        var handler = new FakeHandler((req) =>
        {
            var url = req.RequestUri!.ToString();
            if (req.Method == HttpMethod.Get && url.Contains("properties"))
            {
                // Return existing history with one entry
                var historyJson = JsonSerializer.Serialize(new[]
                {
                    new { ReviewNumber = 1, Recommendation = "Approve", SourceCommit = "aaa" },
                });
                var escaped = JsonSerializer.Serialize(historyJson); // wraps in quotes + escapes
                var body = $"{{\"value\":{{\"AiCodeReview.ReviewHistory\":{{\"$type\":\"System.String\",\"$value\":{escaped}}}}}}}";
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
            }
            if (req.Method.Method == "PATCH" && url.Contains("properties"))
            {
                patchSent = true;
                var body = req.Content!.ReadAsStringAsync().Result;
                Assert.IsTrue(body.Contains("ReviewHistory"), "PATCH should contain ReviewHistory");
                return new HttpResponseMessage(HttpStatusCode.OK);
            }
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        await svc.AppendReviewHistoryAsync(Project, Repo, PrId, new ReviewHistoryEntry
        {
            ReviewNumber = 2,
            Verdict = "Needs Work",
        });

        Assert.IsTrue(patchSent, "Should send a PATCH request");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  UpdateThreadStatusAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task UpdateThreadStatusAsync_SendsPatch()
    {
        var handler = new FakeHandler((req) =>
        {
            Assert.AreEqual("PATCH", req.Method.Method);
            Assert.IsTrue(req.RequestUri!.ToString().Contains("/threads/99"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        await svc.UpdateThreadStatusAsync(Project, Repo, PrId, 99, "closed");
    }

    [TestMethod]
    public async Task UpdateThreadStatusAsync_FailureLogsWarning()
    {
        var handler = new FakeHandler((_) =>
            new HttpResponseMessage(HttpStatusCode.Forbidden)
            {
                Content = new StringContent("Access denied"),
            });

        var svc = CreateService(handler);
        // Should not throw — just logs a warning
        await svc.UpdateThreadStatusAsync(Project, Repo, PrId, 99, "closed");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ReplyToThreadAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task ReplyToThreadAsync_PostsComment()
    {
        var handler = new FakeHandler((req) =>
        {
            Assert.AreEqual(HttpMethod.Post, req.Method);
            Assert.IsTrue(req.RequestUri!.ToString().Contains("/threads/55/comments"));
            var body = req.Content!.ReadAsStringAsync().Result;
            Assert.IsTrue(body.Contains("Resolved"));
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var svc = CreateService(handler);
        await svc.ReplyToThreadAsync(Project, Repo, PrId, 55, "Resolved");
    }

    [TestMethod]
    public async Task ReplyToThreadAsync_Failure_Throws()
    {
        var handler = new FakeHandler((_) =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("Server error"),
            });

        var svc = CreateService(handler);
        await Assert.ThrowsExceptionAsync<HttpRequestException>(() =>
            svc.ReplyToThreadAsync(Project, Repo, PrId, 55, "text"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetIterationCountAsync
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task GetIterationCountAsync_ReturnsArrayLength()
    {
        var json = """{"value":[{"id":1},{"id":2},{"id":3}]}""";

        var svc = CreateService(json);
        var count = await svc.GetIterationCountAsync(Project, Repo, PrId);
        Assert.AreEqual(3, count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static AzureDevOpsService CreateService(string jsonResponse, AzureDevOpsSettings? settings = null)
    {
        var handler = new FakeHandler((_) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(jsonResponse, Encoding.UTF8, "application/json"),
        });
        return CreateService(handler, settings);
    }

    private static AzureDevOpsService CreateService(FakeHandler handler, AzureDevOpsSettings? settings = null)
    {
        settings ??= new AzureDevOpsSettings
        {
            Organization = Org,
            PersonalAccessToken = "fake-pat",
            ReviewTagName = "ai-reviewed",
        };

        var client = new HttpClient(handler);
        return new AzureDevOpsService(
            client,
            Options.Create(settings),
            new NullTelemetryService(),
            LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Debug)).CreateLogger<AzureDevOpsService>());
    }

    /// <summary>Mock HTTP handler that returns canned responses.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_handler(request));
    }
}
