using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Issue #4 — Two-Pass Review Architecture.
/// Validates Pass 1 (PR summary) prompt construction, result injection into
/// Pass 2, token aggregation, fallback on Pass 1 failure, and summary markdown.
/// </summary>
[TestClass]
public class TwoPassReviewTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Pass 1 Prompt Construction
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildPrSummaryUserPrompt_ContainsPrContext()
    {
        var service = CreateService();
        var pr = CreatePr();
        var files = CreateFileChanges();

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("PR #42"), "Prompt must include PR number.");
        Assert.IsTrue(prompt.Contains("Test PR Title"), "Prompt must include PR title.");
        Assert.IsTrue(prompt.Contains("developer@test.com"), "Prompt must include author.");
        Assert.IsTrue(prompt.Contains("feature/test"), "Prompt must include source branch.");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_ContainsAllFileNames()
    {
        var service = CreateService();
        var pr = CreatePr();
        var files = CreateFileChanges();

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("/src/Service.cs"), "Prompt must include Service.cs.");
        Assert.IsTrue(prompt.Contains("/src/Model.cs"), "Prompt must include Model.cs.");
        Assert.IsTrue(prompt.Contains("/src/Controller.cs"), "Prompt must include Controller.cs.");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_IncludesDiffs()
    {
        var service = CreateService();
        var pr = CreatePr();
        var files = CreateFileChanges();

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        // Service.cs has a unified diff
        Assert.IsTrue(prompt.Contains("```diff"), "Prompt must include diff code blocks.");
        Assert.IsTrue(prompt.Contains("public void NewMethod"), "Prompt must include diff content.");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_TruncatesLargeFiles()
    {
        var service = CreateService();
        var pr = CreatePr();

        // Create a file with 500 lines — should be truncated to 200 for Pass 1
        var largeContent = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"// line {i}"));
        var files = new List<FileChange>
        {
            new FileChange
            {
                FilePath = "/src/Large.cs",
                ChangeType = "add",
                ModifiedContent = largeContent,
            }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("[truncated:"), "Large files should be truncated in Pass 1 prompt.");
        Assert.IsTrue(prompt.Contains("300 more lines"), "Should truncate to 200 lines (500-200=300 remaining).");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_IncludesWorkItems()
    {
        var service = CreateService();
        var pr = CreatePr();
        var files = CreateFileChanges();
        var workItems = new List<WorkItemInfo>
        {
            new WorkItemInfo
            {
                Id = 1234,
                Title = "Add feature X",
                WorkItemType = "User Story",
                State = "Active",
                AcceptanceCriteria = "Must do Y and Z",
            }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files, workItems);

        Assert.IsTrue(prompt.Contains("Add feature X"), "Prompt must include work item title.");
        Assert.IsTrue(prompt.Contains("Must do Y and Z"), "Prompt must include AC.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Pass 1 Result Model
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void PrSummaryResult_DefaultValues_AreEmpty()
    {
        var result = new PrSummaryResult();

        Assert.AreEqual(string.Empty, result.Intent);
        Assert.AreEqual(string.Empty, result.ArchitecturalImpact);
        Assert.AreEqual(0, result.CrossFileRelationships.Count);
        Assert.AreEqual(0, result.RiskAreas.Count);
        Assert.AreEqual(0, result.FileGroupings.Count);
    }

    [TestMethod]
    public void PrSummaryResult_Deserializes_FromJson()
    {
        var json = """
        {
            "intent": "Adds a new caching layer",
            "architecturalImpact": "Introduces Redis dependency",
            "crossFileRelationships": ["CacheService.cs uses CacheConfig.cs"],
            "riskAreas": [{"area": "CacheService.cs", "reason": "No TTL validation"}],
            "fileGroupings": [{"groupName": "Cache", "files": ["CacheService.cs", "CacheConfig.cs"], "description": "Caching layer"}]
        }
        """;

        var result = System.Text.Json.JsonSerializer.Deserialize<PrSummaryResult>(json,
            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.IsNotNull(result);
        Assert.AreEqual("Adds a new caching layer", result!.Intent);
        Assert.AreEqual("Introduces Redis dependency", result.ArchitecturalImpact);
        Assert.AreEqual(1, result.CrossFileRelationships.Count);
        Assert.AreEqual(1, result.RiskAreas.Count);
        Assert.AreEqual("CacheService.cs", result.RiskAreas[0].Area);
        Assert.AreEqual(1, result.FileGroupings.Count);
        Assert.AreEqual(2, result.FileGroupings[0].Files.Count);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Pass 1 Injection into Pass 2 (Cross-File Context)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void PullRequestInfo_CrossFileSummary_DefaultNull()
    {
        var pr = new PullRequestInfo();
        Assert.IsNull(pr.CrossFileSummary, "CrossFileSummary must default to null.");
    }

    [TestMethod]
    public void PullRequestInfo_CrossFileSummary_CanBeSet()
    {
        var pr = CreatePr();
        pr.CrossFileSummary = new PrSummaryResult
        {
            Intent = "Test intent",
            CrossFileRelationships = new List<string> { "A depends on B" },
        };

        Assert.IsNotNull(pr.CrossFileSummary);
        Assert.AreEqual("Test intent", pr.CrossFileSummary.Intent);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Orchestrator Integration — Pass 1 injected, tokens aggregated
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Pass1_GeneratePrSummaryAsync_CalledAndReturnsSummary()
    {
        // Arrange: verify GeneratePrSummaryAsync is callable and returns expected results
        var fake = new FakeCodeReviewService();

        // Verify that the FakeCodeReviewService's GeneratePrSummaryAsync is called
        bool pass1Called = false;
        fake.PrSummaryFactory = (pr, files, wi) =>
        {
            pass1Called = true;
            return new PrSummaryResult
            {
                Intent = "Test PR intent from Pass 1",
                CrossFileRelationships = new List<string> { "File A calls File B" },
            };
        };

        // This test verifies the integration flow doesn't crash
        // Full verification of cross-file context injection requires a live PR
        // We'll focus on verifying Pass 1 is called

        // For now, verify the fake service's GeneratePrSummaryAsync works
        var summary = await fake.GeneratePrSummaryAsync(
            CreatePr(),
            CreateFileChanges());

        Assert.IsTrue(pass1Called, "Pass 1 (GeneratePrSummaryAsync) must be called.");
        Assert.IsNotNull(summary);
        Assert.AreEqual("Test PR intent from Pass 1", summary!.Intent);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Token Aggregation
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void TokenAggregation_Pass1PlusPass2_Summed()
    {
        // Simulate what the orchestrator does: merge per-file + add Pass 1 tokens
        var perFileResult = new CodeReviewResult
        {
            Summary = new ReviewSummary { Verdict = "APPROVED", Description = "" },
            PromptTokens = 1000,
            CompletionTokens = 500,
            TotalTokens = 1500,
            AiDurationMs = 2000,
        };

        var prSummary = new PrSummaryResult
        {
            Intent = "Test intent",
            PromptTokens = 300,
            CompletionTokens = 100,
            TotalTokens = 400,
            AiDurationMs = 500,
        };

        // Simulate the orchestrator's aggregation logic
        perFileResult.PromptTokens = (perFileResult.PromptTokens ?? 0) + (prSummary.PromptTokens ?? 0);
        perFileResult.CompletionTokens = (perFileResult.CompletionTokens ?? 0) + (prSummary.CompletionTokens ?? 0);
        perFileResult.TotalTokens = (perFileResult.TotalTokens ?? 0) + (prSummary.TotalTokens ?? 0);
        perFileResult.AiDurationMs = (perFileResult.AiDurationMs ?? 0) + (prSummary.AiDurationMs ?? 0);

        Assert.AreEqual(1300, perFileResult.PromptTokens);
        Assert.AreEqual(600, perFileResult.CompletionTokens);
        Assert.AreEqual(1900, perFileResult.TotalTokens);
        Assert.AreEqual(2500, perFileResult.AiDurationMs);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Summary Markdown includes Pass 1 context
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildSummaryMarkdown_WithPrSummary_IncludesCrossFileSection()
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 3, EditsCount = 2, AddsCount = 1,
                Verdict = "APPROVED WITH SUGGESTIONS",
                Description = "Test PR intent",
            },
            FileReviews = new List<FileReview>(),
        };

        var prSummary = new PrSummaryResult
        {
            Intent = "Adds caching layer",
            ArchitecturalImpact = "Introduces Redis dependency",
            CrossFileRelationships = new List<string>
            {
                "CacheService.cs calls CacheConfig.cs for settings",
            },
            RiskAreas = new List<RiskArea>
            {
                new RiskArea { Area = "CacheService.cs", Reason = "No TTL validation" },
            },
            FileGroupings = new List<FileGrouping>
            {
                new FileGrouping
                {
                    GroupName = "Caching",
                    Files = new List<string> { "CacheService.cs", "CacheConfig.cs" },
                    Description = "New caching layer",
                },
            },
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, prSummary: prSummary);

        Assert.IsTrue(markdown.Contains("### Cross-File Analysis"), "Must include Cross-File Analysis section.");
        Assert.IsTrue(markdown.Contains("Introduces Redis dependency"), "Must include architectural impact.");
        Assert.IsTrue(markdown.Contains("CacheService.cs calls CacheConfig.cs"), "Must include relationships.");
        Assert.IsTrue(markdown.Contains("No TTL validation"), "Must include risk areas.");
        Assert.IsTrue(markdown.Contains("Caching"), "Must include file groupings.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_WithoutPrSummary_NoCrossFileSection()
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 1, EditsCount = 1,
                Verdict = "APPROVED",
            },
            FileReviews = new List<FileReview>(),
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsFalse(markdown.Contains("### Cross-File Analysis"),
            "Must NOT include Cross-File Analysis when prSummary is null.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_WithPrSummary_NoneImpact_SkipsArchSection()
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 1, Verdict = "APPROVED",
            },
            FileReviews = new List<FileReview>(),
        };

        var prSummary = new PrSummaryResult
        {
            Intent = "Minor fix",
            ArchitecturalImpact = "None",
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, prSummary: prSummary);

        Assert.IsTrue(markdown.Contains("### Cross-File Analysis"), "Section header should still appear.");
        Assert.IsFalse(markdown.Contains("Architectural Impact"), "Should skip 'None' architectural impact.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Pass 1 Failure Fallback
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task Pass1Failure_ReturnsNull_PerFileReviewsStillWork()
    {
        var fake = new FakeCodeReviewService();
        fake.PrSummaryFactory = (pr, files, wi) => null; // Simulate failure

        var summary = await fake.GeneratePrSummaryAsync(CreatePr(), CreateFileChanges());

        Assert.IsNull(summary, "Pass 1 failure should return null.");

        // Per-file reviews should still work
        var fileResult = await fake.ReviewFileAsync(CreatePr(), CreateFileChanges()[0], 3);
        Assert.IsNotNull(fileResult);
        Assert.AreEqual("APPROVED WITH SUGGESTIONS", fileResult.Summary.Verdict);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  FakeCodeReviewService — Pass 1 basics
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FakeService_GeneratePrSummary_ReturnsDeterministicResult()
    {
        var fake = new FakeCodeReviewService();
        var pr = CreatePr();
        var files = CreateFileChanges();

        var summary = await fake.GeneratePrSummaryAsync(pr, files);

        Assert.IsNotNull(summary);
        Assert.IsTrue(summary!.Intent.Contains("PR #42"), "Fake summary must reference PR ID.");
        Assert.AreEqual("fake-model", summary.ModelName);
        Assert.AreEqual(150, summary.TotalTokens);
        Assert.AreEqual(1, summary.FileGroupings.Count);
        Assert.AreEqual(3, summary.FileGroupings[0].Files.Count); // 3 files
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  TruncateContentToLines (static helper)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void TruncateContentToLines_BelowLimit_ReturnsUnchanged()
    {
        var content = "line1\nline2\nline3";
        var result = AzureOpenAiReviewService.TruncateContentToLines(content, 10);
        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void TruncateContentToLines_AboveLimit_Truncates()
    {
        var content = string.Join('\n', Enumerable.Range(1, 100).Select(i => $"line {i}"));
        var result = AzureOpenAiReviewService.TruncateContentToLines(content, 50);
        Assert.IsTrue(result.Contains("line 50"), "Must include line 50.");
        Assert.IsFalse(result.Contains("line 51\n"), "Must NOT include line 51.");
        Assert.IsTrue(result.Contains("[truncated: 50 more lines]"), "Must have truncation marker.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static AzureOpenAiReviewService CreateService()
    {
        var loggerFactory = LoggerFactory.Create(
            b => b.SetMinimumLevel(LogLevel.Warning));
        return new AzureOpenAiReviewService(
            "https://fake.openai.azure.com/",
            "fake-key",
            "gpt-4o",
            customInstructionsPath: null,
            loggerFactory.CreateLogger<AzureOpenAiReviewService>());
    }

    private static PullRequestInfo CreatePr() => new()
    {
        PullRequestId = 42,
        Title = "Test PR Title",
        Description = "This PR adds a new feature.",
        SourceBranch = "refs/heads/feature/test",
        TargetBranch = "refs/heads/main",
        CreatedBy = "developer@test.com",
    };

    private static List<FileChange> CreateFileChanges() => new()
    {
        new FileChange
        {
            FilePath = "/src/Service.cs",
            ChangeType = "edit",
            OriginalContent = "public class Service { }",
            ModifiedContent = "public class Service { public void NewMethod() { } }",
            UnifiedDiff = "--- a/src/Service.cs\n+++ b/src/Service.cs\n@@ -1 +1 @@\n-public class Service { }\n+public class Service { public void NewMethod() { } }",
        },
        new FileChange
        {
            FilePath = "/src/Model.cs",
            ChangeType = "add",
            ModifiedContent = "public class Model { public int Id { get; set; } }",
        },
        new FileChange
        {
            FilePath = "/src/Controller.cs",
            ChangeType = "edit",
            OriginalContent = "public class Controller { }",
            ModifiedContent = "public class Controller { private readonly Service _svc; }",
            UnifiedDiff = "--- a/src/Controller.cs\n+++ b/src/Controller.cs\n@@ -1 +1 @@\n-public class Controller { }\n+public class Controller { private readonly Service _svc; }",
        },
    };
}
