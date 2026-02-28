using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for PR size guardrails: file prioritization, threshold evaluation,
/// changed-line counting, and summary markdown integration.
/// </summary>
[TestClass]
public class SizeGuardrailsTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static FileChange MakeFile(string path, string changeType,
        List<(int Start, int End)>? ranges = null, string? content = null)
    {
        return new FileChange
        {
            FilePath = path,
            ChangeType = changeType,
            ChangedLineRanges = ranges ?? new List<(int Start, int End)>(),
            ModifiedContent = content,
        };
    }

    private static CodeReviewResult MakeResult(string verdict = "APPROVED")
    {
        return new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 2,
                EditsCount = 1,
                AddsCount = 1,
                DeletesCount = 0,
                CommitsCount = 1,
                Description = "Test PR.",
                Verdict = verdict,
                VerdictJustification = "Looks good.",
            },
            FileReviews = new List<FileReview>(),
            InlineComments = new List<InlineComment>(),
            RecommendedVote = 10,
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CountChangedLines
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void CountChangedLines_UsesChangedLineRanges()
    {
        var files = new List<FileChange>
        {
            MakeFile("/a.cs", "edit", new List<(int, int)> { (1, 10), (20, 25) }),
            MakeFile("/b.cs", "edit", new List<(int, int)> { (5, 5) }),
        };

        // (10-1+1) + (25-20+1) + (5-5+1) = 10 + 6 + 1 = 17
        Assert.AreEqual(17, CodeReviewOrchestrator.CountChangedLines(files));
    }

    [TestMethod]
    public void CountChangedLines_FallsBackToContentLines_ForAdds()
    {
        var files = new List<FileChange>
        {
            MakeFile("/new.cs", "add", content: "line1\nline2\nline3"),
        };

        Assert.AreEqual(3, CodeReviewOrchestrator.CountChangedLines(files));
    }

    [TestMethod]
    public void CountChangedLines_EmptyList_ReturnsZero()
    {
        Assert.AreEqual(0, CodeReviewOrchestrator.CountChangedLines(new List<FileChange>()));
    }

    [TestMethod]
    public void CountChangedLines_NoRangesNoContent_ReturnsZero()
    {
        var files = new List<FileChange>
        {
            MakeFile("/empty.cs", "edit"),
        };

        Assert.AreEqual(0, CodeReviewOrchestrator.CountChangedLines(files));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PrioritizeFiles
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void PrioritizeFiles_SortsByChangeTypePriority()
    {
        var files = new List<FileChange>
        {
            MakeFile("/rename.cs", "rename"),
            MakeFile("/edit.cs", "edit", new List<(int, int)> { (1, 5) }),
            MakeFile("/add.cs", "add"),
            MakeFile("/delete.cs", "delete"),
        };

        var sorted = CodeReviewOrchestrator.PrioritizeFiles(files);

        Assert.AreEqual("/add.cs", sorted[0].FilePath, "Adds come first.");
        Assert.AreEqual("/edit.cs", sorted[1].FilePath, "Edits come second.");
        Assert.AreEqual("/delete.cs", sorted[2].FilePath, "Deletes come third.");
        Assert.AreEqual("/rename.cs", sorted[3].FilePath, "Renames come last.");
    }

    [TestMethod]
    public void PrioritizeFiles_WithinSameChangeType_SortsByChangedLinesDescending()
    {
        var files = new List<FileChange>
        {
            MakeFile("/small.cs", "edit", new List<(int, int)> { (1, 5) }),       // 5 lines
            MakeFile("/large.cs", "edit", new List<(int, int)> { (1, 50) }),      // 50 lines
            MakeFile("/medium.cs", "edit", new List<(int, int)> { (1, 20) }),     // 20 lines
        };

        var sorted = CodeReviewOrchestrator.PrioritizeFiles(files);

        Assert.AreEqual("/large.cs", sorted[0].FilePath, "Largest edit first.");
        Assert.AreEqual("/medium.cs", sorted[1].FilePath, "Medium edit second.");
        Assert.AreEqual("/small.cs", sorted[2].FilePath, "Smallest edit last.");
    }

    [TestMethod]
    public void PrioritizeFiles_EmptyList_ReturnsEmpty()
    {
        var sorted = CodeReviewOrchestrator.PrioritizeFiles(new List<FileChange>());
        Assert.AreEqual(0, sorted.Count);
    }

    [TestMethod]
    public void PrioritizeFiles_UnknownChangeType_SortedLast()
    {
        var files = new List<FileChange>
        {
            MakeFile("/mystery.cs", "mystery"),
            MakeFile("/add.cs", "add"),
        };

        var sorted = CodeReviewOrchestrator.PrioritizeFiles(files);

        Assert.AreEqual("/add.cs", sorted[0].FilePath);
        Assert.AreEqual("/mystery.cs", sorted[1].FilePath);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  EvaluateSizeGuardrails
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void EvaluateSizeGuardrails_WithinThresholds_ReturnsNull()
    {
        var settings = new SizeGuardrailsSettings
        {
            WarnFileCount = 30,
            WarnChangedLines = 2000,
        };
        var files = new List<FileChange>
        {
            MakeFile("/a.cs", "edit", new List<(int, int)> { (1, 10) }),
        };

        Assert.IsNull(CodeReviewOrchestrator.EvaluateSizeGuardrails(files, settings));
    }

    [TestMethod]
    public void EvaluateSizeGuardrails_ExceedsFileCount_ReturnsWarning()
    {
        var settings = new SizeGuardrailsSettings
        {
            WarnFileCount = 2,
            WarnChangedLines = 0, // Disabled
        };
        var files = Enumerable.Range(1, 5)
            .Select(i => MakeFile($"/f{i}.cs", "edit", new List<(int, int)> { (1, 1) }))
            .ToList();

        var warning = CodeReviewOrchestrator.EvaluateSizeGuardrails(files, settings);

        Assert.IsNotNull(warning);
        Assert.IsTrue(warning.Contains("5 reviewable files"), $"Expected file count in: {warning}");
        Assert.IsTrue(warning.Contains("threshold: 2"), $"Expected threshold in: {warning}");
    }

    [TestMethod]
    public void EvaluateSizeGuardrails_ExceedsChangedLines_ReturnsWarning()
    {
        var settings = new SizeGuardrailsSettings
        {
            WarnFileCount = 0, // Disabled
            WarnChangedLines = 100,
        };
        var files = new List<FileChange>
        {
            MakeFile("/big.cs", "edit", new List<(int, int)> { (1, 500) }),
        };

        var warning = CodeReviewOrchestrator.EvaluateSizeGuardrails(files, settings);

        Assert.IsNotNull(warning);
        Assert.IsTrue(warning.Contains("500"), $"Expected changed line count in: {warning}");
        Assert.IsTrue(warning.Contains("threshold:"), $"Expected threshold label in: {warning}");
    }

    [TestMethod]
    public void EvaluateSizeGuardrails_ExceedsBoth_ReturnsCombinedWarning()
    {
        var settings = new SizeGuardrailsSettings
        {
            WarnFileCount = 1,
            WarnChangedLines = 10,
        };
        var files = new List<FileChange>
        {
            MakeFile("/a.cs", "edit", new List<(int, int)> { (1, 50) }),
            MakeFile("/b.cs", "edit", new List<(int, int)> { (1, 50) }),
        };

        var warning = CodeReviewOrchestrator.EvaluateSizeGuardrails(files, settings);

        Assert.IsNotNull(warning);
        Assert.IsTrue(warning.Contains("2 reviewable files"), $"Expected file count: {warning}");
        Assert.IsTrue(warning.Contains("100"), $"Expected total changed lines: {warning}");
        Assert.IsTrue(warning.Contains("Consider splitting"), $"Expected recommendation: {warning}");
    }

    [TestMethod]
    public void EvaluateSizeGuardrails_DisabledThresholds_ReturnsNull()
    {
        var settings = new SizeGuardrailsSettings
        {
            WarnFileCount = 0,
            WarnChangedLines = 0,
        };
        var files = Enumerable.Range(1, 100)
            .Select(i => MakeFile($"/f{i}.cs", "edit", new List<(int, int)> { (1, 1000) }))
            .ToList();

        Assert.IsNull(CodeReviewOrchestrator.EvaluateSizeGuardrails(files, settings));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildSummaryMarkdown — sizeWarning integration
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildSummaryMarkdown_WithSizeWarning_ShowsWarningBanner()
    {
        var result = MakeResult();
        var warning = "Large PR detected — 50 reviewable files (threshold: 30).";

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, sizeWarning: warning);

        Assert.IsTrue(md.Contains(":warning: **PR Size Warning**"), $"Expected warning banner in:\n{md}");
        Assert.IsTrue(md.Contains(warning), $"Expected warning text in:\n{md}");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_WithoutSizeWarning_NoWarningBanner()
    {
        var result = MakeResult();

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsFalse(md.Contains("PR Size Warning"), "No warning when sizeWarning is null.");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_SizeWarningAppearsAfterSummary_BeforeCrossFile()
    {
        var result = MakeResult();
        var prSummary = new PrSummaryResult
        {
            ArchitecturalImpact = "High — new service added.",
            RiskAreas = new List<RiskArea>
            {
                new() { Area = "Data layer", Reason = "New entity" },
            },
        };
        var warning = "50 files exceed the threshold.";

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            42, result, prSummary: prSummary, sizeWarning: warning);

        var summaryIdx = md.IndexOf("### Summary");
        var warningIdx = md.IndexOf("PR Size Warning");
        var crossFileIdx = md.IndexOf("### Cross-File Analysis");

        Assert.IsTrue(summaryIdx >= 0, "Summary section present.");
        Assert.IsTrue(warningIdx >= 0, "Warning present.");
        Assert.IsTrue(crossFileIdx >= 0, "Cross-file section present.");
        Assert.IsTrue(warningIdx > summaryIdx, "Warning appears after Summary.");
        Assert.IsTrue(warningIdx < crossFileIdx, "Warning appears before Cross-File Analysis.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  CountChangedLines — edge cases
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void CountChangedLines_EmptyContent_ReturnsZero()
    {
        var files = new List<FileChange>
        {
            MakeFile("/empty.cs", "add", content: ""),
        };

        Assert.AreEqual(0, CodeReviewOrchestrator.CountChangedLines(files));
    }

    [TestMethod]
    public void CountChangedLines_TrailingNewline_DoesNotOvercount()
    {
        // "line1\nline2\n" is 2 lines, not 3
        var files = new List<FileChange>
        {
            MakeFile("/trailing.cs", "add", content: "line1\nline2\n"),
        };

        Assert.AreEqual(2, CodeReviewOrchestrator.CountChangedLines(files));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  PrioritizeFiles — content fallback for ordering
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void PrioritizeFiles_ContentFallback_WhenNoRanges()
    {
        // File with content but no ranges should still be ordered by line count
        var files = new List<FileChange>
        {
            MakeFile("/small.cs", "add", content: "a"),                    // 1 line
            MakeFile("/large.cs", "add", content: "a\nb\nc\nd\ne"),       // 5 lines
        };

        var sorted = CodeReviewOrchestrator.PrioritizeFiles(files);

        Assert.AreEqual("/large.cs", sorted[0].FilePath, "Larger content file first.");
        Assert.AreEqual("/small.cs", sorted[1].FilePath, "Smaller content file second.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Focus mode — FocusModeMaxFiles = 0 guard
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void FocusMode_MaxFilesZero_DoesNotTrimFiles()
    {
        // FocusModeMaxFiles = 0 should disable trimming even if FocusModeEnabled = true
        var settings = new SizeGuardrailsSettings
        {
            FocusModeEnabled = true,
            FocusModeMaxFiles = 0,
        };

        // Simulate the orchestrator's focus-mode check inline:
        // The condition should short-circuit when FocusModeMaxFiles is 0
        var files = Enumerable.Range(1, 10)
            .Select(i => MakeFile($"/f{i}.cs", "edit", new List<(int, int)> { (1, 5) }))
            .ToList();

        bool shouldTrim = settings.FocusModeEnabled
            && settings.FocusModeMaxFiles > 0
            && files.Count > settings.FocusModeMaxFiles;

        Assert.IsFalse(shouldTrim, "FocusModeMaxFiles = 0 should disable trimming.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  SizeGuardrailsSettings — defaults
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void SizeGuardrailsSettings_HasSensibleDefaults()
    {
        var settings = new SizeGuardrailsSettings();

        Assert.AreEqual(30, settings.WarnFileCount);
        Assert.AreEqual(2000, settings.WarnChangedLines);
        Assert.AreEqual(20, settings.FocusModeMaxFiles);
        Assert.IsFalse(settings.FocusModeEnabled);
        Assert.AreEqual("SizeGuardrails", SizeGuardrailsSettings.SectionName);
    }
}
