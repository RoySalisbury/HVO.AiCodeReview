using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for <see cref="CodeReviewOrchestrator.BuildSummaryMarkdown"/>.
/// Focuses on rejection / needs-work verdicts, ensuring blocking issues
/// are always surfaced with file paths — even when fileReviews is empty.
/// </summary>
[TestClass]
public class BuildSummaryMarkdownTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static CodeReviewResult MakeResult(
        string verdict,
        string justification,
        List<FileReview>? fileReviews = null,
        List<InlineComment>? inlineComments = null,
        int recommendedVote = 10)
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
                Description = "Test PR description.",
                Verdict = verdict,
                VerdictJustification = justification,
            },
            FileReviews = fileReviews ?? new List<FileReview>(),
            InlineComments = inlineComments ?? new List<InlineComment>(),
            RecommendedVote = recommendedVote,
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  1. Approved verdict — no blocking issues section
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Approved_NoBlockingIssuesSection()
    {
        var result = MakeResult("APPROVED", "All good.");

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(123, result);

        Assert.IsTrue(md.Contains("### Verdict: **APPROVED**"), "Verdict header present.");
        Assert.IsFalse(md.Contains("Blocking Issues"), "No blocking issues for approved PRs.");
    }

    [TestMethod]
    public void ApprovedWithSuggestions_NoBlockingIssuesSection()
    {
        var result = MakeResult("APPROVED WITH SUGGESTIONS", "Minor suggestions only.");

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(456, result);

        Assert.IsTrue(md.Contains("### Verdict: **APPROVED WITH SUGGESTIONS**"));
        Assert.IsFalse(md.Contains("Blocking Issues"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  2. Rejected with fileReviews — blocking issues from fileReviews
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Rejected_WithFileReviews_ShowsBlockingIssuesWithFilePaths()
    {
        var result = MakeResult(
            "REJECTED",
            "The file `/src/services/ExchangeCurrencyService.cs` contains only a hash.",
            fileReviews: new List<FileReview>
            {
                new FileReview
                {
                    FilePath = "/src/services/ExchangeCurrencyService.cs",
                    Verdict = "NEEDS WORK",
                    ReviewText = "File contains only a commit hash with no functional code."
                },
                new FileReview
                {
                    FilePath = "/src/server.js",
                    Verdict = "CONCERN",
                    ReviewText = "Race condition in initInProgress flag."
                }
            },
            recommendedVote: -10);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(999, result);

        // Verdict present
        Assert.IsTrue(md.Contains("### Verdict: **REJECTED**"), "Verdict header present.");

        // Blocking Issues section present with both files
        Assert.IsTrue(md.Contains("### :x: Blocking Issues"), "Blocking issues section present.");
        Assert.IsTrue(md.Contains("`/src/services/ExchangeCurrencyService.cs`"),
            "Blocking issue references the empty file by path.");
        Assert.IsTrue(md.Contains("`/src/server.js`"),
            "Blocking issue references the concern file by path.");
        Assert.IsTrue(md.Contains("commit hash"), "Blocking issue describes the problem.");
        Assert.IsTrue(md.Contains("Race condition"), "Blocking issue describes the concern.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  3. Rejected WITHOUT fileReviews — falls back to inline comments
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Rejected_NoFileReviews_FallsBackToInlineComments()
    {
        var result = MakeResult(
            "REJECTED",
            "Critical issues found.",
            fileReviews: new List<FileReview>(), // empty!
            inlineComments: new List<InlineComment>
            {
                new InlineComment
                {
                    FilePath = "/src/lib/submodules.js",
                    StartLine = 42,
                    EndLine = 42,
                    LeadIn = "Bug",
                    Comment = "Silent failure when git call fails.",
                    Status = "active"
                },
                new InlineComment
                {
                    FilePath = "/src/config.json",
                    StartLine = 1,
                    EndLine = 1,
                    LeadIn = "Concern",
                    Comment = "Missing required field.",
                    Status = "active"
                }
            },
            recommendedVote: -10);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(888, result);

        Assert.IsTrue(md.Contains("### Verdict: **REJECTED**"));
        Assert.IsTrue(md.Contains("### :x: Blocking Issues"),
            "Blocking issues section should appear even without fileReviews.");
        Assert.IsTrue(md.Contains("`/src/lib/submodules.js`"),
            "Inline comment file path surfaces in blocking issues.");
        Assert.IsTrue(md.Contains("`/src/config.json`"),
            "Second inline comment file path surfaces.");
        Assert.IsTrue(md.Contains("Silent failure"),
            "Inline comment description included.");
        Assert.IsTrue(md.Contains("[Line 42]"),
            "Line number included for inline-based blocking issue.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  4. Rejected with NO fileReviews AND no inline comments
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Rejected_NoFileReviews_NoInlineComments_NoBlockingSection()
    {
        var result = MakeResult(
            "REJECTED",
            "The file `/src/empty.cs` has no content.",
            fileReviews: new List<FileReview>(),
            inlineComments: new List<InlineComment>(),
            recommendedVote: -10);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(777, result);

        Assert.IsTrue(md.Contains("### Verdict: **REJECTED**"));
        // justification should still reference the file
        Assert.IsTrue(md.Contains("/src/empty.cs"),
            "Verdict justification mentions the file path.");
        // No blocking issues section when there are no items to populate it
        Assert.IsFalse(md.Contains("### :x: Blocking Issues"),
            "No blocking issues section when no file reviews or inline comments.");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  5. NEEDS WORK verdict — same blocking issues behavior
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void NeedsWork_WithFileReviews_ShowsBlockingIssues()
    {
        var result = MakeResult(
            "NEEDS WORK",
            "Missing error handling in `/src/handler.ts`.",
            fileReviews: new List<FileReview>
            {
                new FileReview
                {
                    FilePath = "/src/handler.ts",
                    Verdict = "CONCERN",
                    ReviewText = "No try-catch around async operations."
                }
            },
            recommendedVote: -5);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(555, result);

        Assert.IsTrue(md.Contains("### Verdict: **NEEDS WORK**"));
        Assert.IsTrue(md.Contains("### :x: Blocking Issues"));
        Assert.IsTrue(md.Contains("`/src/handler.ts`"));
        Assert.IsTrue(md.Contains("No try-catch"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  6. Empty / stub file scenario (hash-only content)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Rejected_EmptyStubFile_FilePathInBlockingIssues()
    {
        var result = MakeResult(
            "REJECTED",
            "The file `/src/services/ExchangeCurrencyService.cs` contains only a git hash " +
            "'0dedb30abb1e9ccea54b957708adc72eca7baee8' with no functional code. " +
            "This is invalid for a new file addition and must be replaced with actual implementation.",
            fileReviews: new List<FileReview>
            {
                new FileReview
                {
                    FilePath = "/src/services/ExchangeCurrencyService.cs",
                    Verdict = "NEEDS WORK",
                    ReviewText = "File contains only a commit hash (0dedb30...) with no functional code."
                }
            },
            recommendedVote: -10);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(63543, result);

        // File path MUST appear in verdict justification
        Assert.IsTrue(md.Contains("/src/services/ExchangeCurrencyService.cs"),
            "File path must appear in the verdict justification.");

        // Blocking Issues section must also list the file
        Assert.IsTrue(md.Contains("### :x: Blocking Issues"));
        Assert.IsTrue(md.Contains("`/src/services/ExchangeCurrencyService.cs`"),
            "Blocking issues section must reference the stub file by path.");
        Assert.IsTrue(md.Contains("only a commit hash") || md.Contains("commit hash"),
            "Blocking issue describes the problem (hash-only content).");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  7. Re-review header and prior metadata
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReReview_Rejected_ShowsReReviewHeaderAndBlockingIssues()
    {
        var result = MakeResult(
            "REJECTED",
            "The file `/src/broken.py` has a critical bug on line 15.",
            fileReviews: new List<FileReview>
            {
                new FileReview
                {
                    FilePath = "/src/broken.py",
                    Verdict = "CONCERN",
                    ReviewText = "Null dereference on line 15."
                }
            },
            recommendedVote: -10);

        var priorMeta = new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = new DateTime(2026, 2, 27, 10, 0, 0, DateTimeKind.Utc),
            LastReviewedSourceCommit = "abc1234567890",
            LastReviewedIteration = 1,
            VoteSubmitted = true,
            WasDraft = false,
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(
            63543, result, isReReview: true, reviewNumber: 2, priorMetadata: priorMeta);

        Assert.IsTrue(md.Contains("## Re-Review (Review 2)"), "Re-review header present.");
        Assert.IsTrue(md.Contains("Prior review"), "Prior review context present.");
        Assert.IsTrue(md.Contains("### Verdict: **REJECTED**"));
        Assert.IsTrue(md.Contains("### :x: Blocking Issues"));
        Assert.IsTrue(md.Contains("`/src/broken.py`"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  8. Code Changes Review section appears for concerns
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Rejected_FileReviewsConcern_AppearsInCodeChangesSection()
    {
        var result = MakeResult(
            "REJECTED",
            "Issues in `/src/api.ts`.",
            fileReviews: new List<FileReview>
            {
                new FileReview
                {
                    FilePath = "/src/api.ts",
                    Verdict = "CONCERN",
                    ReviewText = "Unhandled promise rejection."
                },
                new FileReview
                {
                    FilePath = "/src/clean.ts",
                    Verdict = "APPROVED",
                    ReviewText = "No issues found."
                }
            },
            recommendedVote: -10);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(111, result);

        // Code Changes Review section should include CONCERN but not APPROVED
        Assert.IsTrue(md.Contains("### Code Changes Review"), "Code Changes Review section present.");
        Assert.IsTrue(md.Contains("`/src/api.ts` -- CONCERN"), "Concern file in Code Changes Review.");
        Assert.IsFalse(md.Contains("`/src/clean.ts` -- APPROVED"),
            "Approved files should not appear in Code Changes Review.");

        // Blocking Issues also present
        Assert.IsTrue(md.Contains("### :x: Blocking Issues"));
        Assert.IsTrue(md.Contains("`/src/api.ts`"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  9. Active vs closed inline comments in fallback
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Rejected_NoFileReviews_OnlyActiveInlineCommentsInBlockingIssues()
    {
        var result = MakeResult(
            "REJECTED",
            "Critical issues found.",
            fileReviews: new List<FileReview>(),
            inlineComments: new List<InlineComment>
            {
                new InlineComment
                {
                    FilePath = "/src/helper.cs",
                    StartLine = 10,
                    EndLine = 10,
                    LeadIn = "LGTM",
                    Comment = "Nice refactor.",
                    Status = "closed"
                },
                new InlineComment
                {
                    FilePath = "/src/service.cs",
                    StartLine = 55,
                    EndLine = 60,
                    LeadIn = "Bug",
                    Comment = "Off-by-one error in loop boundary.",
                    Status = "active"
                }
            },
            recommendedVote: -10);

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(222, result);

        Assert.IsTrue(md.Contains("### :x: Blocking Issues"));
        // Only the active comment should appear
        Assert.IsTrue(md.Contains("`/src/service.cs`"), "Active bug comment surfaces.");
        Assert.IsTrue(md.Contains("Off-by-one"), "Bug description surfaces.");
        Assert.IsFalse(md.Contains("`/src/helper.cs`"),
            "Closed LGTM comment should NOT appear in blocking issues.");
    }
}
