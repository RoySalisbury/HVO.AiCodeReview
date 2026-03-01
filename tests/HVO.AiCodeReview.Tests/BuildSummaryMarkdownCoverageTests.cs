using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for <see cref="CodeReviewOrchestrator.BuildSummaryMarkdown"/> branches
/// that are not covered by existing tests: test coverage gaps, blocking issues,
/// AC/DoD, re-review without prior metadata, file groupings, code changes review, etc.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class BuildSummaryMarkdownCoverageTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  Test Coverage Gaps section
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void WithTestCoverageGaps_IncludesGapSection()
    {
        var result = MakeResult("APPROVED");
        var gaps = new List<TestCoverageGapDetector.TestCoverageGap>
        {
            new("src/Services/Foo.cs", "edit", new List<string> { "tests/FooTests.cs" }),
            new("src/Services/Bar.cs", "add", new List<string> { "tests/BarTests.cs" }),
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, testCoverageGaps: gaps);

        Assert.IsTrue(md.Contains("Test Coverage Gaps"), "Should contain gap section header");
        Assert.IsTrue(md.Contains("src/Services/Foo.cs"), "Should list Foo.cs");
        Assert.IsTrue(md.Contains("src/Services/Bar.cs"), "Should list Bar.cs");
        Assert.IsTrue(md.Contains("informational observation"), "Should contain disclaimer");
    }

    [TestMethod]
    public void WithEmptyTestCoverageGaps_OmitsGapSection()
    {
        var result = MakeResult("APPROVED");
        var gaps = new List<TestCoverageGapDetector.TestCoverageGap>();

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, testCoverageGaps: gaps);

        Assert.IsFalse(md.Contains("Test Coverage Gaps"), "Should not contain gap section when empty");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Code Changes Review (CONCERN/NEEDS WORK files)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void FilesWithConcern_IncludesCodeChangesReview()
    {
        var result = MakeResult("APPROVED WITH SUGGESTIONS");
        result.FileReviews.Add(new FileReview
        {
            FilePath = "src/Risky.cs",
            Verdict = "CONCERN",
            ReviewText = "Possible null reference on line 42."
        });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsTrue(md.Contains("Code Changes Review"), "Should include Code Changes Review section");
        Assert.IsTrue(md.Contains("src/Risky.cs"), "Should list the file with concern");
        Assert.IsTrue(md.Contains("CONCERN"), "Should show the verdict");
        Assert.IsTrue(md.Contains("null reference"), "Should include review text");
    }

    [TestMethod]
    public void FilesWithNeedsWork_IncludedInCodeChangesReview()
    {
        var result = MakeResult("NEEDS WORK");
        result.FileReviews.Add(new FileReview
        {
            FilePath = "src/Bad.cs",
            Verdict = "NEEDS WORK",
            ReviewText = "Missing error handling."
        });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsTrue(md.Contains("src/Bad.cs"));
        Assert.IsTrue(md.Contains("NEEDS WORK"));
    }

    [TestMethod]
    public void FilesWithRejected_IncludedInCodeChangesReview()
    {
        var result = MakeResult("REJECTED");
        result.FileReviews.Add(new FileReview
        {
            FilePath = "src/Terrible.cs",
            Verdict = "REJECTED",
            ReviewText = "Security vulnerability found."
        });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsTrue(md.Contains("src/Terrible.cs"));
        Assert.IsTrue(md.Contains("REJECTED"));
    }

    [TestMethod]
    public void AiReviewFailed_IncludedInCodeChangesReview()
    {
        var result = MakeResult("APPROVED");
        result.FileReviews.Add(new FileReview
        {
            FilePath = "src/Failed.cs",
            Verdict = "SKIPPED",
            ReviewText = "AI review failed for this file."
        });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsTrue(md.Contains("src/Failed.cs"), "AI-failed files should be included");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Blocking Issues (non-approval verdicts)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void RejectedVerdict_ShowsBlockingIssues()
    {
        var result = MakeResult("REJECTED");
        result.FileReviews.Add(new FileReview
        {
            FilePath = "src/Vuln.cs",
            Verdict = "REJECTED",
            ReviewText = "SQL injection vulnerability."
        });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsTrue(md.Contains("Blocking Issues"), "Should show Blocking Issues for REJECTED");
        Assert.IsTrue(md.Contains("must be resolved"), "Should include resolution requirement");
    }

    [TestMethod]
    public void NeedsWorkVerdict_ShowsBlockingIssues()
    {
        var result = MakeResult("NEEDS WORK");
        result.FileReviews.Add(new FileReview
        {
            FilePath = "src/Incomplete.cs",
            Verdict = "NEEDS WORK",
            ReviewText = "Missing validation."
        });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsTrue(md.Contains("Blocking Issues"), "Should show Blocking Issues for NEEDS WORK");
    }

    [TestMethod]
    public void RejectedVerdict_NoFileReviews_FallsBackToInlineComments()
    {
        var result = MakeResult("REJECTED");
        // No file reviews with issues, but active inline comments
        result.InlineComments.Add(new InlineComment
        {
            FilePath = "src/Problem.cs",
            StartLine = 10,
            EndLine = 10,
            LeadIn = "Bug",
            Comment = "Uninitialized variable",
            Status = "active"
        });

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsTrue(md.Contains("Blocking Issues"), "Should show blocking issues from inline comments");
        Assert.IsTrue(md.Contains("src/Problem.cs"), "Should list file from inline comment");
    }

    [TestMethod]
    public void ApprovedVerdict_NoBlockingIssues()
    {
        var result = MakeResult("APPROVED");

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsFalse(md.Contains("Blocking Issues"), "APPROVED should not have blocking issues");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Re-review — no prior metadata
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReReview_NoPriorMetadata_ShowsFallbackMessage()
    {
        var result = MakeResult("APPROVED");

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result,
            isReReview: true, priorMetadata: null);

        Assert.IsTrue(md.Contains("Re-Review"), "Should show Re-Review header");
        Assert.IsTrue(md.Contains("re-review triggered by new changes"),
            "Should show fallback message without prior metadata");
    }

    [TestMethod]
    public void ReReview_WithPriorMetadata_ShowsPriorReviewDetails()
    {
        var result = MakeResult("APPROVED");
        var prior = new ReviewMetadata
        {
            ReviewCount = 2,
            ReviewedAtUtc = new DateTime(2026, 1, 15, 10, 30, 0, DateTimeKind.Utc),
            LastReviewedSourceCommit = "abcdef1234567890",
            LastReviewedIteration = 3,
            VoteSubmitted = true,
            WasDraft = false,
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result,
            isReReview: true, reviewNumber: 3, priorMetadata: prior);

        Assert.IsTrue(md.Contains("Re-Review"), "Should show Re-Review header");
        Assert.IsTrue(md.Contains("Review #2"), "Should show prior review count");
        Assert.IsTrue(md.Contains("abcdef1"), "Should show truncated commit hash");
        Assert.IsTrue(md.Contains("Iteration 3"), "Should show iteration");
        Assert.IsTrue(md.Contains("vote submitted"), "Should show vote status");
    }

    [TestMethod]
    public void ReReview_PriorMetadataDraft_ShowsDraftLabel()
    {
        var result = MakeResult("APPROVED");
        var prior = new ReviewMetadata
        {
            ReviewCount = 1,
            ReviewedAtUtc = DateTime.UtcNow,
            LastReviewedSourceCommit = "abc1234",
            LastReviewedIteration = 1,
            VoteSubmitted = false,
            WasDraft = true,
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result,
            isReReview: true, priorMetadata: prior);

        Assert.IsTrue(md.Contains("(draft)"), "Should show draft label");
        Assert.IsTrue(md.Contains("no vote"), "Should show no vote");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  File Groupings (prSummary)
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void PrSummary_WithFileGroupings_ShowsGroupings()
    {
        var result = MakeResult("APPROVED");
        var prSummary = new PrSummaryResult
        {
            Intent = "Test intent",
            FileGroupings = new List<FileGrouping>
            {
                new()
                {
                    GroupName = "Service Layer",
                    Description = "Business logic changes",
                    Files = new List<string> { "src/ServiceA.cs", "src/ServiceB.cs" }
                }
            }
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, prSummary: prSummary);

        Assert.IsTrue(md.Contains("File Groupings"), "Should show file groupings header");
        Assert.IsTrue(md.Contains("Service Layer"), "Should show group name");
        Assert.IsTrue(md.Contains("Business logic"), "Should show group description");
        Assert.IsTrue(md.Contains("ServiceA.cs"), "Should list grouped files");
    }

    [TestMethod]
    public void PrSummary_ArchitecturalImpactNone_Omitted()
    {
        var result = MakeResult("APPROVED");
        var prSummary = new PrSummaryResult
        {
            Intent = "Minor fix",
            ArchitecturalImpact = "None",
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, prSummary: prSummary);

        Assert.IsFalse(md.Contains("Architectural Impact"), "Should omit when impact is 'None'");
    }

    [TestMethod]
    public void PrSummary_EmptyCrossFileRelationships_Omitted()
    {
        var result = MakeResult("APPROVED");
        var prSummary = new PrSummaryResult
        {
            Intent = "Simple change",
            CrossFileRelationships = new List<string>(),
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, prSummary: prSummary);

        Assert.IsFalse(md.Contains("Cross-File Relationships"),
            "Should omit empty cross-file relationships section");
    }

    [TestMethod]
    public void PrSummary_EmptyRiskAreas_Omitted()
    {
        var result = MakeResult("APPROVED");
        var prSummary = new PrSummaryResult
        {
            Intent = "Safe change",
            RiskAreas = new List<RiskArea>(),
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, prSummary: prSummary);

        Assert.IsFalse(md.Contains("Risk Areas"), "Should omit empty risk areas section");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Acceptance Criteria / DoD
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void WorkItemsWithAC_ShowsACSection()
    {
        var result = MakeResult("APPROVED");
        var workItems = new List<WorkItemInfo>
        {
            new()
            {
                Id = 100,
                WorkItemType = "User Story",
                Title = "Login feature",
                AcceptanceCriteria = "- Login form validates email\n- Error shown for invalid credentials"
            }
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, workItems: workItems);

        Assert.IsTrue(md.Contains("Acceptance Criteria"), "Should show AC section");
        Assert.IsTrue(md.Contains("Login feature"), "Should show work item title");
    }

    [TestMethod]
    public void AcAnalysis_WithItems_ShowsStatusTable()
    {
        var result = MakeResult("APPROVED");
        result.AcceptanceCriteriaAnalysis = new AcceptanceCriteriaAnalysis
        {
            Summary = "All criteria addressed.",
            Items = new List<AcceptanceCriteriaItem>
            {
                new()
                {
                    Criterion = "Login validates email",
                    Status = "Addressed",
                    Evidence = "EmailValidator class added"
                },
                new()
                {
                    Criterion = "Error handling",
                    Status = "Partially Addressed",
                    Evidence = "Only covers network errors"
                },
                new()
                {
                    Criterion = "Logout button",
                    Status = "Not Addressed",
                    Evidence = "No logout implementation found"
                },
                new()
                {
                    Criterion = "Audit logging",
                    Status = "Cannot Determine",
                    Evidence = "No evidence in changed files"
                }
            }
        };
        var workItems = new List<WorkItemInfo>
        {
            new()
            {
                Id = 100,
                WorkItemType = "User Story",
                Title = "Login",
                AcceptanceCriteria = "Some AC"
            }
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, workItems: workItems);

        Assert.IsTrue(md.Contains(":white_check_mark:"), "Should show check for Addressed");
        Assert.IsTrue(md.Contains(":large_orange_diamond:"), "Should show diamond for Partially");
        Assert.IsTrue(md.Contains(":x:"), "Should show X for Not Addressed");
        Assert.IsTrue(md.Contains(":grey_question:"), "Should show ? for Cannot Determine");
        Assert.IsTrue(md.Contains("Login validates email"), "Should include criterion text");
        Assert.IsTrue(md.Contains("EmailValidator"), "Should include evidence");
    }

    [TestMethod]
    public void WorkItemsWithAC_NoAiAnalysis_ShowsFallbackMessage()
    {
        var result = MakeResult("APPROVED");
        // No AI-generated AC analysis
        var workItems = new List<WorkItemInfo>
        {
            new()
            {
                Id = 100,
                WorkItemType = "User Story",
                Title = "Login",
                AcceptanceCriteria = "Some criteria here"
            }
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, workItems: workItems);

        Assert.IsTrue(md.Contains("Acceptance Criteria"), "Should show AC header");
        Assert.IsTrue(md.Contains("did not produce a per-criterion analysis"),
            "Should show fallback message when AI didn't analyze");
    }

    [TestMethod]
    public void NoWorkItems_NoAcAnalysis_OmitsACSection()
    {
        var result = MakeResult("APPROVED");

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result);

        Assert.IsFalse(md.Contains("Acceptance Criteria"), "Should omit AC section entirely");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Review number label
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewNumber_Zero_NoLabel()
    {
        var result = MakeResult("APPROVED");
        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, reviewNumber: 0);
        Assert.IsFalse(md.Contains("(Review"), "No label when reviewNumber is 0");
    }

    [TestMethod]
    public void ReviewNumber_Three_ShowsLabel()
    {
        var result = MakeResult("APPROVED");
        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, reviewNumber: 3);
        Assert.IsTrue(md.Contains("(Review 3)"), "Should show review number");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Depth badges
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void StandardDepth_NoBadge()
    {
        var result = MakeResult("APPROVED");
        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result,
            reviewDepth: ReviewDepth.Standard);
        Assert.IsFalse(md.Contains(":zap:"), "Standard depth should have no badge");
        Assert.IsFalse(md.Contains(":mag:"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Skipped files note
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void SkippedFiles_ShowsExclusionNote()
    {
        var result = MakeResult("APPROVED");
        var skipped = new List<FileChange>
        {
            new() { FilePath = "package-lock.json", SkipReason = "lock file" },
            new() { FilePath = "yarn.lock", SkipReason = "lock file" },
            new() { FilePath = "icon.png", SkipReason = "binary" },
        };

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, skippedFiles: skipped);

        Assert.IsTrue(md.Contains("3 file(s) excluded"), "Should show skipped count");
        Assert.IsTrue(md.Contains("lock file"), "Should group by skip reason");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Session ID
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void WithSessionId_ShowsFooter()
    {
        var result = MakeResult("APPROVED");
        var sessionId = Guid.NewGuid();

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, sessionId: sessionId);

        Assert.IsTrue(md.Contains($"Session: {sessionId}"), "Should show session ID in footer");
    }

    [TestMethod]
    public void NoSessionId_NoFooter()
    {
        var result = MakeResult("APPROVED");

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result, sessionId: null);

        Assert.IsFalse(md.Contains("Session:"), "Should not show session footer without ID");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Size warning
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void SizeWarning_ShowsBanner()
    {
        var result = MakeResult("APPROVED");

        var md = CodeReviewOrchestrator.BuildSummaryMarkdown(42, result,
            sizeWarning: "This PR touches 50 files and 2000 lines — consider splitting.");

        Assert.IsTrue(md.Contains("PR Size Warning"), "Should show size warning");
        Assert.IsTrue(md.Contains("consider splitting"), "Should include warning text");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static CodeReviewResult MakeResult(string verdict)
    {
        return new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                Verdict = verdict,
                VerdictJustification = $"The code is {verdict.ToLower()}.",
                FilesChanged = 1,
                EditsCount = 1,
                AddsCount = 0,
                DeletesCount = 0,
                Description = "Test review summary."
            },
            FileReviews = new List<FileReview>(),
            InlineComments = new List<InlineComment>(),
        };
    }
}
