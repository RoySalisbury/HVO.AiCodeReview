using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for thread management: semantic deduplication, line overlap detection,
/// reply building, and the ExistingCommentThread model extensions.
/// </summary>
[TestClass]
public class ThreadManagementTests
{
    // ───────────────────────────────────────────────────────────────────
    //  LinesOverlap
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void LinesOverlap_ExactSameRange_ReturnsTrue()
    {
        Assert.IsTrue(CodeReviewOrchestrator.LinesOverlap(10, 15, 10, 15));
    }

    [TestMethod]
    public void LinesOverlap_Overlapping_ReturnsTrue()
    {
        Assert.IsTrue(CodeReviewOrchestrator.LinesOverlap(10, 20, 15, 25));
    }

    [TestMethod]
    public void LinesOverlap_Adjacent_ReturnsTrue()
    {
        // Lines 10-15 and 16-20 → adjacent, within tolerance=5
        Assert.IsTrue(CodeReviewOrchestrator.LinesOverlap(10, 15, 16, 20));
    }

    [TestMethod]
    public void LinesOverlap_WithinTolerance_ReturnsTrue()
    {
        // Lines 10-15 and 20-25 → gap of 5, within default tolerance=5
        Assert.IsTrue(CodeReviewOrchestrator.LinesOverlap(10, 15, 20, 25));
    }

    [TestMethod]
    public void LinesOverlap_BeyondTolerance_ReturnsFalse()
    {
        // Lines 10-15 and 25-30 → gap of 10, beyond default tolerance=5
        Assert.IsFalse(CodeReviewOrchestrator.LinesOverlap(10, 15, 25, 30));
    }

    [TestMethod]
    public void LinesOverlap_Contained_ReturnsTrue()
    {
        Assert.IsTrue(CodeReviewOrchestrator.LinesOverlap(10, 30, 15, 20));
    }

    [TestMethod]
    public void LinesOverlap_CustomTolerance_Respected()
    {
        // Gap of 10, tolerance=3 → should NOT overlap
        Assert.IsFalse(CodeReviewOrchestrator.LinesOverlap(10, 15, 25, 30, tolerance: 3));
        // Gap of 10, tolerance=15 → should overlap
        Assert.IsTrue(CodeReviewOrchestrator.LinesOverlap(10, 15, 25, 30, tolerance: 15));
    }

    // ───────────────────────────────────────────────────────────────────
    //  ExistingCommentThread model extensions
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ExistingCommentThread_LeadIn_DefaultEmpty()
    {
        var thread = new ExistingCommentThread();
        Assert.AreEqual("", thread.LeadIn);
    }

    [TestMethod]
    public void ExistingCommentThread_Replies_DefaultEmpty()
    {
        var thread = new ExistingCommentThread();
        Assert.IsNotNull(thread.Replies);
        Assert.AreEqual(0, thread.Replies.Count);
    }

    [TestMethod]
    public void ExistingCommentThread_WithReplies_CapturesAll()
    {
        var thread = new ExistingCommentThread
        {
            ThreadId = 42,
            FilePath = "/src/foo.cs",
            StartLine = 10,
            EndLine = 15,
            Content = "**Bug.** Missing null check.",
            LeadIn = "Bug",
            Replies = new List<ThreadReply>
            {
                new()
                {
                    Author = "Dev User",
                    Content = "Fixed in latest commit.",
                    CreatedDateUtc = new DateTime(2026, 2, 27, 10, 0, 0, DateTimeKind.Utc),
                },
                new()
                {
                    Author = "Reviewer",
                    Content = "Confirmed, looks good now.",
                    CreatedDateUtc = new DateTime(2026, 2, 27, 11, 0, 0, DateTimeKind.Utc),
                },
            },
        };

        Assert.AreEqual(2, thread.Replies.Count);
        Assert.AreEqual("Dev User", thread.Replies[0].Author);
        Assert.AreEqual("Reviewer", thread.Replies[1].Author);
    }

    // ───────────────────────────────────────────────────────────────────
    //  ThreadVerificationCandidate — AuthorReplies
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ThreadVerificationCandidate_AuthorReplies_DefaultEmpty()
    {
        var candidate = new ThreadVerificationCandidate();
        Assert.IsNotNull(candidate.AuthorReplies);
        Assert.AreEqual(0, candidate.AuthorReplies.Count);
    }

    [TestMethod]
    public void ThreadVerificationCandidate_AuthorReplies_Populated()
    {
        var candidate = new ThreadVerificationCandidate
        {
            ThreadId = 1,
            FilePath = "/src/bar.cs",
            OriginalComment = "**Concern.** Error handling is missing.",
            AuthorReplies = new List<ThreadReply>
            {
                new() { Author = "Author", Content = "Added try-catch block." },
            },
        };

        Assert.AreEqual(1, candidate.AuthorReplies.Count);
        Assert.AreEqual("Added try-catch block.", candidate.AuthorReplies[0].Content);
    }

    // ───────────────────────────────────────────────────────────────────
    //  ThreadReply model
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ThreadReply_Properties_SetCorrectly()
    {
        var reply = new ThreadReply
        {
            Author = "John Doe",
            Content = "I've addressed this in the latest push.",
            CreatedDateUtc = new DateTime(2026, 2, 27, 12, 0, 0, DateTimeKind.Utc),
        };

        Assert.AreEqual("John Doe", reply.Author);
        Assert.AreEqual("I've addressed this in the latest push.", reply.Content);
        Assert.AreEqual(new DateTime(2026, 2, 27, 12, 0, 0, DateTimeKind.Utc), reply.CreatedDateUtc);
    }

    // ───────────────────────────────────────────────────────────────────
    //  Semantic dedup scenario tests (via LinesOverlap + LeadIn matching)
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void SemanticDedup_SameFileSeverityOverlappingLines_Matches()
    {
        // Simulates the semantic dedup logic from the orchestrator
        var existingThread = new ExistingCommentThread
        {
            ThreadId = 100,
            FilePath = "/src/service.cs",
            StartLine = 10,
            EndLine = 15,
            LeadIn = "Bug",
            IsAiGenerated = true,
            Content = "**Bug.** Missing null check on input parameter.",
        };

        var newComment = new InlineComment
        {
            FilePath = "/src/service.cs",
            StartLine = 12,
            EndLine = 18,
            LeadIn = "Bug",
            Comment = "Input parameter should be validated before use.",
        };

        // Check semantic match criteria
        bool fileMatch = string.Equals(existingThread.FilePath, newComment.FilePath, StringComparison.OrdinalIgnoreCase);
        bool leadInMatch = string.Equals(existingThread.LeadIn, newComment.LeadIn, StringComparison.OrdinalIgnoreCase);
        bool linesMatch = CodeReviewOrchestrator.LinesOverlap(
            existingThread.StartLine, existingThread.EndLine, newComment.StartLine, newComment.EndLine);

        Assert.IsTrue(fileMatch, "File should match");
        Assert.IsTrue(leadInMatch, "LeadIn severity should match");
        Assert.IsTrue(linesMatch, "Lines should overlap");
    }

    [TestMethod]
    public void SemanticDedup_DifferentSeverity_DoesNotMatch()
    {
        var existingThread = new ExistingCommentThread
        {
            FilePath = "/src/service.cs",
            StartLine = 10,
            EndLine = 15,
            LeadIn = "Suggestion",
            IsAiGenerated = true,
        };

        var newComment = new InlineComment
        {
            FilePath = "/src/service.cs",
            StartLine = 10,
            EndLine = 15,
            LeadIn = "Bug",
        };

        bool leadInMatch = string.Equals(existingThread.LeadIn, newComment.LeadIn, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(leadInMatch, "Different severity should NOT match");
    }

    [TestMethod]
    public void SemanticDedup_DifferentFile_DoesNotMatch()
    {
        var existingThread = new ExistingCommentThread
        {
            FilePath = "/src/service.cs",
            StartLine = 10,
            EndLine = 15,
            LeadIn = "Bug",
            IsAiGenerated = true,
        };

        var newComment = new InlineComment
        {
            FilePath = "/src/controller.cs",
            StartLine = 10,
            EndLine = 15,
            LeadIn = "Bug",
        };

        bool fileMatch = string.Equals(existingThread.FilePath, newComment.FilePath, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(fileMatch, "Different file should NOT match");
    }

    [TestMethod]
    public void SemanticDedup_NonOverlappingLines_DoesNotMatch()
    {
        var existingThread = new ExistingCommentThread
        {
            FilePath = "/src/service.cs",
            StartLine = 10,
            EndLine = 15,
            LeadIn = "Bug",
            IsAiGenerated = true,
        };

        var newComment = new InlineComment
        {
            FilePath = "/src/service.cs",
            StartLine = 50,
            EndLine = 55,
            LeadIn = "Bug",
        };

        bool linesMatch = CodeReviewOrchestrator.LinesOverlap(
            existingThread.StartLine, existingThread.EndLine, newComment.StartLine, newComment.EndLine);
        Assert.IsFalse(linesMatch, "Non-overlapping lines on different parts of file should NOT match");
    }
}
