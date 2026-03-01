using AiCodeReview.Services;

namespace AiCodeReview.Tests;

/// <summary>
/// Unit tests for internal/static methods of <see cref="AzureDevOpsService"/>.
/// Covers: StripHtml, StatusToInt, IsBinaryExtension, BuildThreadProperties,
/// ComputeUnifiedDiff (large-file fallback), ParseChangedLineRanges.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AzureDevOpsServiceInternalTests
{
    // ═══════════════════════════════════════════════════════════════════
    //  StripHtml
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void StripHtml_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, AzureDevOpsService.StripHtml(null!));
        Assert.AreEqual(string.Empty, AzureDevOpsService.StripHtml(""));
        Assert.AreEqual(string.Empty, AzureDevOpsService.StripHtml("   "));
    }

    [TestMethod]
    public void StripHtml_PlainText_ReturnsUnchanged()
    {
        Assert.AreEqual("Hello world", AzureDevOpsService.StripHtml("Hello world"));
    }

    [TestMethod]
    public void StripHtml_BrTags_ReplacedWithNewlines()
    {
        var input = "Line 1<br/>Line 2<BR>Line 3<br />Line 4";
        var result = AzureDevOpsService.StripHtml(input);
        Assert.IsTrue(result.Contains("Line 1\nLine 2"), "Should replace <br/> with newline");
        Assert.IsTrue(result.Contains("Line 3"), "Should handle <br />");
    }

    [TestMethod]
    public void StripHtml_ListItems_ConvertedToBullets()
    {
        var input = "<ul><li>First</li><li>Second</li></ul>";
        var result = AzureDevOpsService.StripHtml(input);
        Assert.IsTrue(result.Contains("- First"), "Should convert <li> to bullet");
        Assert.IsTrue(result.Contains("- Second"));
    }

    [TestMethod]
    public void StripHtml_ParagraphTags_AddNewlines()
    {
        var input = "<p>Paragraph 1</p><p>Paragraph 2</p>";
        var result = AzureDevOpsService.StripHtml(input);
        Assert.IsTrue(result.Contains("Paragraph 1\n"), "Should add newline after </p>");
        Assert.IsTrue(result.Contains("Paragraph 2"));
    }

    [TestMethod]
    public void StripHtml_DivTags_AddNewlines()
    {
        var input = "<div>Block 1</div><div>Block 2</div>";
        var result = AzureDevOpsService.StripHtml(input);
        Assert.IsTrue(result.Contains("Block 1\n"));
        Assert.IsTrue(result.Contains("Block 2"));
    }

    [TestMethod]
    public void StripHtml_MixedTags_AllStripped()
    {
        var input = "<h1>Title</h1><p>Body with <strong>bold</strong> and <a href='#'>link</a>.</p>";
        var result = AzureDevOpsService.StripHtml(input);
        Assert.IsFalse(result.Contains("<"), "Should not contain any HTML tags");
        Assert.IsTrue(result.Contains("Title"));
        Assert.IsTrue(result.Contains("bold"));
        Assert.IsTrue(result.Contains("link"));
    }

    [TestMethod]
    public void StripHtml_HtmlEntities_Decoded()
    {
        var input = "A &amp; B &lt; C &gt; D &quot;quoted&quot;";
        var result = AzureDevOpsService.StripHtml(input);
        Assert.IsTrue(result.Contains("A & B"), "Should decode &amp;");
        Assert.IsTrue(result.Contains("< C >"), "Should decode &lt; and &gt;");
        Assert.IsTrue(result.Contains("\"quoted\""), "Should decode &quot;");
    }

    [TestMethod]
    public void StripHtml_MultipleBlankLines_Collapsed()
    {
        var input = "Line 1<br/><br/><br/><br/><br/>Line 2";
        var result = AzureDevOpsService.StripHtml(input);
        // Multiple newlines should be collapsed to at most 2
        Assert.IsFalse(result.Contains("\n\n\n"), "Should not have 3+ consecutive newlines");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  StatusToInt
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow("active", 1)]
    [DataRow("Active", 1)]
    [DataRow("ACTIVE", 1)]
    [DataRow("fixed", 2)]
    [DataRow("wontfix", 3)]
    [DataRow("closed", 4)]
    [DataRow("bydesign", 5)]
    [DataRow("pending", 6)]
    [DataRow("unknown", 4)]   // default
    [DataRow("", 4)]          // default for empty
    public void StatusToInt_MapsCorrectly(string status, int expected)
    {
        Assert.AreEqual(expected, AzureDevOpsService.StatusToInt(status));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  IsBinaryExtension
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    [DataRow(".png", true)]
    [DataRow(".jpg", true)]
    [DataRow(".gif", true)]
    [DataRow(".ico", true)]
    [DataRow(".svg", true)]
    [DataRow(".webp", true)]
    [DataRow(".tif", true)]
    [DataRow(".woff", true)]
    [DataRow(".woff2", true)]
    [DataRow(".ttf", true)]
    [DataRow(".zip", true)]
    [DataRow(".gz", true)]
    [DataRow(".tar", true)]
    [DataRow(".dll", true)]
    [DataRow(".exe", true)]
    [DataRow(".pdb", true)]
    [DataRow(".so", true)]
    [DataRow(".jar", true)]
    [DataRow(".nupkg", true)]
    [DataRow(".wasm", true)]
    [DataRow(".pdf", true)]
    [DataRow(".doc", true)]
    [DataRow(".xlsx", true)]
    [DataRow(".mp3", true)]
    [DataRow(".mp4", true)]
    [DataRow(".db", true)]
    [DataRow(".snk", true)]
    [DataRow(".pfx", true)]
    [DataRow(".psd", true)]
    [DataRow(".cs", false)]
    [DataRow(".ts", false)]
    [DataRow(".json", false)]
    [DataRow(".md", false)]
    [DataRow(".xml", false)]
    [DataRow(".yaml", false)]
    [DataRow(".csproj", false)]
    public void IsBinaryExtension_ClassifiesCorrectly(string ext, bool expected)
    {
        Assert.AreEqual(expected, AzureDevOpsService.IsBinaryExtension(ext));
    }

    // ═══════════════════════════════════════════════════════════════════
    //  BuildThreadProperties
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void BuildThreadProperties_ContainsUniqueIdEntry()
    {
        var props = AzureDevOpsService.BuildThreadProperties();

        Assert.IsTrue(props.ContainsKey("Microsoft.TeamFoundation.Discussion.UniqueID"));
        var inner = props["Microsoft.TeamFoundation.Discussion.UniqueID"];
        Assert.AreEqual("System.String", inner["$type"]);
        Assert.IsTrue(Guid.TryParse(inner["$value"], out _), "Should be a valid GUID");
    }

    [TestMethod]
    public void BuildThreadProperties_TwoCallsProduceDifferentGuids()
    {
        var props1 = AzureDevOpsService.BuildThreadProperties();
        var props2 = AzureDevOpsService.BuildThreadProperties();

        Assert.AreNotEqual(
            props1["Microsoft.TeamFoundation.Discussion.UniqueID"]["$value"],
            props2["Microsoft.TeamFoundation.Discussion.UniqueID"]["$value"],
            "Each call should generate a unique GUID");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ComputeUnifiedDiff — large file fallback
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ComputeUnifiedDiff_LargeFile_UsesSimpleFallback()
    {
        // Create inputs that trigger the (long)m * n > 25_000_000 fallback
        // 5001 lines × 5001 lines = ~25M > threshold
        var lines = 5001;
        var oldLines = Enumerable.Range(1, lines).Select(i => $"old line {i}").ToArray();
        var newLines = Enumerable.Range(1, lines).Select(i => $"new line {i}").ToArray();
        // Change one line to force a diff
        newLines[2500] = "CHANGED line 2501";

        var original = string.Join("\n", oldLines);
        var modified = string.Join("\n", newLines);

        var diff = AzureDevOpsService.ComputeUnifiedDiff(original, modified, "large.cs");

        Assert.IsTrue(diff.Contains("---"), "Should produce unified diff headers");
        Assert.IsTrue(diff.Contains("+++"), "Should produce unified diff headers");
        Assert.IsTrue(diff.Contains("CHANGED"), "Should show the changed line");
    }

    [TestMethod]
    public void ComputeUnifiedDiff_EmptyOriginal_ShowsAllAdded()
    {
        var diff = AzureDevOpsService.ComputeUnifiedDiff(
            "", "line 1\nline 2\nline 3", "new.cs");
        Assert.IsTrue(diff.Contains("+line 1"), "Should show added lines");
    }

    [TestMethod]
    public void ComputeUnifiedDiff_EmptyModified_ShowsAllDeleted()
    {
        var diff = AzureDevOpsService.ComputeUnifiedDiff(
            "line 1\nline 2\nline 3", "", "old.cs");
        Assert.IsTrue(diff.Contains("-line 1"), "Should show deleted lines");
    }

    [TestMethod]
    public void ComputeUnifiedDiff_Identical_ReturnsNoChangesMessage()
    {
        var content = "same\ncontent\nhere";
        var diff = AzureDevOpsService.ComputeUnifiedDiff(content, content, "same.cs");
        Assert.IsTrue(diff.Contains("no changes"), $"Identical files should indicate no changes. Got: {diff}");
    }

    [TestMethod]
    public void ComputeUnifiedDiff_MultipleHunks_GroupedCorrectly()
    {
        // Create a file with changes far apart to produce multiple hunks
        var oldLines = Enumerable.Range(1, 50).Select(i => $"line {i}").ToList();
        var newLines = new List<string>(oldLines);
        newLines[5] = "CHANGED-5";   // Near top
        newLines[45] = "CHANGED-45"; // Near bottom

        var diff = AzureDevOpsService.ComputeUnifiedDiff(
            string.Join("\n", oldLines),
            string.Join("\n", newLines),
            "multi.cs");

        Assert.IsTrue(diff.Contains("CHANGED-5"));
        Assert.IsTrue(diff.Contains("CHANGED-45"));
        // Should have multiple @@ markers for separate hunks
        var hunkCount = diff.Split("@@").Length - 1;
        Assert.IsTrue(hunkCount >= 2, $"Should have at least 2 hunks, got {hunkCount / 2}");
    }

    // ═══════════════════════════════════════════════════════════════════
    //  ParseChangedLineRanges — additional coverage
    // ═══════════════════════════════════════════════════════════════════

    [TestMethod]
    public void ParseChangedLineRanges_MultipleHunks_ReturnsAllRanges()
    {
        var diff = "@@ -1,3 +1,4 @@\n context\n+added\n context\n@@ -10,3 +11,5 @@\n context\n+add1\n+add2\n context";
        var ranges = AzureDevOpsService.ParseChangedLineRanges(diff);
        Assert.IsTrue(ranges.Count >= 2, "Should parse ranges from multiple hunks");
    }

    [TestMethod]
    public void ParseChangedLineRanges_NoHunks_ReturnsEmpty()
    {
        var ranges = AzureDevOpsService.ParseChangedLineRanges("just plain text");
        Assert.AreEqual(0, ranges.Count);
    }

    [TestMethod]
    public void ParseChangedLineRanges_Empty_ReturnsEmpty()
    {
        var ranges = AzureDevOpsService.ParseChangedLineRanges("");
        Assert.AreEqual(0, ranges.Count);
    }
}
