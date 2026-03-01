using AiCodeReview.Models;
using AiCodeReview.Services;
using AiCodeReview.Tests.Helpers;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for Issue #13 — Incremental Review (Delta-Only Re-Review).
/// Validates delta detection, file partitioning, carry-forward logic,
/// BuildSummaryMarkdown delta section rendering, DeltaReviewInfo model,
/// FakeDevOpsService iteration changes, and token savings estimation.
/// </summary>
[TestCategory("Unit")]
[TestClass]
public class DeltaReviewTests
{
    // ═══════════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════════

    private static CodeReviewResult MakeResult(
        string verdict = "APPROVED",
        int inlineCount = 0,
        int? totalTokens = null)
    {
        return new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 3,
                EditsCount = 2,
                AddsCount = 1,
                DeletesCount = 0,
                CommitsCount = 1,
                Description = "Test PR description.",
                Verdict = verdict,
                VerdictJustification = "Test justification.",
            },
            InlineComments = Enumerable.Range(0, inlineCount).Select(i => new InlineComment
            {
                FilePath = "test.cs",
                StartLine = i + 5,
                EndLine = i + 5,
                LeadIn = "Suggestion",
                Comment = $"Comment {i}",
                Status = "closed",
            }).ToList(),
            TotalTokens = totalTokens,
            PromptTokens = totalTokens.HasValue ? (int)(totalTokens.Value * 0.7) : null,
            CompletionTokens = totalTokens.HasValue ? (int)(totalTokens.Value * 0.3) : null,
        };
    }

    private static DeltaReviewInfo MakeDeltaInfo(
        int baseIteration = 1,
        int currentIteration = 2,
        int totalFiles = 10,
        List<string>? changedPaths = null,
        List<string>? carriedForwardPaths = null)
    {
        changedPaths ??= new List<string> { "/src/Changed.cs" };
        carriedForwardPaths ??= new List<string> { "/src/Unchanged.cs", "/src/Other.cs" };

        return new DeltaReviewInfo
        {
            IsDeltaReview = true,
            BaseIteration = baseIteration,
            CurrentIteration = currentIteration,
            TotalFilesInPr = totalFiles,
            DeltaFilesReviewed = changedPaths.Count,
            CarriedForwardFiles = carriedForwardPaths.Count,
            ChangedFilePaths = changedPaths,
            CarriedForwardFilePaths = carriedForwardPaths,
        };
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  1. DeltaReviewInfo Model
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void DeltaReviewInfo_DefaultValues_AllZeroOrEmpty()
    {
        var info = new DeltaReviewInfo();

        Assert.IsFalse(info.IsDeltaReview);
        Assert.AreEqual(0, info.BaseIteration);
        Assert.AreEqual(0, info.CurrentIteration);
        Assert.AreEqual(0, info.TotalFilesInPr);
        Assert.AreEqual(0, info.DeltaFilesReviewed);
        Assert.AreEqual(0, info.CarriedForwardFiles);
        Assert.AreEqual(0, info.ChangedFilePaths.Count);
        Assert.AreEqual(0, info.CarriedForwardFilePaths.Count);
        Assert.AreEqual(0, info.EstimatedTokenSavings);
    }

    [TestMethod]
    public void DeltaReviewInfo_CanPopulateAllProperties()
    {
        var info = MakeDeltaInfo(
            baseIteration: 3,
            currentIteration: 5,
            totalFiles: 15,
            changedPaths: new List<string> { "/src/A.cs", "/src/B.cs" },
            carriedForwardPaths: new List<string> { "/src/C.cs", "/src/D.cs", "/src/E.cs" });

        Assert.IsTrue(info.IsDeltaReview);
        Assert.AreEqual(3, info.BaseIteration);
        Assert.AreEqual(5, info.CurrentIteration);
        Assert.AreEqual(15, info.TotalFilesInPr);
        Assert.AreEqual(2, info.DeltaFilesReviewed);
        Assert.AreEqual(3, info.CarriedForwardFiles);
        Assert.AreEqual(2, info.ChangedFilePaths.Count);
        Assert.AreEqual(3, info.CarriedForwardFilePaths.Count);
    }

    [TestMethod]
    public void DeltaReviewInfo_EstimatedTokenSavings_IsSettable()
    {
        var info = new DeltaReviewInfo { EstimatedTokenSavings = 5000 };
        Assert.AreEqual(5000, info.EstimatedTokenSavings);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  2. BuildSummaryMarkdown — Delta Section Rendering
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildSummaryMarkdown_NoDeltaInfo_NoIncrementalSection()
    {
        var result = MakeResult();

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsFalse(md.Contains("Incremental Review"),
            "Should not contain incremental review section when deltaInfo is null.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_NonDeltaReview_NoIncrementalSection()
    {
        var result = MakeResult();
        var delta = new DeltaReviewInfo { IsDeltaReview = false };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsFalse(md.Contains("Incremental Review"),
            "Should not contain incremental review section when IsDeltaReview is false.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_ContainsIncrementalSection()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(baseIteration: 2, currentIteration: 4);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("### :arrows_counterclockwise: Incremental Review"),
            "Should contain incremental review header.");
        Assert.IsTrue(md.Contains("iteration 2 → 4"),
            "Should show iteration range.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_ShowsFileCounts()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(totalFiles: 10,
            changedPaths: new List<string> { "/src/A.cs", "/src/B.cs" },
            carriedForwardPaths: new List<string> { "/src/C.cs", "/src/D.cs", "/src/E.cs" });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("| Total files in PR | 10 |"), "Should show total files.");
        Assert.IsTrue(md.Contains("| Files reviewed (delta) | 2 |"), "Should show delta files.");
        Assert.IsTrue(md.Contains("| Files carried forward | 3 |"), "Should show carried forward files.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_ShowsTokenSavings()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo();
        delta.EstimatedTokenSavings = 12500;

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("Estimated token savings"),
            "Should show token savings when > 0.");
        Assert.IsTrue(md.Contains("12,500"),
            "Should format token savings with thousands separator.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_ZeroTokenSavings_NoSavingsRow()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo();
        delta.EstimatedTokenSavings = 0;

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsFalse(md.Contains("Estimated token savings"),
            "Should not show token savings row when savings are 0.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_ListsChangedFiles()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(
            changedPaths: new List<string> { "/src/Zebra.cs", "/src/Alpha.cs" });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("Files reviewed in this pass"),
            "Should have expandable section for changed files.");
        Assert.IsTrue(md.Contains("- `/src/Alpha.cs`"),
            "Changed files should be listed alphabetically.");
        Assert.IsTrue(md.Contains("- `/src/Zebra.cs`"));
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_ListsCarriedForwardFiles()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(
            carriedForwardPaths: new List<string> { "/src/Z.cs", "/src/A.cs" });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("Files carried forward from prior review"),
            "Should have expandable section for carried-forward files.");
        Assert.IsTrue(md.Contains("- `/src/A.cs`"),
            "Carried-forward files should be listed alphabetically.");
        Assert.IsTrue(md.Contains("- `/src/Z.cs`"));
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_EmptyCarriedForward_NoCarriedSection()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(
            changedPaths: new List<string> { "/src/A.cs" },
            carriedForwardPaths: new List<string>());

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("Files reviewed in this pass"),
            "Should still have changed files section.");
        Assert.IsFalse(md.Contains("Files carried forward from prior review"),
            "Should not show carried-forward section when list is empty.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_ReReview_BothSectionsPresent()
    {
        var result = MakeResult("APPROVED WITH SUGGESTIONS");
        var delta = MakeDeltaInfo();
        var metadata = new ReviewMetadata
        {
            LastReviewedSourceCommit = "abc1234567890",
            ReviewCount = 1,
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, isReReview: true, reviewNumber: 2,
            priorMetadata: metadata, deltaInfo: delta);

        Assert.IsTrue(md.Contains("Re-Review"),
            "Should show re-review header.");
        Assert.IsTrue(md.Contains("Incremental Review"),
            "Should also show incremental review section.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  3. ReviewResponse — DeltaInfo property
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewResponse_DeltaInfo_DefaultNull()
    {
        var response = new ReviewResponse();
        Assert.IsNull(response.DeltaInfo);
    }

    [TestMethod]
    public void ReviewResponse_DeltaInfo_CanBeSet()
    {
        var delta = MakeDeltaInfo();
        var response = new ReviewResponse { DeltaInfo = delta };

        Assert.IsNotNull(response.DeltaInfo);
        Assert.IsTrue(response.DeltaInfo.IsDeltaReview);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  4. ReviewHistoryEntry — Delta fields
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewHistoryEntry_DeltaFields_DefaultValues()
    {
        var entry = new ReviewHistoryEntry();

        Assert.IsFalse(entry.IsDeltaReview);
        Assert.AreEqual(0, entry.DeltaFilesReviewed);
        Assert.AreEqual(0, entry.CarriedForwardFiles);
    }

    [TestMethod]
    public void ReviewHistoryEntry_DeltaFields_CanBePopulated()
    {
        var entry = new ReviewHistoryEntry
        {
            IsDeltaReview = true,
            DeltaFilesReviewed = 3,
            CarriedForwardFiles = 7,
        };

        Assert.IsTrue(entry.IsDeltaReview);
        Assert.AreEqual(3, entry.DeltaFilesReviewed);
        Assert.AreEqual(7, entry.CarriedForwardFiles);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  5. FakeDevOpsService — GetIterationChangesAsync
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public async Task FakeDevOpsService_GetIterationChanges_DefaultEmpty()
    {
        var fake = new FakeDevOpsService();

        var result = await fake.GetIterationChangesAsync("proj", "repo", 1, 1, 2);

        Assert.IsNotNull(result);
        Assert.AreEqual(0, result.Count, "Should return empty set when not seeded.");
    }

    [TestMethod]
    public async Task FakeDevOpsService_GetIterationChanges_ReturnsSeededPaths()
    {
        var fake = new FakeDevOpsService();
        fake.SeedIterationChanges(new[] { "/src/A.cs", "/src/B.cs" });

        var result = await fake.GetIterationChangesAsync("proj", "repo", 1, 1, 2);

        Assert.AreEqual(2, result.Count);
        Assert.IsTrue(result.Contains("/src/A.cs"));
        Assert.IsTrue(result.Contains("/src/B.cs"));
    }

    [TestMethod]
    public async Task FakeDevOpsService_GetIterationChanges_CaseInsensitive()
    {
        var fake = new FakeDevOpsService();
        fake.SeedIterationChanges(new[] { "/src/MyFile.cs" });

        var result = await fake.GetIterationChangesAsync("proj", "repo", 1, 1, 2);

        Assert.IsTrue(result.Contains("/src/myfile.cs"),
            "Should match case-insensitively.");
        Assert.IsTrue(result.Contains("/SRC/MYFILE.CS"),
            "Should match case-insensitively.");
    }

    [TestMethod]
    public async Task FakeDevOpsService_SeedIterationChanges_OverwritesPrevious()
    {
        var fake = new FakeDevOpsService();
        fake.SeedIterationChanges(new[] { "/src/Old.cs" });
        fake.SeedIterationChanges(new[] { "/src/New.cs" });

        var result = await fake.GetIterationChangesAsync("proj", "repo", 1, 1, 2);

        Assert.AreEqual(1, result.Count);
        Assert.IsTrue(result.Contains("/src/New.cs"));
        Assert.IsFalse(result.Contains("/src/Old.cs"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  6. Token Savings Estimation
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void TokenSavingsEstimation_ProportionalToCarriedForward()
    {
        // If 2 files reviewed out of 5 total, and used 1000 tokens,
        // full review would have used ≈ 1000 * (5/2) = 2500 tokens
        // savings = 2500 - 1000 = 1500 tokens

        var delta = MakeDeltaInfo(
            changedPaths: new List<string> { "/src/A.cs", "/src/B.cs" },
            carriedForwardPaths: new List<string> { "/src/C.cs", "/src/D.cs", "/src/E.cs" });

        // Simulate the calculation from HandleReviewAsync
        int actualTokens = 1000;
        int totalReviewable = delta.DeltaFilesReviewed + delta.CarriedForwardFiles;
        double ratio = (double)delta.CarriedForwardFiles / totalReviewable;
        int estimatedSavings = (int)(actualTokens / (1.0 - ratio) * ratio);

        delta.EstimatedTokenSavings = estimatedSavings;

        // 3 carried / 5 total = 60% savings → 1000 / 0.4 * 0.6 = 1500
        Assert.AreEqual(1500, delta.EstimatedTokenSavings,
            "Savings should be proportional to carried-forward ratio.");
    }

    [TestMethod]
    public void TokenSavingsEstimation_AllFilesChanged_ZeroSavings()
    {
        // If all files changed (delta = total), ratio = 0, savings = 0
        var delta = MakeDeltaInfo(
            changedPaths: new List<string> { "/src/A.cs", "/src/B.cs" },
            carriedForwardPaths: new List<string>());

        int actualTokens = 1000;
        int totalReviewable = delta.DeltaFilesReviewed + delta.CarriedForwardFiles;
        if (totalReviewable > 0 && delta.CarriedForwardFiles > 0)
        {
            double ratio = (double)delta.CarriedForwardFiles / totalReviewable;
            delta.EstimatedTokenSavings = (int)(actualTokens / (1.0 - ratio) * ratio);
        }

        Assert.AreEqual(0, delta.EstimatedTokenSavings,
            "No savings when all files were reviewed.");
    }

    [TestMethod]
    public void TokenSavingsEstimation_HeavyCarryForward_LargeSavings()
    {
        // 1 file reviewed, 9 carried forward → 90% savings
        var delta = MakeDeltaInfo(
            changedPaths: new List<string> { "/src/A.cs" },
            carriedForwardPaths: Enumerable.Range(0, 9)
                .Select(i => $"/src/File{i}.cs").ToList());

        int actualTokens = 500;
        int totalReviewable = delta.DeltaFilesReviewed + delta.CarriedForwardFiles;
        double ratio = (double)delta.CarriedForwardFiles / totalReviewable;
        delta.EstimatedTokenSavings = (int)(actualTokens / (1.0 - ratio) * ratio);

        // 9/10 = 0.9 → 500 / 0.1 * 0.9 = 4500
        Assert.AreEqual(4500, delta.EstimatedTokenSavings,
            "Heavy carry-forward should yield large token savings.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  7. Delta Detection Logic (file partitioning)
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void DeltaPartitioning_FilesAreCorrectlySplit()
    {
        // Simulate the partitioning logic from HandleReviewAsync
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit" },
            new() { FilePath = "/src/B.cs", ChangeType = "edit" },
            new() { FilePath = "/src/C.cs", ChangeType = "add" },
            new() { FilePath = "/src/D.cs", ChangeType = "edit" },
        };

        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/src/A.cs",
            "/src/C.cs"
        };

        var deltaFiles = new List<FileChange>();
        var carriedForward = new List<FileChange>();

        foreach (var fc in fileChanges)
        {
            if (changedPaths.Contains(fc.FilePath))
                deltaFiles.Add(fc);
            else
                carriedForward.Add(fc);
        }

        Assert.AreEqual(2, deltaFiles.Count, "Should have 2 delta files.");
        Assert.AreEqual(2, carriedForward.Count, "Should have 2 carried-forward files.");
        Assert.IsTrue(deltaFiles.Any(f => f.FilePath == "/src/A.cs"));
        Assert.IsTrue(deltaFiles.Any(f => f.FilePath == "/src/C.cs"));
        Assert.IsTrue(carriedForward.Any(f => f.FilePath == "/src/B.cs"));
        Assert.IsTrue(carriedForward.Any(f => f.FilePath == "/src/D.cs"));
    }

    [TestMethod]
    public void DeltaPartitioning_AllFilesChanged_AllInDelta()
    {
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit" },
            new() { FilePath = "/src/B.cs", ChangeType = "edit" },
        };

        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/src/A.cs",
            "/src/B.cs"
        };

        var deltaFiles = new List<FileChange>();
        var carriedForward = new List<FileChange>();

        foreach (var fc in fileChanges)
        {
            if (changedPaths.Contains(fc.FilePath))
                deltaFiles.Add(fc);
            else
                carriedForward.Add(fc);
        }

        Assert.AreEqual(2, deltaFiles.Count);
        Assert.AreEqual(0, carriedForward.Count,
            "No files should be carried forward when all changed.");
    }

    [TestMethod]
    public void DeltaPartitioning_CaseInsensitiveMatching()
    {
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/MyFile.cs", ChangeType = "edit" },
            new() { FilePath = "/src/Other.cs", ChangeType = "edit" },
        };

        var changedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "/src/myfile.cs" // lowercase
        };

        var deltaFiles = fileChanges.Where(fc => changedPaths.Contains(fc.FilePath)).ToList();
        var carriedForward = fileChanges.Where(fc => !changedPaths.Contains(fc.FilePath)).ToList();

        Assert.AreEqual(1, deltaFiles.Count,
            "Should match case-insensitively.");
        Assert.AreEqual("/src/MyFile.cs", deltaFiles[0].FilePath);
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  8. BuildSummaryMarkdown — Details/Summary HTML structure
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_UsesCollapsibleSections()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(
            changedPaths: new List<string> { "/src/A.cs" },
            carriedForwardPaths: new List<string> { "/src/B.cs" });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        // Verify <details> blocks for collapsible sections
        Assert.IsTrue(md.Contains("<details><summary>Files reviewed in this pass</summary>"));
        Assert.IsTrue(md.Contains("<details><summary>Files carried forward from prior review</summary>"));
        Assert.IsTrue(md.Contains("</details>"));
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_MetricsTableFormat()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo();

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        // Verify table structure
        Assert.IsTrue(md.Contains("| Metric | Count |"), "Should have table header.");
        Assert.IsTrue(md.Contains("|--------|------:|"), "Should have alignment row.");
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  9. Edge cases
    // ═══════════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_EmptyChangedFiles_NoChangedSection()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(
            changedPaths: new List<string>(),
            carriedForwardPaths: new List<string> { "/src/A.cs" });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsFalse(md.Contains("Files reviewed in this pass"),
            "Should not show 'reviewed' section when no files were changed.");
        Assert.IsTrue(md.Contains("Files carried forward from prior review"),
            "Should still show carried-forward section.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_SingleIteration_ShowsIterationRange()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(baseIteration: 5, currentIteration: 6);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("iteration 5 → 6"));
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_MultipleIterationSkip_ShowsRange()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo(baseIteration: 2, currentIteration: 8);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        Assert.IsTrue(md.Contains("iteration 2 → 8"),
            "Should show full iteration range even when iterations were skipped.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_WithSessionId_BothPresent()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo();
        var sessionId = Guid.NewGuid();

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, sessionId: sessionId, deltaInfo: delta);

        Assert.IsTrue(md.Contains("Incremental Review"),
            "Delta section should be present.");
        Assert.IsTrue(md.Contains($"Session: {sessionId}"),
            "Session ID footer should also be present.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_WithDepthMode_BothPresent()
    {
        var result = MakeResult();
        var delta = MakeDeltaInfo();

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, reviewDepth: ReviewDepth.Deep, deltaInfo: delta);

        Assert.IsTrue(md.Contains("Incremental Review"));
    }

    [TestMethod]
    public void BuildSummaryMarkdown_DeltaReview_LargeFileList_AllDisplayed()
    {
        var result = MakeResult();
        var changedPaths = Enumerable.Range(1, 50)
            .Select(i => $"/src/Changed{i:D3}.cs").ToList();
        var carriedPaths = Enumerable.Range(1, 100)
            .Select(i => $"/src/Carried{i:D3}.cs").ToList();

        var delta = MakeDeltaInfo(
            totalFiles: 150,
            changedPaths: changedPaths,
            carriedForwardPaths: carriedPaths);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, deltaInfo: delta);

        // Verify first and last files are present (sorted)
        Assert.IsTrue(md.Contains("/src/Changed001.cs"));
        Assert.IsTrue(md.Contains("/src/Changed050.cs"));
        Assert.IsTrue(md.Contains("/src/Carried001.cs"));
        Assert.IsTrue(md.Contains("/src/Carried100.cs"));
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  10. Orchestrator Integration — Delta Review via BuildFullyFake
    // ═══════════════════════════════════════════════════════════════════════

    /// <summary>
    /// Seeds a PR in the fake DevOps service with prior metadata indicating
    /// a previous review, then triggers a re-review to exercise the delta path.
    /// </summary>
    private static (FakeDevOpsService fakeDevOps, FakeCodeReviewService fakeAi) SeedDeltaPr(
        string project, string repo, int prId,
        int priorIteration, int currentIteration,
        List<FileChange> fileChanges,
        IEnumerable<string>? changedSinceLastReview = null)
    {
        var fakeDevOps = new FakeDevOpsService();
        var fakeAi = new FakeCodeReviewService();

        var prInfo = new PullRequestInfo
        {
            PullRequestId = prId,
            Title = $"Delta Test PR {prId}",
            Status = "active",
            IsDraft = false,
            LastMergeSourceCommit = "new-commit-hash",  // Different from prior
            TargetBranch = "refs/heads/main",
            SourceBranch = $"refs/heads/feature/delta-{prId}",
        };
        fakeDevOps.SeedPullRequest(project, repo, prInfo);
        fakeDevOps.SeedFileChanges(project, repo, prId, fileChanges);
        fakeDevOps.SeedIterationCount(project, repo, prId, currentIteration);

        // Seed prior review metadata so DetermineAction returns ReReview
        fakeDevOps.SetReviewMetadataAsync(project, repo, prId, new ReviewMetadata
        {
            LastReviewedSourceCommit = "old-commit-hash",
            LastReviewedIteration = priorIteration,
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow.AddHours(-1),
        }).GetAwaiter().GetResult();

        if (changedSinceLastReview != null)
        {
            fakeDevOps.SeedIterationChanges(changedSinceLastReview);
        }

        return (fakeDevOps, fakeAi);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_OnlyChangedFilesSentToAi()
    {
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit", OriginalContent = "// a old", ModifiedContent = "// a new" },
            new() { FilePath = "/src/B.cs", ChangeType = "edit", OriginalContent = "// b old", ModifiedContent = "// b new" },
            new() { FilePath = "/src/C.cs", ChangeType = "edit", OriginalContent = "// c old", ModifiedContent = "// c new" },
        };

        var (fakeDevOps, fakeAi) = SeedDeltaPr(
            "DeltaProj", "DeltaRepo", 200,
            priorIteration: 1, currentIteration: 3,
            fileChanges,
            changedSinceLastReview: new[] { "/src/A.cs" }); // Only A.cs changed

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 200, simulationOnly: true);

        Assert.AreEqual("Simulated", response.Status);
        Assert.IsNotNull(response.DeltaInfo, "DeltaInfo should be populated for incremental review.");
        Assert.IsTrue(response.DeltaInfo.IsDeltaReview);
        Assert.AreEqual(1, response.DeltaInfo.DeltaFilesReviewed,
            "Only the 1 changed file should be reviewed.");
        Assert.AreEqual(2, response.DeltaInfo.CarriedForwardFiles,
            "2 unchanged files should be carried forward.");
        Assert.AreEqual(1, response.DeltaInfo.BaseIteration);
        Assert.AreEqual(3, response.DeltaInfo.CurrentIteration);
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_AllFilesChanged_NoDelta()
    {
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/B.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
        };

        // All files changed → no delta (changedPaths.Count == fileChanges.Count)
        var (fakeDevOps, fakeAi) = SeedDeltaPr(
            "DeltaProj", "DeltaRepo", 201,
            priorIteration: 1, currentIteration: 2,
            fileChanges,
            changedSinceLastReview: new[] { "/src/A.cs", "/src/B.cs" });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 201, simulationOnly: true);

        Assert.AreEqual("Simulated", response.Status);
        Assert.IsNull(response.DeltaInfo,
            "DeltaInfo should be null when all files changed.");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_NoIterationChanges_FullReview()
    {
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
        };

        // No iteration changes seeded (API returns empty set) → full review
        var (fakeDevOps, fakeAi) = SeedDeltaPr(
            "DeltaProj", "DeltaRepo", 202,
            priorIteration: 1, currentIteration: 2,
            fileChanges);
        // Don't seed iteration changes

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 202, simulationOnly: true);

        Assert.AreEqual("Simulated", response.Status);
        Assert.IsNull(response.DeltaInfo,
            "DeltaInfo should be null when iteration changes API returns empty.");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_FirstReview_NoDelta()
    {
        // First review (no prior metadata) → FullReview, no delta
        var fakeDevOps = new FakeDevOpsService();
        var fakeAi = new FakeCodeReviewService();

        var prInfo = new PullRequestInfo
        {
            PullRequestId = 203,
            Title = "First Review PR",
            Status = "active",
            IsDraft = false,
            LastMergeSourceCommit = "first-commit",
            TargetBranch = "refs/heads/main",
            SourceBranch = "refs/heads/feature/first",
        };
        fakeDevOps.SeedPullRequest("DeltaProj", "DeltaRepo", prInfo);
        fakeDevOps.SeedFileChanges("DeltaProj", "DeltaRepo", 203, new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
        });
        fakeDevOps.SeedIterationCount("DeltaProj", "DeltaRepo", 203, 1);

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 203, simulationOnly: true);

        Assert.AreEqual("Simulated", response.Status);
        Assert.IsNull(response.DeltaInfo,
            "DeltaInfo should be null for first review (no prior iteration).");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_SummaryContainsDeltaSection()
    {
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/Changed.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/Unchanged1.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/Unchanged2.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
        };

        var (fakeDevOps, fakeAi) = SeedDeltaPr(
            "DeltaProj", "DeltaRepo", 204,
            priorIteration: 2, currentIteration: 4,
            fileChanges,
            changedSinceLastReview: new[] { "/src/Changed.cs" });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 204, simulationOnly: true);

        Assert.IsNotNull(response.Summary);
        Assert.IsTrue(response.Summary.Contains("Incremental Review"),
            "Summary should contain incremental review section.");
        Assert.IsTrue(response.Summary.Contains("iteration 2 → 4"),
            "Summary should show iteration range.");
        Assert.IsTrue(response.Summary.Contains("/src/Changed.cs"),
            "Summary should list the changed file.");
        Assert.IsTrue(response.Summary.Contains("/src/Unchanged1.cs"),
            "Summary should list carried-forward files.");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_TokenSavingsEstimated()
    {
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/B.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/C.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/D.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
        };

        // Only 1 of 4 files changed → 75% savings expected
        var (fakeDevOps, fakeAi) = SeedDeltaPr(
            "DeltaProj", "DeltaRepo", 205,
            priorIteration: 1, currentIteration: 2,
            fileChanges,
            changedSinceLastReview: new[] { "/src/A.cs" });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 205, simulationOnly: true);

        Assert.IsNotNull(response.DeltaInfo);
        Assert.AreEqual(1, response.DeltaInfo.DeltaFilesReviewed);
        Assert.AreEqual(3, response.DeltaInfo.CarriedForwardFiles);
        // Token savings should be > 0 if AI produced tokens
        if (response.TotalTokens is > 0)
        {
            Assert.IsTrue(response.DeltaInfo.EstimatedTokenSavings > 0,
                "Should estimate token savings when tokens were used.");
        }
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_SameIteration_NoDelta()
    {
        // Prior iteration == current iteration → delta detection should not trigger
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
        };

        var (fakeDevOps, fakeAi) = SeedDeltaPr(
            "DeltaProj", "DeltaRepo", 206,
            priorIteration: 2, currentIteration: 2,  // Same iteration
            fileChanges,
            changedSinceLastReview: new[] { "/src/A.cs" });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 206, simulationOnly: true);

        Assert.AreEqual("Simulated", response.Status);
        Assert.IsNull(response.DeltaInfo,
            "DeltaInfo should be null when iteration hasn't advanced.");
    }

    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(30_000)]
    public async Task Orchestrator_DeltaReview_ForceReview_StillDoesDelta()
    {
        // forceReview triggers ReReview action but delta optimization still applies
        var fileChanges = new List<FileChange>
        {
            new() { FilePath = "/src/A.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/B.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
            new() { FilePath = "/src/C.cs", ChangeType = "edit", OriginalContent = "// old", ModifiedContent = "// new" },
        };

        var (fakeDevOps, fakeAi) = SeedDeltaPr(
            "DeltaProj", "DeltaRepo", 207,
            priorIteration: 1, currentIteration: 3,
            fileChanges,
            changedSinceLastReview: new[] { "/src/A.cs" });

        await using var ctx = TestServiceBuilder.BuildFullyFake(fakeAi: fakeAi, fakeDevOps: fakeDevOps);
        var response = await ctx.Orchestrator.ExecuteReviewAsync(
            "DeltaProj", "DeltaRepo", 207,
            forceReview: true, simulationOnly: true);

        Assert.AreEqual("Simulated", response.Status);
        Assert.IsNotNull(response.DeltaInfo,
            "Delta should still trigger with forceReview.");
        Assert.AreEqual(1, response.DeltaInfo.DeltaFilesReviewed);
        Assert.AreEqual(2, response.DeltaInfo.CarriedForwardFiles);
    }
}
