using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Tests for internal static helper methods on <see cref="CodeReviewOrchestrator"/>.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class OrchestratorInternalTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  ExtractCodeContext
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ExtractCodeContext_NullContent_ReturnsUnavailable()
    {
        var result = CodeReviewOrchestrator.ExtractCodeContext(null, 1, 5);
        Assert.AreEqual("(file content not available)", result);
    }

    [TestMethod]
    public void ExtractCodeContext_EmptyContent_ReturnsUnavailable()
    {
        var result = CodeReviewOrchestrator.ExtractCodeContext("", 1, 5);
        Assert.AreEqual("(file content not available)", result);
    }

    [TestMethod]
    public void ExtractCodeContext_ValidContent_IncludesContext()
    {
        var content = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"Line {i}"));
        var result = CodeReviewOrchestrator.ExtractCodeContext(content, 15, 15, contextLines: 2);

        Assert.IsTrue(result.Contains("Line 13"), "Should include context before");
        Assert.IsTrue(result.Contains("Line 15"), "Should include the target line");
        Assert.IsTrue(result.Contains("Line 17"), "Should include context after");
        Assert.IsFalse(result.Contains("Line 11"), "Should not include distant lines");
    }

    [TestMethod]
    public void ExtractCodeContext_LineNumbersIncluded()
    {
        var content = "alpha\nbeta\ngamma";
        var result = CodeReviewOrchestrator.ExtractCodeContext(content, 2, 2, contextLines: 0);
        Assert.IsTrue(result.Contains("2"), "Should contain line number 2");
        Assert.IsTrue(result.Contains("beta"), "Should contain line content");
    }

    [TestMethod]
    public void ExtractCodeContext_ClampsToFileBounds()
    {
        var content = "a\nb\nc";
        var result = CodeReviewOrchestrator.ExtractCodeContext(content, 1, 1, contextLines: 100);
        Assert.IsTrue(result.Contains("a"));
        Assert.IsTrue(result.Contains("c"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildThreadReply
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildThreadReply_WithReplies_AcknowledgesConversation()
    {
        var comment = new InlineComment { LeadIn = "Bug", Comment = "Null ref still exists" };
        var thread = new ExistingCommentThread
        {
            Replies = new List<ThreadReply>
            {
                new() { Author = "Dev", Content = "I fixed it" },
            },
        };

        var result = CodeReviewOrchestrator.BuildThreadReply(comment, thread, " _[AI]_");

        Assert.IsTrue(result.Contains("reviewed the changes and the conversation"));
        Assert.IsTrue(result.Contains("**Bug.** Null ref still exists"));
        Assert.IsTrue(result.Contains("_[AI]_"));
    }

    [TestMethod]
    public void BuildThreadReply_NoReplies_FlagsReissue()
    {
        var comment = new InlineComment { LeadIn = "Concern", Comment = "Missing validation" };
        var thread = new ExistingCommentThread { Replies = new List<ThreadReply>() };

        var result = CodeReviewOrchestrator.BuildThreadReply(comment, thread, "");

        Assert.IsTrue(result.Contains("flagged again during re-review"));
        Assert.IsTrue(result.Contains("**Concern.** Missing validation"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MapVerdictToRecommendation
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow("Approved", "Approved")]
    [DataRow("APPROVED", "Approved")]
    [DataRow("Approved With Suggestions", "ApprovedWithSuggestions")]
    [DataRow("Needs Work", "NeedsWork")]
    [DataRow("Rejected", "Rejected")]
    [DataRow("Unknown", "Approved")]       // default fallback
    [DataRow("", "Approved")]              // empty fallback
    public void MapVerdictToRecommendation_MapsCorrectly(string verdict, string expected)
    {
        Assert.AreEqual(expected, CodeReviewOrchestrator.MapVerdictToRecommendation(verdict));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  VoteToLabel
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow(10, "Approved")]
    [DataRow(5, "Approved with suggestions")]
    [DataRow(-5, "Waiting for author")]
    [DataRow(-10, "Rejected")]
    [DataRow(0, "Vote: 0")]
    [DataRow(99, "Vote: 99")]
    public void VoteToLabel_MapsCorrectly(int vote, string expected)
    {
        Assert.AreEqual(expected, CodeReviewOrchestrator.VoteToLabel(vote));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ResolveLineFromSnippet
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ResolveLineFromSnippet_ExactMatch()
    {
        var lines = new[] { "class Foo", "{", "    public void Bar()", "    {", "    }", "}" };
        var result = CodeReviewOrchestrator.ResolveLineFromSnippet("public void Bar()", lines);

        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.Value.start);
        Assert.AreEqual(3, result.Value.end);
    }

    [TestMethod]
    public void ResolveLineFromSnippet_MultiLineSnippet()
    {
        var lines = new[] { "class Foo", "{", "    public void Bar()", "    {", "    }", "}" };
        var result = CodeReviewOrchestrator.ResolveLineFromSnippet("public void Bar()\n    {", lines);

        Assert.IsNotNull(result);
        Assert.AreEqual(3, result.Value.start);
        Assert.AreEqual(4, result.Value.end);
    }

    [TestMethod]
    public void ResolveLineFromSnippet_CaseInsensitiveFallback()
    {
        var lines = new[] { "class Foo", "PUBLIC VOID BAR()" };
        var result = CodeReviewOrchestrator.ResolveLineFromSnippet("public void bar()", lines);

        Assert.IsNotNull(result);
        Assert.AreEqual(2, result.Value.start);
    }

    [TestMethod]
    public void ResolveLineFromSnippet_NotFound_ReturnsNull()
    {
        var lines = new[] { "class Foo", "}" };
        var result = CodeReviewOrchestrator.ResolveLineFromSnippet("public void NotHere()", lines);
        Assert.IsNull(result);
    }

    [TestMethod]
    public void ResolveLineFromSnippet_EmptySnippet_ReturnsNull()
    {
        var lines = new[] { "class Foo" };
        Assert.IsNull(CodeReviewOrchestrator.ResolveLineFromSnippet("", lines));
        Assert.IsNull(CodeReviewOrchestrator.ResolveLineFromSnippet("   ", lines));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ChunkFiles
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ChunkFiles_EvenSplit()
    {
        var files = Enumerable.Range(1, 6).Select(i => new FileChange { FilePath = $"f{i}.cs" }).ToList();
        var chunks = CodeReviewOrchestrator.ChunkFiles(files, 3);

        Assert.AreEqual(2, chunks.Count);
        Assert.AreEqual(3, chunks[0].Count);
        Assert.AreEqual(3, chunks[1].Count);
    }

    [TestMethod]
    public void ChunkFiles_UnevenSplit()
    {
        var files = Enumerable.Range(1, 5).Select(i => new FileChange { FilePath = $"f{i}.cs" }).ToList();
        var chunks = CodeReviewOrchestrator.ChunkFiles(files, 3);

        Assert.AreEqual(2, chunks.Count);
        Assert.AreEqual(3, chunks[0].Count);
        Assert.AreEqual(2, chunks[1].Count);
    }

    [TestMethod]
    public void ChunkFiles_SingleBatch()
    {
        var files = Enumerable.Range(1, 2).Select(i => new FileChange { FilePath = $"f{i}.cs" }).ToList();
        var chunks = CodeReviewOrchestrator.ChunkFiles(files, 10);

        Assert.AreEqual(1, chunks.Count);
        Assert.AreEqual(2, chunks[0].Count);
    }

    [TestMethod]
    public void ChunkFiles_EmptyList()
    {
        var chunks = CodeReviewOrchestrator.ChunkFiles(new List<FileChange>(), 5);
        Assert.AreEqual(0, chunks.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  MergeBatchResults
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void MergeBatchResults_SingleBatch_ReturnsSameInstance()
    {
        var batch = new CodeReviewResult { Summary = new ReviewSummary { Verdict = "Approved" } };
        var result = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { batch }, 5);
        Assert.AreSame(batch, result, "Single batch should return same instance");
    }

    [TestMethod]
    public void MergeBatchResults_MergesInlineComments()
    {
        var b1 = MakeBatchResult("Approved", 10, new[] { "Obs A" });
        b1.InlineComments.Add(new InlineComment { Comment = "c1" });

        var b2 = MakeBatchResult("Approved", 10, new[] { "Obs B" });
        b2.InlineComments.Add(new InlineComment { Comment = "c2" });
        b2.InlineComments.Add(new InlineComment { Comment = "c3" });

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 10);

        Assert.AreEqual(3, merged.InlineComments.Count);
    }

    [TestMethod]
    public void MergeBatchResults_DeduplicatesObservations()
    {
        var b1 = MakeBatchResult("Approved", 10, new[] { "Obs A", "Obs B" });
        var b2 = MakeBatchResult("Approved", 10, new[] { "Obs B", "Obs C" });

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 5);

        Assert.AreEqual(3, merged.Observations.Count, "Obs B should be deduplicated");
    }

    [TestMethod]
    public void MergeBatchResults_WorstVerdictWins()
    {
        var b1 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        var b2 = MakeBatchResult("Needs Work", -5, Array.Empty<string>());

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 5);

        Assert.AreEqual("Needs Work", merged.Summary.Verdict);
        Assert.AreEqual(-5, merged.RecommendedVote);
    }

    [TestMethod]
    public void MergeBatchResults_SumsTokenMetrics()
    {
        var b1 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b1.PromptTokens = 100;
        b1.CompletionTokens = 50;
        b1.TotalTokens = 150;
        b1.AiDurationMs = 1000;

        var b2 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b2.PromptTokens = 200;
        b2.CompletionTokens = 100;
        b2.TotalTokens = 300;
        b2.AiDurationMs = 2000;

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 5);

        Assert.AreEqual(300, merged.PromptTokens);
        Assert.AreEqual(150, merged.CompletionTokens);
        Assert.AreEqual(450, merged.TotalTokens);
        Assert.AreEqual(3000, merged.AiDurationMs);
    }

    [TestMethod]
    public void MergeBatchResults_NullMetrics_Handled()
    {
        var b1 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        // No token metrics set
        var b2 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b2.PromptTokens = 200;

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 5);

        Assert.AreEqual(200, merged.PromptTokens);
    }

    [TestMethod]
    public void MergeBatchResults_CountsFieldsFromSummary()
    {
        var b1 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b1.Summary.EditsCount = 3;
        b1.Summary.AddsCount = 1;
        b1.Summary.DeletesCount = 0;

        var b2 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b2.Summary.EditsCount = 2;
        b2.Summary.AddsCount = 0;
        b2.Summary.DeletesCount = 1;

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 10);

        Assert.AreEqual(5, merged.Summary.EditsCount);
        Assert.AreEqual(1, merged.Summary.AddsCount);
        Assert.AreEqual(1, merged.Summary.DeletesCount);
        Assert.AreEqual(10, merged.Summary.FilesChanged);
    }

    [TestMethod]
    public void MergeBatchResults_MergesAcceptanceCriteria()
    {
        var b1 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b1.AcceptanceCriteriaAnalysis = new AcceptanceCriteriaAnalysis
        {
            Items = new List<AcceptanceCriteriaItem>
            {
                new() { Criterion = "Login form shown", Status = "Addressed", Evidence = "File A" },
            }
        };

        var b2 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b2.AcceptanceCriteriaAnalysis = new AcceptanceCriteriaAnalysis
        {
            Items = new List<AcceptanceCriteriaItem>
            {
                new() { Criterion = "Login form shown", Status = "Not Addressed", Evidence = "File B" },
                new() { Criterion = "Error handling", Status = "Addressed", Evidence = "File B" },
            }
        };

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 5);

        Assert.IsNotNull(merged.AcceptanceCriteriaAnalysis);
        Assert.AreEqual(2, merged.AcceptanceCriteriaAnalysis.Items.Count);

        // Conflicting statuses should downgrade to "Partially Addressed"
        var loginCriterion = merged.AcceptanceCriteriaAnalysis.Items.First(i => i.Criterion == "Login form shown");
        Assert.AreEqual("Partially Addressed", loginCriterion.Status);
    }

    [TestMethod]
    public void MergeBatchResults_FileReviewsMerged()
    {
        var b1 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b1.FileReviews.Add(new FileReview { FilePath = "a.cs" });

        var b2 = MakeBatchResult("Approved", 10, Array.Empty<string>());
        b2.FileReviews.Add(new FileReview { FilePath = "b.cs" });
        b2.FileReviews.Add(new FileReview { FilePath = "c.cs" });

        var merged = CodeReviewOrchestrator.MergeBatchResults(new List<CodeReviewResult> { b1, b2 }, 3);

        Assert.AreEqual(3, merged.FileReviews.Count);
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static CodeReviewResult MakeBatchResult(string verdict, int vote, string[] observations)
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                Verdict = verdict,
                VerdictJustification = $"Justification for {verdict}",
            },
            RecommendedVote = vote,
        };
        foreach (var obs in observations)
            result.Observations.Add(obs);
        return result;
    }
}
