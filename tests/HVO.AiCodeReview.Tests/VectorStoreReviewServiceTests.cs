using System.Text.Json;
using System.Text.Json.Serialization;
using AiCodeReview.Models;
using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for <see cref="VectorStoreReviewService"/> internal helper methods.
/// These test pure logic (extension handling, filename mapping, response parsing)
/// without requiring Azure OpenAI connectivity.
/// </summary>
[TestCategory("Unit")]
[TestClass]
public class VectorStoreReviewServiceTests
{
    // ═══════════════════════════════════════════════════════════════════
    // NeedsRename
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow(".cs", true, DisplayName = "C# files need rename")]
    [DataRow(".csx", true, DisplayName = "C# script files need rename")]
    [DataRow(".vb", true, DisplayName = "VB.NET files need rename")]
    [DataRow(".fs", true, DisplayName = "F# files need rename")]
    [DataRow(".csproj", true, DisplayName = "Project files need rename")]
    [DataRow(".sln", true, DisplayName = "Solution files need rename")]
    [DataRow(".xaml", true, DisplayName = "XAML files need rename")]
    [DataRow(".yml", true, DisplayName = "YAML files need rename")]
    [DataRow(".yaml", true, DisplayName = "YAML files need rename")]
    [DataRow(".sql", true, DisplayName = "SQL files need rename")]
    [DataRow(".dockerfile", true, DisplayName = "Dockerfile ext needs rename")]
    [DataRow(".swift", true, DisplayName = "Swift files need rename")]
    [DataRow(".kt", true, DisplayName = "Kotlin files need rename")]
    [DataRow(".rs", true, DisplayName = "Rust files need rename")]
    public void NeedsRename_UnsupportedExtensions_ReturnsTrue(string ext, bool expected)
    {
        var result = VectorStoreReviewService.NeedsRename($"src/MyFile{ext}");
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    [DataRow(".c")]
    [DataRow(".cpp")]
    [DataRow(".css")]
    [DataRow(".go")]
    [DataRow(".html")]
    [DataRow(".java")]
    [DataRow(".js")]
    [DataRow(".json")]
    [DataRow(".md")]
    [DataRow(".php")]
    [DataRow(".py")]
    [DataRow(".rb")]
    [DataRow(".ts")]
    [DataRow(".txt")]
    [DataRow(".xml")]
    public void NeedsRename_SupportedExtensions_ReturnsFalse(string ext)
    {
        Assert.IsFalse(VectorStoreReviewService.NeedsRename($"src/MyFile{ext}"));
    }

    [TestMethod]
    public void NeedsRename_NoExtension_ReturnsTrue()
    {
        Assert.IsTrue(VectorStoreReviewService.NeedsRename("Dockerfile"));
        Assert.IsTrue(VectorStoreReviewService.NeedsRename("Makefile"));
        Assert.IsTrue(VectorStoreReviewService.NeedsRename(".gitignore"));
    }

    // ═══════════════════════════════════════════════════════════════════
    // GetUploadFilename
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GetUploadFilename_CSharpFile_AppendsTxt()
    {
        var result = VectorStoreReviewService.GetUploadFilename("Services/MyService.cs");
        Assert.AreEqual("Services/MyService.cs.txt", result);
    }

    [TestMethod]
    public void GetUploadFilename_JavaScriptFile_NoChange()
    {
        var result = VectorStoreReviewService.GetUploadFilename("src/app.js");
        Assert.AreEqual("src/app.js", result);
    }

    [TestMethod]
    public void GetUploadFilename_PythonFile_NoChange()
    {
        var result = VectorStoreReviewService.GetUploadFilename("scripts/deploy.py");
        Assert.AreEqual("scripts/deploy.py", result);
    }

    [TestMethod]
    public void GetUploadFilename_TypeScriptFile_NoChange()
    {
        var result = VectorStoreReviewService.GetUploadFilename("src/index.ts");
        Assert.AreEqual("src/index.ts", result);
    }

    [TestMethod]
    public void GetUploadFilename_CsProjFile_AppendsTxt()
    {
        var result = VectorStoreReviewService.GetUploadFilename("MyApp.csproj");
        Assert.AreEqual("MyApp.csproj.txt", result);
    }

    [TestMethod]
    public void GetUploadFilename_NoExtension_AppendsTxt()
    {
        var result = VectorStoreReviewService.GetUploadFilename("Dockerfile");
        Assert.AreEqual("Dockerfile.txt", result);
    }

    [TestMethod]
    public void GetUploadFilename_BackslashPath_NormalizesToForwardSlash()
    {
        var result = VectorStoreReviewService.GetUploadFilename("src\\Models\\User.cs");
        Assert.AreEqual("src/Models/User.cs.txt", result);
    }

    [TestMethod]
    public void GetUploadFilename_LeadingSlash_Stripped()
    {
        var result = VectorStoreReviewService.GetUploadFilename("/src/app.js");
        Assert.AreEqual("src/app.js", result);
    }

    [TestMethod]
    public void GetUploadFilename_JsonFile_NoChange()
    {
        var result = VectorStoreReviewService.GetUploadFilename("appsettings.json");
        Assert.AreEqual("appsettings.json", result);
    }

    [TestMethod]
    public void GetUploadFilename_XmlFile_NoChange()
    {
        var result = VectorStoreReviewService.GetUploadFilename("web.config.xml");
        Assert.AreEqual("web.config.xml", result);
    }

    // ═══════════════════════════════════════════════════════════════════
    // BuildUploadFileMap
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildUploadFileMap_NoCollisions_MatchesGetUploadFilename()
    {
        var paths = new[] { "src/app.js", "Models/User.cs", "Dockerfile" };
        var map = VectorStoreReviewService.BuildUploadFileMap(paths);

        Assert.AreEqual(3, map.Count);
        Assert.AreEqual("src/app.js", map["src/app.js"]);
        Assert.AreEqual("Models/User.cs.txt", map["Models/User.cs"]);
        Assert.AreEqual("Dockerfile.txt", map["Dockerfile"]);
    }

    [TestMethod]
    public void BuildUploadFileMap_Collision_CsAndCsTxt_Disambiguated()
    {
        // src/Foo.cs → would naively become src/Foo.cs.txt
        // src/Foo.cs.txt → stays src/Foo.cs.txt (supported extension)
        // Without disambiguation, both map to "src/Foo.cs.txt"
        var paths = new[] { "src/Foo.cs", "src/Foo.cs.txt" };
        var map = VectorStoreReviewService.BuildUploadFileMap(paths);

        Assert.AreEqual(2, map.Count);
        // The real .txt file keeps its name
        Assert.AreEqual("src/Foo.cs.txt", map["src/Foo.cs.txt"]);
        // The .cs file gets disambiguated
        Assert.AreEqual("src/Foo.cs_1.txt", map["src/Foo.cs"],
            "Colliding file should be disambiguated with _1.txt suffix");
    }

    [TestMethod]
    public void BuildUploadFileMap_Collision_UniqueUploadNames()
    {
        var paths = new[] { "src/Foo.cs", "src/Foo.cs.txt" };
        var map = VectorStoreReviewService.BuildUploadFileMap(paths);

        var uploadNames = map.Values.ToList();
        Assert.AreEqual(uploadNames.Distinct(StringComparer.OrdinalIgnoreCase).Count(), uploadNames.Count,
            "All upload names must be unique");
    }

    [TestMethod]
    public void BuildUploadFileMap_MultipleCollisions_AllDisambiguated()
    {
        // Two different unsupported extensions that would both collide
        // after .txt append — edge case with three files
        var paths = new[] { "config.yml", "config.yml.txt", "data.json" };
        var map = VectorStoreReviewService.BuildUploadFileMap(paths);

        Assert.AreEqual(3, map.Count);
        Assert.AreEqual("config.yml.txt", map["config.yml.txt"]);
        Assert.AreEqual("config.yml_1.txt", map["config.yml"]);
        Assert.AreEqual("data.json", map["data.json"]); // no collision
    }

    [TestMethod]
    public void BuildUploadFileMap_NoUnsupportedExtensions_NoChanges()
    {
        var paths = new[] { "app.js", "style.css", "data.json" };
        var map = VectorStoreReviewService.BuildUploadFileMap(paths);

        Assert.AreEqual("app.js", map["app.js"]);
        Assert.AreEqual("style.css", map["style.css"]);
        Assert.AreEqual("data.json", map["data.json"]);
    }

    // ═══════════════════════════════════════════════════════════════════
    // ParseReviewResponse
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ParseReviewResponse_ValidJson_ReturnsResult()
    {
        var json = BuildValidReviewJson(filesChanged: 3, verdict: "APPROVED");
        var files = CreateFileChanges("a.cs", "b.cs", "c.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.IsNotNull(result);
        Assert.AreEqual("APPROVED", result.Summary.Verdict);
        Assert.AreEqual(3, result.Summary.FilesChanged);
    }

    [TestMethod]
    public void ParseReviewResponse_WrappedInMarkdownFences_ParsesCorrectly()
    {
        var json = "```json\n" + BuildValidReviewJson(filesChanged: 2, verdict: "NEEDS WORK") + "\n```";
        var files = CreateFileChanges("x.cs", "y.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.IsNotNull(result);
        Assert.AreEqual("NEEDS WORK", result.Summary.Verdict);
        Assert.AreEqual(2, result.Summary.FilesChanged);
    }

    [TestMethod]
    public void ParseReviewResponse_WithPreambleText_FindsJson()
    {
        var json = "Here is my analysis:\n\n" + BuildValidReviewJson(filesChanged: 1, verdict: "APPROVED");
        var files = CreateFileChanges("a.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.IsNotNull(result);
        Assert.AreEqual("APPROVED", result.Summary.Verdict);
    }

    [TestMethod]
    public void ParseReviewResponse_WithTrailingText_FindsJson()
    {
        var json = BuildValidReviewJson(filesChanged: 1, verdict: "APPROVED") + "\n\nLet me know if you need more details.";
        var files = CreateFileChanges("a.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.IsNotNull(result);
        Assert.AreEqual("APPROVED", result.Summary.Verdict);
    }

    [TestMethod]
    public void ParseReviewResponse_InvalidJson_ReturnsFallback()
    {
        var files = CreateFileChanges("a.cs");

        var result = VectorStoreReviewService.ParseReviewResponse("This is not JSON at all.", files);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.Summary.FilesChanged);
        Assert.IsTrue(result.Summary.VerdictJustification.Contains("could not be parsed"));
        Assert.IsTrue(result.Observations.Count > 0);
    }

    [TestMethod]
    public void ParseReviewResponse_OverridesFilesChangedCount()
    {
        // JSON says 99 files, but we pass 2 actual files — should use actual count
        var json = BuildValidReviewJson(filesChanged: 99, verdict: "APPROVED");
        var files = CreateFileChanges("a.cs", "b.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.AreEqual(2, result.Summary.FilesChanged);
    }

    [TestMethod]
    public void ParseReviewResponse_WithInlineComments_ParsesComments()
    {
        var json = @"{
            ""summary"": { ""verdict"": ""NEEDS WORK"", ""description"": ""test"" },
            ""inlineComments"": [
                {
                    ""filePath"": ""src/Service.cs.txt"",
                    ""startLine"": 10,
                    ""endLine"": 15,
                    ""comment"": ""Potential null reference"",
                    ""status"": ""active""
                }
            ],
            ""recommendedVote"": 5
        }";
        var files = CreateFileChanges("src/Service.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.AreEqual(1, result.InlineComments.Count);
        Assert.AreEqual("src/Service.cs", result.InlineComments[0].FilePath,
            "Path normalization should strip .txt suffix from uploaded filename");
        Assert.AreEqual(10, result.InlineComments[0].StartLine);
        Assert.AreEqual("active", result.InlineComments[0].Status);
    }

    [TestMethod]
    public void ParseReviewResponse_PlainMarkdownFences_ParsesCorrectly()
    {
        var json = "```\n" + BuildValidReviewJson(filesChanged: 1, verdict: "APPROVED") + "\n```";
        var files = CreateFileChanges("a.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.IsNotNull(result);
        Assert.AreEqual("APPROVED", result.Summary.Verdict);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Path normalization in ParseReviewResponse
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ParseReviewResponse_LeadingSlash_StrippedFromFilePath()
    {
        var json = @"{
            ""summary"": { ""verdict"": ""APPROVED"", ""description"": ""test"" },
            ""inlineComments"": [
                { ""filePath"": ""/src/Foo.cs"", ""startLine"": 1, ""comment"": ""x"", ""status"": ""active"" }
            ],
            ""recommendedVote"": 10
        }";
        var files = CreateFileChanges("src/Foo.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.AreEqual("src/Foo.cs", result.InlineComments[0].FilePath);
    }

    [TestMethod]
    public void ParseReviewResponse_TxtSuffix_MappedBackToOriginalPath()
    {
        var json = @"{
            ""summary"": { ""verdict"": ""APPROVED"", ""description"": ""test"" },
            ""inlineComments"": [
                { ""filePath"": ""Models/User.cs.txt"", ""startLine"": 5, ""comment"": ""y"", ""status"": ""active"" }
            ],
            ""recommendedVote"": 10
        }";
        var files = CreateFileChanges("Models/User.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.AreEqual("Models/User.cs", result.InlineComments[0].FilePath);
    }

    [TestMethod]
    public void ParseReviewResponse_LeadingSlashAndTxtSuffix_BothNormalized()
    {
        var json = @"{
            ""summary"": { ""verdict"": ""APPROVED"", ""description"": ""test"" },
            ""inlineComments"": [
                { ""filePath"": ""/Services/Handler.cs.txt"", ""startLine"": 1, ""comment"": ""z"", ""status"": ""active"" }
            ],
            ""recommendedVote"": 10
        }";
        var files = CreateFileChanges("Services/Handler.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.AreEqual("Services/Handler.cs", result.InlineComments[0].FilePath);
    }

    [TestMethod]
    public void ParseReviewResponse_AlreadyCorrectPath_Unchanged()
    {
        var json = @"{
            ""summary"": { ""verdict"": ""APPROVED"", ""description"": ""test"" },
            ""inlineComments"": [
                { ""filePath"": ""src/app.js"", ""startLine"": 1, ""comment"": ""ok"", ""status"": ""active"" }
            ],
            ""recommendedVote"": 10
        }";
        var files = CreateFileChanges("src/app.js");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.AreEqual("src/app.js", result.InlineComments[0].FilePath);
    }

    [TestMethod]
    public void ParseReviewResponse_NullFilePath_SkippedSafely()
    {
        var json = @"{
            ""summary"": { ""verdict"": ""APPROVED"", ""description"": ""test"" },
            ""inlineComments"": [
                { ""filePath"": null, ""startLine"": 1, ""comment"": ""orphan"", ""status"": ""active"" },
                { ""filePath"": ""src/Valid.cs.txt"", ""startLine"": 2, ""comment"": ""real"", ""status"": ""active"" }
            ],
            ""recommendedVote"": 10
        }";
        var files = CreateFileChanges("src/Valid.cs");

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.AreEqual(2, result.InlineComments.Count);
        Assert.IsNull(result.InlineComments[0].FilePath);
        Assert.AreEqual("src/Valid.cs", result.InlineComments[1].FilePath);
    }

    [TestMethod]
    public void ParseReviewResponse_DuplicateUploadFilenames_DoesNotThrow()
    {
        // Without BuildUploadFileMap (backward compat fallback): both files map to
        // the same upload key via GetUploadFilename. GroupBy+Last prevents the crash.
        var json = @"{
            ""summary"": { ""verdict"": ""APPROVED"", ""description"": ""test"" },
            ""inlineComments"": [
                { ""filePath"": ""src/Foo.cs.txt"", ""startLine"": 1, ""comment"": ""dup"", ""status"": ""active"" }
            ],
            ""recommendedVote"": 10
        }";
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Foo.cs", ChangeType = "edit", ModifiedContent = "// a" },
            new() { FilePath = "src/Foo.cs.txt", ChangeType = "edit", ModifiedContent = "// b" },
        };

        var result = VectorStoreReviewService.ParseReviewResponse(json, files);

        Assert.IsNotNull(result);
        Assert.AreEqual(1, result.InlineComments.Count);
        // Last-wins: src/Foo.cs.txt keeps its own path since .txt is a supported extension
        Assert.AreEqual("src/Foo.cs.txt", result.InlineComments[0].FilePath);
    }

    [TestMethod]
    public void ParseReviewResponse_WithUploadFileMap_DisambiguatedPaths()
    {
        // When BuildUploadFileMap is used, src/Foo.cs maps to src/Foo.cs_1.txt
        // and src/Foo.cs.txt stays as-is. The assistant might return the disambiguated
        // name, which should map back to the original path.
        var files = new List<FileChange>
        {
            new() { FilePath = "src/Foo.cs", ChangeType = "edit", ModifiedContent = "// a" },
            new() { FilePath = "src/Foo.cs.txt", ChangeType = "edit", ModifiedContent = "// b" },
        };

        var uploadFileMap = VectorStoreReviewService.BuildUploadFileMap(
            files.Select(f => f.FilePath));

        var json = @"{
            ""summary"": { ""verdict"": ""APPROVED"", ""description"": ""test"" },
            ""inlineComments"": [
                { ""filePath"": ""src/Foo.cs_1.txt"", ""startLine"": 1, ""comment"": ""for .cs file"", ""status"": ""active"" },
                { ""filePath"": ""src/Foo.cs.txt"", ""startLine"": 5, ""comment"": ""for .txt file"", ""status"": ""active"" }
            ],
            ""recommendedVote"": 10
        }";

        var result = VectorStoreReviewService.ParseReviewResponse(json, files, uploadFileMap);

        Assert.AreEqual(2, result.InlineComments.Count);
        Assert.AreEqual("src/Foo.cs", result.InlineComments[0].FilePath,
            "Disambiguated name src/Foo.cs_1.txt should map back to src/Foo.cs");
        Assert.AreEqual("src/Foo.cs.txt", result.InlineComments[1].FilePath,
            "Real .txt file should keep its original path");
    }

    // ═══════════════════════════════════════════════════════════════════
    // ReviewStrategy enum
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ReviewStrategy_DefaultValues_AreCorrect()
    {
        Assert.AreEqual(0, (int)ReviewStrategy.FileByFile);
        Assert.AreEqual(1, (int)ReviewStrategy.Auto);
        Assert.AreEqual(2, (int)ReviewStrategy.Vector);
    }

    [TestMethod]
    public void ReviewRequest_DefaultStrategy_IsFileByFile()
    {
        var request = new ReviewRequest();
        Assert.AreEqual(ReviewStrategy.FileByFile, request.ReviewStrategy);
    }

    [TestMethod]
    [DataRow("\"FileByFile\"", ReviewStrategy.FileByFile)]
    [DataRow("\"Auto\"", ReviewStrategy.Auto)]
    [DataRow("\"Vector\"", ReviewStrategy.Vector)]
    public void ReviewStrategy_JsonStringDeserialization_Works(string json, ReviewStrategy expected)
    {
        var result = JsonSerializer.Deserialize<ReviewStrategy>(json);
        Assert.AreEqual(expected, result);
    }

    [TestMethod]
    public void ReviewStrategy_JsonRoundTrip_UsesStringNames()
    {
        var json = JsonSerializer.Serialize(ReviewStrategy.Vector);
        Assert.AreEqual("\"Vector\"", json);

        var deserialized = JsonSerializer.Deserialize<ReviewStrategy>(json);
        Assert.AreEqual(ReviewStrategy.Vector, deserialized);
    }

    // ═══════════════════════════════════════════════════════════════════
    // AssistantsSettings defaults
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void AssistantsSettings_DefaultValues()
    {
        var settings = new AssistantsSettings();
        Assert.AreEqual(5, settings.AutoThreshold);
        Assert.AreEqual(1000, settings.PollIntervalMs);
        Assert.AreEqual(120, settings.MaxPollAttempts);
        Assert.AreEqual("2024-05-01-preview", settings.ApiVersion);
    }

    // ═══════════════════════════════════════════════════════════════════
    // Helpers
    // ═══════════════════════════════════════════════════════════════════

    private static string BuildValidReviewJson(int filesChanged, string verdict)
    {
        var obj = new
        {
            summary = new
            {
                filesChanged,
                description = "Test review",
                verdict,
                verdictJustification = "Test justification",
            },
            fileReviews = Array.Empty<object>(),
            inlineComments = Array.Empty<object>(),
            observations = new[] { "All looks good." },
            recommendedVote = verdict == "APPROVED" ? 10 : 5,
        };
        return JsonSerializer.Serialize(obj, new JsonSerializerOptions { WriteIndented = true });
    }

    private static List<FileChange> CreateFileChanges(params string[] paths)
    {
        return paths.Select(p => new FileChange
        {
            FilePath = p,
            ChangeType = "edit",
            ModifiedContent = $"// content of {p}",
        }).ToList();
    }
}
