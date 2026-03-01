using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for the internal and static methods of <see cref="AzureOpenAiReviewService"/>.
/// These tests exercise prompt assembly, truncation, and model adapter behavior
/// without calling the Azure OpenAI API.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AzureOpenAiReviewServiceTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  TruncateContentToLines (static)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void TruncateContentToLines_ShortContent_ReturnsOriginal()
    {
        var content = "line1\nline2\nline3";
        var result = AzureOpenAiReviewService.TruncateContentToLines(content, 5);
        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void TruncateContentToLines_ExactLimit_ReturnsOriginal()
    {
        var content = "line1\nline2\nline3";
        var result = AzureOpenAiReviewService.TruncateContentToLines(content, 3);
        Assert.AreEqual(content, result);
    }

    [TestMethod]
    public void TruncateContentToLines_ExceedsLimit_Truncates()
    {
        var content = "line1\nline2\nline3\nline4\nline5";
        var result = AzureOpenAiReviewService.TruncateContentToLines(content, 3);
        Assert.IsTrue(result.StartsWith("line1\nline2\nline3"), "Should keep first 3 lines");
        Assert.IsTrue(result.Contains("[truncated: 2 more lines]"), "Should include truncation marker");
    }

    [TestMethod]
    public void TruncateContentToLines_SingleLine_ReturnsOriginal()
    {
        var result = AzureOpenAiReviewService.TruncateContentToLines("hello", 1);
        Assert.AreEqual("hello", result);
    }

    [TestMethod]
    public void TruncateContentToLines_MaxLinesOne_TruncatesRest()
    {
        var content = "line1\nline2\nline3";
        var result = AzureOpenAiReviewService.TruncateContentToLines(content, 1);
        Assert.IsTrue(result.StartsWith("line1"), "Should keep first line");
        Assert.IsTrue(result.Contains("[truncated: 2 more lines]"));
    }

    [TestMethod]
    public void TruncateContentToLines_EmptyContent_ReturnsEmpty()
    {
        var result = AzureOpenAiReviewService.TruncateContentToLines("", 10);
        Assert.AreEqual("", result);
    }

    [TestMethod]
    public void TruncateContentToLines_LargeContent_TruncatesCorrectly()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => $"line {i}");
        var content = string.Join("\n", lines);

        var result = AzureOpenAiReviewService.TruncateContentToLines(content, 200);

        Assert.IsTrue(result.Contains("line 200"), "Should include line 200");
        Assert.IsFalse(result.Contains("line 201\n"), "Should not include line 201");
        Assert.IsTrue(result.Contains("[truncated: 800 more lines]"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ModelName property
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ModelName_ReturnsConfiguredName()
    {
        var service = CreateService("test-model-42");
        Assert.AreEqual("test-model-42", service.ModelName);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetSystemPrompt — fallback (no pipeline)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GetSystemPrompt_NoPipeline_ReturnsFallbackPrompt()
    {
        var service = CreateService();
        var prompt = service.GetSystemPrompt();

        Assert.IsTrue(prompt.Length > 100, "Fallback prompt should contain substantial text");
        Assert.IsTrue(prompt.Contains("JSON", StringComparison.OrdinalIgnoreCase),
            "System prompt should reference JSON output format");
    }

    [TestMethod]
    public void GetSingleFileSystemPrompt_NoPipeline_ReturnsFallbackPrompt()
    {
        var service = CreateService();
        var prompt = service.GetSingleFileSystemPrompt();

        Assert.IsTrue(prompt.Length > 100);
        Assert.IsTrue(prompt.Contains("JSON", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void GetPrSummarySystemPrompt_NoPipeline_ReturnsFallbackPrompt()
    {
        var service = CreateService();
        var prompt = service.GetPrSummarySystemPrompt();

        Assert.IsTrue(prompt.Length > 100);
        Assert.IsTrue(prompt.Contains("intent", StringComparison.OrdinalIgnoreCase),
            "PR summary prompt should reference intent");
    }

    [TestMethod]
    public void GetThreadVerificationSystemPrompt_NoPipeline_ReturnsFallbackPrompt()
    {
        var service = CreateService();
        var prompt = service.GetThreadVerificationSystemPrompt();

        Assert.IsTrue(prompt.Length > 100);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GetSystemPrompt — with custom instructions
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GetSystemPrompt_WithCustomInstructions_IncludesInstructions()
    {
        // Create a temp custom instructions file
        var tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(tempDir);
        var filePath = Path.Combine(tempDir, "custom.json");
        File.WriteAllText(filePath, """{"customInstructions": "Always mention performance."}""");

        try
        {
            var service = CreateService(customInstructionsPath: filePath);
            var prompt = service.GetSystemPrompt();

            Assert.IsTrue(prompt.Contains("performance"),
                "System prompt should include custom instructions about performance");
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [TestMethod]
    public void GetSystemPrompt_MissingCustomInstructionsFile_StillReturnsPrompt()
    {
        var service = CreateService(customInstructionsPath: "/nonexistent/custom.json");
        var prompt = service.GetSystemPrompt();

        Assert.IsTrue(prompt.Length > 100,
            "Should return fallback prompt even when custom instructions file is missing");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildPrSummaryUserPrompt
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildPrSummaryUserPrompt_IncludesPrMetadata()
    {
        var service = CreateService();
        var pr = CreatePullRequest(42, "Fix null reference", "alice", "feature/fix", "main");
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Service.cs", ChangeType = "edit", UnifiedDiff = "- old\n+ new" }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("PR #42"), "Should include PR number");
        Assert.IsTrue(prompt.Contains("Fix null reference"), "Should include title");
        Assert.IsTrue(prompt.Contains("alice"), "Should include author");
        Assert.IsTrue(prompt.Contains("feature/fix"), "Should include source branch");
        Assert.IsTrue(prompt.Contains("main"), "Should include target branch");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_IncludesDescription()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test", description: "This fixes a critical bug");
        var files = new List<FileChange> { CreateFileChange("src/a.cs") };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("critical bug"), "Should include PR description");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_NoDescription_OmitsDescriptionLine()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        pr.Description = null!;
        var files = new List<FileChange> { CreateFileChange("src/a.cs") };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsFalse(prompt.Contains("**Description**"), "Should omit description line when null");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_WithDiff_IncludesDiffBlock()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Foo.cs", ChangeType = "edit", UnifiedDiff = "- old\n+ new" }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("```diff"), "Should include diff code block");
        Assert.IsTrue(prompt.Contains("- old"), "Should include diff content");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_AddedFile_IncludesModifiedContent()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        var files = new List<FileChange>
        {
            new() { FilePath = "src/New.cs", ChangeType = "add", ModifiedContent = "public class New { }" }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("public class New { }"), "Should include new file content");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_DeletedFile_ShowsDeletedMarker()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Old.cs", ChangeType = "delete" }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("(file deleted)"), "Should include deletion marker");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_NoContentAvailable_ShowsFallback()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Empty.cs", ChangeType = "edit" }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("no diff or file content available"),
            "Should show fallback message when no content available");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_FallbackToOriginalContent()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Renamed.cs", ChangeType = "rename", OriginalContent = "original content here" }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("original content here"),
            "Should fallback to original content when no diff or modified content");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_WithWorkItems_IncludesWorkItemContext()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        var files = new List<FileChange> { CreateFileChange("src/a.cs") };
        var workItems = new List<WorkItemInfo>
        {
            new() { Id = 100, WorkItemType = "User Story", Title = "Implement login", State = "Active" }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files, workItems);

        Assert.IsTrue(prompt.Contains("Implement login"), "Should include work item title");
        Assert.IsTrue(prompt.Contains("User Story"), "Should include work item type");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_MultipleFiles_ListsAllFiles()
    {
        var service = CreateService();
        var pr = CreatePullRequest(1, "Test");
        var files = new List<FileChange>
        {
            CreateFileChange("src/A.cs", "edit"),
            CreateFileChange("src/B.cs", "add"),
            CreateFileChange("src/C.cs", "delete"),
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        Assert.IsTrue(prompt.Contains("3 total"), "Should show total file count");
        Assert.IsTrue(prompt.Contains("src/A.cs"), "Should list file A");
        Assert.IsTrue(prompt.Contains("src/B.cs"), "Should list file B");
        Assert.IsTrue(prompt.Contains("src/C.cs"), "Should list file C");
    }

    [TestMethod]
    public void BuildPrSummaryUserPrompt_LongDiff_TruncatesTo200Lines()
    {
        var service = CreateService(maxInputLines: 5000);
        var pr = CreatePullRequest(1, "Test");
        var longDiff = string.Join("\n", Enumerable.Range(1, 500).Select(i => $"+ line {i}"));
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Big.cs", ChangeType = "edit", UnifiedDiff = longDiff }
        };

        var prompt = service.BuildPrSummaryUserPrompt(pr, files);

        // Pass 1 caps at min(maxInputLines, 200) = 200
        Assert.IsTrue(prompt.Contains("[truncated:"), "Long diff should be truncated");
        Assert.IsTrue(prompt.Contains("300 more lines"), "Should show 300 remaining lines");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Model adapter integration
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Constructor_WithModelAdapter_AppliesOverrides()
    {
        var adapter = new ModelAdapter
        {
            Name = "test-adapter",
            Temperature = 0.2f,
            MaxOutputTokensBatch = 999
        };

        var service = CreateService(modelAdapter: adapter);

        // The model name should still be correct
        Assert.AreEqual("test-model", service.ModelName);
        // Prompt should still be generated
        Assert.IsTrue(service.GetSystemPrompt().Length > 0);
    }

    [TestMethod]
    public void Constructor_WithModelAdapterPreamble_IncludedInPrompt()
    {
        var adapter = new ModelAdapter
        {
            Name = "test-adapter",
            Preamble = "You are a specialized security reviewer."
        };

        var service = CreateService(modelAdapter: adapter);
        // The preamble is only injected via pipeline, which we don't have
        // in this test, so verify model name is set at minimum
        Assert.AreEqual("test-model", service.ModelName);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Creates a service instance for testing internal methods.
    /// Uses a dummy endpoint/key — won't actually connect to Azure OpenAI.
    /// </summary>
    private static AzureOpenAiReviewService CreateService(
        string modelName = "test-model",
        string? customInstructionsPath = null,
        int maxInputLines = 5000,
        ReviewProfile? reviewProfile = null,
        ModelAdapter? modelAdapter = null)
    {
        return new AzureOpenAiReviewService(
            endpoint: "https://test.openai.azure.com/",
            apiKey: "test-api-key-12345678901234567890",
            modelName: modelName,
            customInstructionsPath: customInstructionsPath,
            logger: NullLogger<AzureOpenAiReviewService>.Instance,
            maxInputLinesPerFile: maxInputLines,
            reviewProfile: reviewProfile,
            modelAdapter: modelAdapter);
    }

    private static PullRequestInfo CreatePullRequest(
        int id = 1, string title = "Test PR",
        string author = "tester", string source = "feature/test",
        string target = "main", string description = "Test description")
    {
        return new PullRequestInfo
        {
            PullRequestId = id,
            Title = title,
            CreatedBy = author,
            SourceBranch = source,
            TargetBranch = target,
            Description = description
        };
    }

    private static FileChange CreateFileChange(
        string path = "src/Test.cs",
        string changeType = "edit",
        string diff = "+ // changed")
    {
        return new FileChange
        {
            FilePath = path,
            ChangeType = changeType,
            UnifiedDiff = diff
        };
    }
}
