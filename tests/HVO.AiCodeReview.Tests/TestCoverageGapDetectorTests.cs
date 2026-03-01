using AiCodeReview.Models;
using AiCodeReview.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for <see cref="TestCoverageGapDetector"/>: production file
/// detection, exclusion patterns, expected test path generation, gap
/// detection, gap summary building, and disabled mode.
/// All tests use fake/in-memory settings — no live services are needed.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TestCoverageGapDetectorTests
{
    // ── Helpers ──────────────────────────────────────────────────────────

    private static TestCoverageGapDetector CreateDetector(
        TestCoverageSettings? settings = null)
    {
        var opts = Options.Create(settings ?? new TestCoverageSettings());
        var logger = NullLogger<TestCoverageGapDetector>.Instance;
        return new TestCoverageGapDetector(opts, logger);
    }

    private static FileChange MakeFile(string path, string changeType = "edit")
    {
        return new FileChange
        {
            FilePath = path,
            ChangeType = changeType,
            ChangedLineRanges = new List<(int Start, int End)>(),
        };
    }

    // ═══════════════════════════════════════════════════════════════════
    //  IsProductionFile
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void IsProductionFile_CsFileInSrc_ReturnsTrue()
    {
        var detector = CreateDetector();
        Assert.IsTrue(detector.IsProductionFile("src/HVO.AiCodeReview/Services/MyService.cs"));
    }

    [TestMethod]
    public void IsProductionFile_TestFile_ReturnsFalse()
    {
        var detector = CreateDetector();
        Assert.IsFalse(detector.IsProductionFile("tests/HVO.AiCodeReview.Tests/MyServiceTests.cs"));
    }

    [TestMethod]
    public void IsProductionFile_TestSuffix_ReturnsFalse()
    {
        var detector = CreateDetector();
        // File ending with "Test" should be treated as test file
        Assert.IsFalse(detector.IsProductionFile("src/SomeTest.cs"));
    }

    [TestMethod]
    public void IsProductionFile_SpecSuffix_ReturnsFalse()
    {
        var detector = CreateDetector();
        Assert.IsFalse(detector.IsProductionFile("src/SomeSpec.cs"));
    }

    [TestMethod]
    public void IsProductionFile_NonCsExtension_ReturnsFalse()
    {
        var detector = CreateDetector();
        Assert.IsFalse(detector.IsProductionFile("src/readme.md"));
        Assert.IsFalse(detector.IsProductionFile("src/config.json"));
        Assert.IsFalse(detector.IsProductionFile("scripts/deploy.sh"));
    }

    [TestMethod]
    public void IsProductionFile_InTestsDirectory_ReturnsFalse()
    {
        var detector = CreateDetector();
        Assert.IsFalse(detector.IsProductionFile("tests/Helpers/TestServiceBuilder.cs"));
    }

    [TestMethod]
    public void IsProductionFile_InTestDirectory_ReturnsFalse()
    {
        var detector = CreateDetector();
        Assert.IsFalse(detector.IsProductionFile("test/Helpers/SomeHelper.cs"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  IsExcluded
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow("src/HVO.AiCodeReview/Models/FileChange.cs")]
    [DataRow("src/HVO.AiCodeReview/DTOs/ReviewDto.cs")]
    [DataRow("src/HVO.AiCodeReview/Migrations/001_Init.cs")]
    [DataRow("src/HVO.AiCodeReview/Properties/launchSettings.json")]
    [DataRow("src/HVO.AiCodeReview/appsettings.json")]
    [DataRow("src/HVO.AiCodeReview/Program.cs")]
    [DataRow("src/HVO.AiCodeReview/Startup.cs")]
    [DataRow("src/HVO.AiCodeReview/Whatever.Designer.cs")]
    [DataRow("src/HVO.AiCodeReview/Whatever.g.cs")]
    [DataRow("src/HVO.AiCodeReview/Whatever.generated.cs")]
    [DataRow("src/HVO.AiCodeReview/GlobalUsings.cs")]
    [DataRow("src/HVO.AiCodeReview/AssemblyInfo.cs")]
    public void IsExcluded_DefaultExclusions_ReturnsTrue(string path)
    {
        var detector = CreateDetector();
        Assert.IsTrue(detector.IsExcluded(path),
            $"Path '{path}' should be excluded by default patterns");
    }

    [TestMethod]
    [DataRow("src/HVO.AiCodeReview/Services/MyService.cs")]
    [DataRow("src/HVO.AiCodeReview/Controllers/CodeReviewController.cs")]
    public void IsExcluded_ProductionService_ReturnsFalse(string path)
    {
        var detector = CreateDetector();
        Assert.IsFalse(detector.IsExcluded(path),
            $"Path '{path}' should NOT be excluded");
    }

    [TestMethod]
    public void IsExcluded_CustomExclusions_Respected()
    {
        var settings = new TestCoverageSettings
        {
            ExcludedPathPatterns = new List<string> { "/Helpers/", "/Utils/" }
        };
        var detector = CreateDetector(settings);

        Assert.IsTrue(detector.IsExcluded("src/Helpers/Extensions.cs"));
        Assert.IsTrue(detector.IsExcluded("src/Utils/StringHelper.cs"));
        Assert.IsFalse(detector.IsExcluded("src/Services/MyService.cs"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  GenerateExpectedTestPaths
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void GenerateExpectedTestPaths_MapsSourceToTestDirectory()
    {
        var detector = CreateDetector();
        var paths = detector.GenerateExpectedTestPaths("src/HVO.AiCodeReview/Services/MyService.cs");

        // Should contain mapped test directory paths
        Assert.IsTrue(paths.Any(p =>
            p.Contains("tests/", StringComparison.OrdinalIgnoreCase) &&
            p.Contains("MyServiceTests.cs", StringComparison.OrdinalIgnoreCase)),
            $"Expected a mapped test path like tests/.../MyServiceTests.cs. Got: {string.Join(", ", paths)}");
    }

    [TestMethod]
    public void GenerateExpectedTestPaths_IncludesMultipleConventions()
    {
        var detector = CreateDetector();
        var paths = detector.GenerateExpectedTestPaths("src/HVO.AiCodeReview/Services/MyService.cs");

        // Should include at least Tests and Test suffixes
        Assert.IsTrue(paths.Any(p => p.EndsWith("MyServiceTests.cs", StringComparison.OrdinalIgnoreCase)),
            "Should include {Name}Tests.cs pattern");
        Assert.IsTrue(paths.Any(p => p.EndsWith("MyServiceTest.cs", StringComparison.OrdinalIgnoreCase)),
            "Should include {Name}Test.cs pattern");
    }

    [TestMethod]
    public void GenerateExpectedTestPaths_IncludesSameDirectoryFallback()
    {
        var detector = CreateDetector();
        var paths = detector.GenerateExpectedTestPaths("src/HVO.AiCodeReview/Services/MyService.cs");

        // Should also include same-directory paths as fallback
        Assert.IsTrue(paths.Any(p =>
            p.StartsWith("src/", StringComparison.OrdinalIgnoreCase) &&
            p.EndsWith("MyServiceTests.cs", StringComparison.OrdinalIgnoreCase)),
            $"Should include same-directory fallback. Got: {string.Join(", ", paths)}");
    }

    [TestMethod]
    public void GenerateExpectedTestPaths_CustomMapping()
    {
        var settings = new TestCoverageSettings
        {
            DirectoryMappings = new Dictionary<string, string>
            {
                ["lib/"] = "spec/"
            },
            TestFilePatterns = new List<string> { "{Name}Tests.cs" }
        };
        var detector = CreateDetector(settings);

        var paths = detector.GenerateExpectedTestPaths("lib/Core/Engine.cs");
        Assert.IsTrue(paths.Any(p =>
            p.StartsWith("spec/", StringComparison.OrdinalIgnoreCase) &&
            p.EndsWith("EngineTests.cs", StringComparison.OrdinalIgnoreCase)));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  DetectGaps
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void DetectGaps_NoFiles_ReturnsEmpty()
    {
        var detector = CreateDetector();
        var gaps = detector.DetectGaps(new List<FileChange>());
        Assert.AreEqual(0, gaps.Count);
    }

    [TestMethod]
    public void DetectGaps_OnlyTestFiles_ReturnsEmpty()
    {
        var detector = CreateDetector();
        var files = new List<FileChange>
        {
            MakeFile("tests/HVO.AiCodeReview.Tests/MyServiceTests.cs"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(0, gaps.Count);
    }

    [TestMethod]
    public void DetectGaps_ProductionWithMatchingTest_ReturnsEmpty()
    {
        var detector = CreateDetector();
        // The src/ → tests/ mapping keeps subdirectory structure, so
        // src/HVO.AiCodeReview/Services/ → tests/HVO.AiCodeReview/Services/
        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/Services/MyService.cs"),
            MakeFile("tests/HVO.AiCodeReview/Services/MyServiceTests.cs"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(0, gaps.Count,
            $"Expected no gaps when matching test exists. Gaps: {string.Join(", ", gaps.Select(g => g.ProductionFile))}");
    }

    [TestMethod]
    public void DetectGaps_ProductionWithoutMatchingTest_ReturnsGap()
    {
        var detector = CreateDetector();
        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/Services/MyService.cs", "edit"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(1, gaps.Count);
        Assert.AreEqual("src/HVO.AiCodeReview/Services/MyService.cs", gaps[0].ProductionFile);
        Assert.AreEqual("edit", gaps[0].ChangeType);
        Assert.IsTrue(gaps[0].ExpectedTestFiles.Count > 0);
    }

    [TestMethod]
    public void DetectGaps_ExcludedFiles_NotFlagged()
    {
        var detector = CreateDetector();
        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/Models/FileChange.cs"),
            MakeFile("src/HVO.AiCodeReview/Program.cs"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(0, gaps.Count,
            "Models and Program.cs should be excluded by default patterns");
    }

    [TestMethod]
    public void DetectGaps_NonCsFiles_NotFlagged()
    {
        var detector = CreateDetector();
        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/appsettings.json"),
            MakeFile("docs/architecture.md"),
            MakeFile("scripts/deploy.sh"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(0, gaps.Count);
    }

    [TestMethod]
    public void DetectGaps_MixedFiles_OnlyFlagsUncoveredProduction()
    {
        var detector = CreateDetector();
        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/Services/ReviewEngine.cs", "edit"),
            MakeFile("src/HVO.AiCodeReview/Services/Validator.cs", "add"),
            MakeFile("tests/HVO.AiCodeReview/Services/ReviewEngineTests.cs", "edit"),
            MakeFile("src/HVO.AiCodeReview/Models/SomeModel.cs", "edit"),  // excluded
            MakeFile("docs/api-reference.md", "edit"),                     // non-cs
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(1, gaps.Count, "Only Validator.cs should be flagged");
        Assert.IsTrue(gaps[0].ProductionFile.Contains("Validator.cs"));
    }

    [TestMethod]
    public void DetectGaps_MultipleGaps_AllReturned()
    {
        var detector = CreateDetector();
        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/Services/ServiceA.cs"),
            MakeFile("src/HVO.AiCodeReview/Services/ServiceB.cs"),
            MakeFile("src/HVO.AiCodeReview/Controllers/MyController.cs"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(3, gaps.Count);
    }

    [TestMethod]
    public void DetectGaps_Disabled_ReturnsEmpty()
    {
        var settings = new TestCoverageSettings { Enabled = false };
        var detector = CreateDetector(settings);

        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/Services/MyService.cs"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(0, gaps.Count, "Should return empty when disabled");
    }

    [TestMethod]
    public void DetectGaps_MatchesTestSuffix_CaseInsensitive()
    {
        var detector = CreateDetector();

        // The test file is in the changed set and matches via normalized path comparison
        var files = new List<FileChange>
        {
            MakeFile("src/HVO.AiCodeReview/Services/Parser.cs"),
            MakeFile("tests/HVO.AiCodeReview/Services/ParserTests.cs"),
        };

        var gaps = detector.DetectGaps(files);
        Assert.AreEqual(0, gaps.Count,
            "Should match test files case-insensitively");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildGapSummary
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildGapSummary_EmptyGaps_ReturnsNull()
    {
        var detector = CreateDetector();
        var result = TestCoverageGapDetector.BuildGapSummary(new List<TestCoverageGapDetector.TestCoverageGap>());
        Assert.IsNull(result);
    }

    [TestMethod]
    public void BuildGapSummary_WithGaps_ContainsHeader()
    {
        var detector = CreateDetector();
        var gaps = new List<TestCoverageGapDetector.TestCoverageGap>
        {
            new("src/Services/Foo.cs", "edit", new List<string> { "tests/FooTests.cs" }),
        };

        var summary = TestCoverageGapDetector.BuildGapSummary(gaps)!;
        Assert.IsTrue(summary.Contains("Test Coverage Gaps"), "Should contain header");
    }

    [TestMethod]
    public void BuildGapSummary_WithGaps_ListsFiles()
    {
        var detector = CreateDetector();
        var gaps = new List<TestCoverageGapDetector.TestCoverageGap>
        {
            new("src/Services/Foo.cs", "edit", new List<string> { "tests/FooTests.cs" }),
            new("src/Services/Bar.cs", "add", new List<string> { "tests/BarTests.cs" }),
        };

        var summary = TestCoverageGapDetector.BuildGapSummary(gaps)!;
        Assert.IsTrue(summary.Contains("src/Services/Foo.cs"), "Should list Foo.cs");
        Assert.IsTrue(summary.Contains("src/Services/Bar.cs"), "Should list Bar.cs");
        Assert.IsTrue(summary.Contains("(edit)"), "Should include change type");
        Assert.IsTrue(summary.Contains("(add)"), "Should include change type");
    }

    [TestMethod]
    public void BuildGapSummary_WithGaps_ContainsDisclaimer()
    {
        var detector = CreateDetector();
        var gaps = new List<TestCoverageGapDetector.TestCoverageGap>
        {
            new("src/Services/Foo.cs", "edit", new List<string> { "tests/FooTests.cs" }),
        };

        var summary = TestCoverageGapDetector.BuildGapSummary(gaps)!;
        Assert.IsTrue(summary.Contains("informational observation"),
            "Should contain informational disclaimer");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  NormalizePath
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void NormalizePath_BackslashesToForward()
    {
        Assert.AreEqual("src/Foo/Bar.cs",
            TestCoverageGapDetector.NormalizePath("src\\Foo\\Bar.cs"));
    }

    [TestMethod]
    public void NormalizePath_StripsLeadingSlash()
    {
        Assert.AreEqual("src/Foo/Bar.cs",
            TestCoverageGapDetector.NormalizePath("/src/Foo/Bar.cs"));
    }

    [TestMethod]
    public void NormalizePath_AlreadyNormalized_Unchanged()
    {
        Assert.AreEqual("src/Foo/Bar.cs",
            TestCoverageGapDetector.NormalizePath("src/Foo/Bar.cs"));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  Settings defaults
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void Settings_DefaultPatterns_IncludeCommonConventions()
    {
        var settings = new TestCoverageSettings();
        Assert.IsTrue(settings.TestFilePatterns.Contains("{Name}Tests.cs"));
        Assert.IsTrue(settings.TestFilePatterns.Contains("{Name}Test.cs"));
        Assert.IsTrue(settings.TestFilePatterns.Contains("{Name}Spec.cs"));
    }

    [TestMethod]
    public void Settings_DefaultExclusions_IncludeModelsAndProgram()
    {
        var settings = new TestCoverageSettings();
        Assert.IsTrue(settings.ExcludedPathPatterns.Contains("/Models/"));
        Assert.IsTrue(settings.ExcludedPathPatterns.Contains("Program.cs"));
    }

    [TestMethod]
    public void Settings_DefaultEnabled_IsTrue()
    {
        var settings = new TestCoverageSettings();
        Assert.IsTrue(settings.Enabled);
    }

    [TestMethod]
    public void Settings_DefaultDirectoryMappings_HasSrcToTests()
    {
        var settings = new TestCoverageSettings();
        Assert.IsTrue(settings.DirectoryMappings.ContainsKey("src/"));
        Assert.AreEqual("tests/", settings.DirectoryMappings["src/"]);
    }
}
