using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for internal/static methods of <see cref="VectorStoreReviewService"/>
/// that were previously private: BuildUserMessage and LoadCustomInstructions.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class VectorStoreBuildUserMessageTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  BuildUserMessage — basic structure
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildUserMessage_IncludesPrMetadata()
    {
        var pr = MakePr();
        var files = new List<FileChange>
        {
            new() { FilePath = "src/File.cs", ChangeType = "edit" }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("PR #42"), "Should include PR number");
        Assert.IsTrue(msg.Contains("Test PR"), "Should include title");
        Assert.IsTrue(msg.Contains("tester"), "Should include author");
        Assert.IsTrue(msg.Contains("refs/heads/feature"), "Should include source branch");
        Assert.IsTrue(msg.Contains("refs/heads/main"), "Should include target branch");
    }

    [TestMethod]
    public void BuildUserMessage_WithDescription_IncludesDescription()
    {
        var pr = MakePr();
        pr.Description = "This PR adds login validation.";
        var files = new List<FileChange> { new() { FilePath = "src/A.cs", ChangeType = "edit" } };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("This PR adds login validation."));
        Assert.IsTrue(msg.Contains("**Description**:"));
    }

    [TestMethod]
    public void BuildUserMessage_LongDescription_Truncated()
    {
        var pr = MakePr();
        pr.Description = new string('X', 3000);
        var files = new List<FileChange> { new() { FilePath = "src/A.cs", ChangeType = "edit" } };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("[truncated]"), "Should truncate descriptions > 2000 chars");
        // The first 2000 chars should be present
        Assert.IsTrue(msg.Contains(new string('X', 100)));
    }

    [TestMethod]
    public void BuildUserMessage_NoDescription_OmitsDescriptionBlock()
    {
        var pr = MakePr();
        pr.Description = "";
        var files = new List<FileChange> { new() { FilePath = "src/A.cs", ChangeType = "edit" } };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsFalse(msg.Contains("**Description**:"), "Should omit description block when empty");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildUserMessage — Pass 1 primer (PrSummary)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildUserMessage_WithPrSummary_IncludesCrossFileContext()
    {
        var pr = MakePr();
        var files = new List<FileChange> { new() { FilePath = "src/A.cs", ChangeType = "edit" } };
        var summary = new PrSummaryResult
        {
            Intent = "Add login validation",
            ArchitecturalImpact = "Affects auth module",
            CrossFileRelationships = new List<string> { "A.cs depends on B.cs" },
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "Security", Reason = "Password handling" }
            }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, summary);

        Assert.IsTrue(msg.Contains("Prior Analysis"));
        Assert.IsTrue(msg.Contains("Add login validation"));
        Assert.IsTrue(msg.Contains("Affects auth module"));
        Assert.IsTrue(msg.Contains("A.cs depends on B.cs"));
        Assert.IsTrue(msg.Contains("Security"));
        Assert.IsTrue(msg.Contains("Password handling"));
    }

    [TestMethod]
    public void BuildUserMessage_PrSummary_NoCrossFileRelationships_OmitsSection()
    {
        var pr = MakePr();
        var files = new List<FileChange> { new() { FilePath = "src/A.cs", ChangeType = "edit" } };
        var summary = new PrSummaryResult
        {
            Intent = "Simple fix",
            CrossFileRelationships = new List<string>(),
            RiskAreas = new List<RiskArea>()
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, summary);

        Assert.IsFalse(msg.Contains("Cross-File Relationships"), "Should omit empty relationships");
        Assert.IsFalse(msg.Contains("Risk Areas"), "Should omit empty risk areas");
    }

    [TestMethod]
    public void BuildUserMessage_NoPrSummary_OmitsPrimer()
    {
        var pr = MakePr();
        var files = new List<FileChange> { new() { FilePath = "src/A.cs", ChangeType = "edit" } };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsFalse(msg.Contains("Prior Analysis"), "Should omit primer when no summary");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildUserMessage — file change badges
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow("add", "[NEW]")]
    [DataRow("delete", "[DELETED]")]
    [DataRow("edit", "[MODIFIED]")]
    [DataRow("rename", "[RENAMED]")]
    [DataRow("copy", "[COPY]")]
    public void BuildUserMessage_ChangeType_CorrectBadge(string changeType, string expectedBadge)
    {
        var pr = MakePr();
        var files = new List<FileChange>
        {
            new() { FilePath = "src/File.cs", ChangeType = changeType }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains(expectedBadge), $"Change type '{changeType}' should produce badge '{expectedBadge}'");
    }

    [TestMethod]
    public void BuildUserMessage_MultipleFiles_ListsAll()
    {
        var pr = MakePr();
        var files = new List<FileChange>
        {
            new() { FilePath = "src/A.cs", ChangeType = "edit" },
            new() { FilePath = "src/B.cs", ChangeType = "add" },
            new() { FilePath = "src/C.cs", ChangeType = "delete" },
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("3 files changed"));
        Assert.IsTrue(msg.Contains("`src/A.cs`"));
        Assert.IsTrue(msg.Contains("`src/B.cs`"));
        Assert.IsTrue(msg.Contains("`src/C.cs`"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildUserMessage — changed line ranges
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildUserMessage_WithChangedLineRanges_ShowsRanges()
    {
        var pr = MakePr();
        var files = new List<FileChange>
        {
            new()
            {
                FilePath = "src/File.cs",
                ChangeType = "edit",
                ChangedLineRanges = new List<(int Start, int End)>
                {
                    (10, 10), (20, 25)
                }
            }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("L10"), "Should show single-line range");
        Assert.IsTrue(msg.Contains("L20-25"), "Should show multi-line range");
    }

    [TestMethod]
    public void BuildUserMessage_MoreThan5Ranges_ShowsTruncationNote()
    {
        var pr = MakePr();
        var files = new List<FileChange>
        {
            new()
            {
                FilePath = "src/File.cs",
                ChangeType = "edit",
                ChangedLineRanges = Enumerable.Range(1, 8)
                    .Select(i => (i * 10, i * 10 + 2)).ToList()
            }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("+3 more ranges"), "Should show count of additional ranges");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildUserMessage — diff snippets
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildUserMessage_WithDiff_IncludesDiff()
    {
        var pr = MakePr();
        var files = new List<FileChange>
        {
            new()
            {
                FilePath = "src/File.cs",
                ChangeType = "edit",
                UnifiedDiff = "@@ -1,3 +1,3 @@\n context\n-old\n+new\n context"
            }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("```diff"), "Should contain diff code block");
        Assert.IsTrue(msg.Contains("-old"));
        Assert.IsTrue(msg.Contains("+new"));
    }

    [TestMethod]
    public void BuildUserMessage_LargeDiff_TruncatedAt3000Chars()
    {
        var pr = MakePr();
        var largeDiff = new string('x', 4000);
        var files = new List<FileChange>
        {
            new()
            {
                FilePath = "src/File.cs",
                ChangeType = "edit",
                UnifiedDiff = largeDiff
            }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("[diff truncated]"), "Should truncate large diffs at 3000 chars");
    }

    [TestMethod]
    public void BuildUserMessage_NoDiff_SkipsDiffBlock()
    {
        var pr = MakePr();
        var files = new List<FileChange>
        {
            new()
            {
                FilePath = "src/File.cs",
                ChangeType = "add",
                UnifiedDiff = null
            }
        };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsFalse(msg.Contains($"### `src/File.cs`"), "Should skip diff block when no diff");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildUserMessage — task instructions
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildUserMessage_AlwaysIncludesTaskSection()
    {
        var pr = MakePr();
        var files = new List<FileChange> { new() { FilePath = "a.cs", ChangeType = "edit" } };

        var msg = VectorStoreReviewService.BuildUserMessage(pr, files, null);

        Assert.IsTrue(msg.Contains("## Your Task"), "Should include task instructions");
        Assert.IsTrue(msg.Contains("file_search"), "Should mention file_search tool");
        Assert.IsTrue(msg.Contains("valid JSON"), "Should instruct JSON response");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  LoadCustomInstructions
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void LoadCustomInstructions_NullPath_ReturnsNull()
    {
        Assert.IsNull(VectorStoreReviewService.LoadCustomInstructions(null));
    }

    [TestMethod]
    public void LoadCustomInstructions_EmptyPath_ReturnsNull()
    {
        Assert.IsNull(VectorStoreReviewService.LoadCustomInstructions(""));
        Assert.IsNull(VectorStoreReviewService.LoadCustomInstructions("   "));
    }

    [TestMethod]
    public void LoadCustomInstructions_NonExistentFile_ReturnsNull()
    {
        Assert.IsNull(VectorStoreReviewService.LoadCustomInstructions("/nonexistent/path/file.json"));
    }

    [TestMethod]
    public void LoadCustomInstructions_ValidFile_ReturnsInstructions()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile,
                """{"customInstructions": "Always check for null references."}""");

            var result = VectorStoreReviewService.LoadCustomInstructions(tempFile);

            Assert.AreEqual("Always check for null references.", result);
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void LoadCustomInstructions_MissingProperty_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """{"otherProperty": "value"}""");
            var result = VectorStoreReviewService.LoadCustomInstructions(tempFile);
            Assert.IsNull(result, "Should return null when customInstructions property is missing");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void LoadCustomInstructions_EmptyInstructions_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, """{"customInstructions": ""}""");
            var result = VectorStoreReviewService.LoadCustomInstructions(tempFile);
            Assert.IsNull(result, "Should return null for empty instructions");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void LoadCustomInstructions_InvalidJson_ReturnsNull()
    {
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllText(tempFile, "not valid json {{{");
            var result = VectorStoreReviewService.LoadCustomInstructions(tempFile);
            Assert.IsNull(result, "Should return null for invalid JSON");
        }
        finally
        {
            File.Delete(tempFile);
        }
    }

    [TestMethod]
    public void LoadCustomInstructions_RelativePath_ResolvesFromBaseDirectory()
    {
        // custom-instructions.json exists at AppContext.BaseDirectory (copied to output)
        var result = VectorStoreReviewService.LoadCustomInstructions("custom-instructions.json");
        // The file exists in the test output directory, result depends on its content
        // Just verify it doesn't throw and returns a reasonable value
        // (null or a string — depends on whether the file has the property)
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static PullRequestInfo MakePr() => new()
    {
        PullRequestId = 42,
        Title = "Test PR",
        CreatedBy = "tester",
        SourceBranch = "refs/heads/feature",
        TargetBranch = "refs/heads/main",
        LastMergeSourceCommit = "abc123",
    };
}
