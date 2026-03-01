using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for the file classification / pre-filter logic that excludes
/// non-reviewable files (submodule refs, lock files, generated files, etc.)
/// from AI review.
/// </summary>
[TestCategory("Unit")]
[TestClass]
public class FileClassificationTests
{
    // ───────────────────────────────────────────────────────────────────
    //  Submodule pointers (40-char SHA hashes)
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void SubmodulePointer_SingleShaHash_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/projects/OneExchange/ExchangePropertyFunction",
            ChangeType = "add",
            ModifiedContent = "ace07c03684ec325b6517a8d11b60defdf1f5723",
        };
        var reason = CodeReviewOrchestrator.ClassifyNonReviewableFile(file);
        Assert.AreEqual("submodule reference", reason);
    }

    [TestMethod]
    public void SubmodulePointer_ShaWithTrailingNewline_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/projects/SomeService",
            ChangeType = "add",
            ModifiedContent = "d142b2a779b1ef4356143b7cc2de50b236f9bef7\n",
        };
        var reason = CodeReviewOrchestrator.ClassifyNonReviewableFile(file);
        Assert.AreEqual("submodule reference", reason);
    }

    [TestMethod]
    public void SubmodulePointer_ShaWithWhitespace_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/projects/AnotherService",
            ChangeType = "edit",
            ModifiedContent = "  a5627bd1634d1830bf50848487d767d8d602424d  \n",
        };
        var reason = CodeReviewOrchestrator.ClassifyNonReviewableFile(file);
        Assert.AreEqual("submodule reference", reason);
    }

    [TestMethod]
    public void SubmodulePointer_MultiLineContent_NotDetected()
    {
        // If there's actual multi-line content, it's not just a submodule pointer
        var file = new FileChange
        {
            FilePath = "/projects/SomeService",
            ChangeType = "add",
            ModifiedContent = "ace07c03684ec325b6517a8d11b60defdf1f5723\nsome other content",
        };
        var reason = CodeReviewOrchestrator.ClassifyNonReviewableFile(file);
        Assert.IsNull(reason, "Multi-line content should not be classified as submodule reference");
    }

    // ───────────────────────────────────────────────────────────────────
    //  Empty files
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void EmptyFile_WhitespaceOnly_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/src/placeholder.txt",
            ChangeType = "add",
            ModifiedContent = "   \n\n  ",
        };
        Assert.AreEqual("empty file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void EmptyFile_TrulyEmpty_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/src/empty.cs",
            ChangeType = "add",
            ModifiedContent = "",
        };
        Assert.AreEqual("empty file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    // ───────────────────────────────────────────────────────────────────
    //  Lock files
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void LockFile_PackageLockJson_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/src/web-app/package-lock.json",
            ChangeType = "edit",
            ModifiedContent = "{ huge lock file content }",
        };
        Assert.AreEqual("lock file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void LockFile_YarnLock_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/yarn.lock",
            ChangeType = "edit",
            ModifiedContent = "# yarn lockfile v1",
        };
        Assert.AreEqual("lock file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void LockFile_ComposerLock_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/composer.lock",
            ChangeType = "edit",
            ModifiedContent = "{}",
        };
        Assert.AreEqual("lock file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void LockFile_PackagesLockJson_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/src/Project/packages.lock.json",
            ChangeType = "edit",
            ModifiedContent = "{}",
        };
        Assert.AreEqual("lock file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    // ───────────────────────────────────────────────────────────────────
    //  Generated files
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void GeneratedFile_DesignerCs_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/src/Forms/MainForm.designer.cs",
            ChangeType = "edit",
            ModifiedContent = "namespace MyApp { partial class MainForm {} }",
        };
        Assert.AreEqual("generated file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void GeneratedFile_MinJs_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/wwwroot/js/app.min.js",
            ChangeType = "edit",
            ModifiedContent = "(function(){})();",
        };
        Assert.AreEqual("generated file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void GeneratedFile_MinCss_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/wwwroot/css/site.min.css",
            ChangeType = "edit",
            ModifiedContent = "body{margin:0}",
        };
        Assert.AreEqual("generated file", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    // ───────────────────────────────────────────────────────────────────
    //  Config markers
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ConfigMarker_Gitkeep_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/src/empty-dir/.gitkeep",
            ChangeType = "add",
            ModifiedContent = "",
        };
        // .gitkeep matches config marker check before empty file content check
        Assert.AreEqual("config marker", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void ConfigMarker_EditorConfig_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/.editorconfig",
            ChangeType = "edit",
            ModifiedContent = "[*.cs]\nindent_size = 4",
        };
        Assert.AreEqual("config marker", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    // ───────────────────────────────────────────────────────────────────
    //  Hash/identifier references
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void HashReference_ShortGuid_Detected()
    {
        var file = new FileChange
        {
            FilePath = "/refs/some-tag",
            ChangeType = "add",
            ModifiedContent = "a1b2c3d4e5f6a1b2c3d4e5f6a1b2",
        };
        Assert.AreEqual("hash/identifier reference", CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    // ───────────────────────────────────────────────────────────────────
    //  Normal reviewable files (should return null)
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void ReviewableFile_CSharpCode_NotFiltered()
    {
        var file = new FileChange
        {
            FilePath = "/src/Services/MyService.cs",
            ChangeType = "edit",
            ModifiedContent = "using System;\nnamespace MyApp { public class MyService { } }",
        };
        Assert.IsNull(CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void ReviewableFile_JavaScript_NotFiltered()
    {
        var file = new FileChange
        {
            FilePath = "/src/app.js",
            ChangeType = "edit",
            ModifiedContent = "const express = require('express');\nconst app = express();",
        };
        Assert.IsNull(CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void ReviewableFile_JsonConfig_NotFiltered()
    {
        var file = new FileChange
        {
            FilePath = "/appsettings.json",
            ChangeType = "edit",
            ModifiedContent = "{\n  \"Logging\": {\n    \"LogLevel\": {\n      \"Default\": \"Information\"\n    }\n  }\n}",
        };
        Assert.IsNull(CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void ReviewableFile_Dockerfile_NotFiltered()
    {
        var file = new FileChange
        {
            FilePath = "/Dockerfile",
            ChangeType = "edit",
            ModifiedContent = "FROM mcr.microsoft.com/dotnet/sdk:8.0\nWORKDIR /app\nCOPY . .",
        };
        Assert.IsNull(CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void ReviewableFile_PackageJson_NotFiltered()
    {
        // Regular package.json (NOT package-lock.json) should be reviewed
        var file = new FileChange
        {
            FilePath = "/package.json",
            ChangeType = "edit",
            ModifiedContent = "{\n  \"name\": \"my-app\",\n  \"version\": \"1.0.0\"\n}",
        };
        Assert.IsNull(CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    [TestMethod]
    public void ReviewableFile_NoContent_NotFiltered()
    {
        // Deleted file with no content available — don't skip, let orchestrator handle
        var file = new FileChange
        {
            FilePath = "/src/old-file.cs",
            ChangeType = "delete",
            ModifiedContent = null,
            OriginalContent = null,
        };
        Assert.IsNull(CodeReviewOrchestrator.ClassifyNonReviewableFile(file));
    }

    // ───────────────────────────────────────────────────────────────────
    //  BuildSummaryMarkdown — skipped files note
    // ───────────────────────────────────────────────────────────────────

    [TestMethod]
    public void BuildSummaryMarkdown_WithSkippedFiles_ShowsNote()
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 5,
                EditsCount = 2,
                AddsCount = 3,
                DeletesCount = 0,
                Description = "Test PR.",
                Verdict = "APPROVED",
                VerdictJustification = "Looks good.",
            },
        };

        var skipped = new List<FileChange>
        {
            new() { FilePath = "/projects/Service1", SkipReason = "submodule reference" },
            new() { FilePath = "/projects/Service2", SkipReason = "submodule reference" },
            new() { FilePath = "/package-lock.json", SkipReason = "lock file" },
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(1234, result, skippedFiles: skipped);
        Assert.IsTrue(markdown.Contains("3 file(s) excluded"), "Should mention skipped file count");
        Assert.IsTrue(markdown.Contains("submodule reference"), "Should mention submodule references");
        Assert.IsTrue(markdown.Contains("lock file"), "Should mention lock files");
    }

    [TestMethod]
    public void BuildSummaryMarkdown_NoSkippedFiles_NoNote()
    {
        var result = new CodeReviewResult
        {
            Summary = new ReviewSummary
            {
                FilesChanged = 3,
                EditsCount = 3,
                AddsCount = 0,
                DeletesCount = 0,
                Description = "Test PR.",
                Verdict = "APPROVED",
                VerdictJustification = "Looks good.",
            },
        };

        var markdown = CodeReviewOrchestrator.BuildSummaryMarkdown(1234, result);
        Assert.IsFalse(markdown.Contains("excluded"), "Should not have exclusion note when no files skipped");
    }
}
