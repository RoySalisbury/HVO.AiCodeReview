using AiCodeReview.Services;

namespace AiCodeReview.Tests;

[TestClass]
public class UnifiedDiffTests
{
    [TestMethod]
    public void ComputeUnifiedDiff_IdenticalContent_ReturnsNoChanges()
    {
        var content = "line1\nline2\nline3\n";
        var result = AzureDevOpsService.ComputeUnifiedDiff(content, content, "/file.cs");
        Assert.AreEqual("(no changes detected)", result);
    }

    [TestMethod]
    public void ComputeUnifiedDiff_SingleLineAdded_ContainsAddedMarker()
    {
        var original = "line1\nline2\nline3\n";
        var modified = "line1\nline2\nnew line\nline3\n";

        var result = AzureDevOpsService.ComputeUnifiedDiff(original, modified, "/file.cs");

        Assert.IsTrue(result.Contains("+new line"), $"Expected '+new line' in:\n{result}");
        Assert.IsTrue(result.Contains("@@"), $"Expected hunk header in:\n{result}");
        Assert.IsTrue(result.Contains("--- a/file.cs"), $"Expected old file header in:\n{result}");
        Assert.IsTrue(result.Contains("+++ b/file.cs"), $"Expected new file header in:\n{result}");
    }

    [TestMethod]
    public void ComputeUnifiedDiff_SingleLineRemoved_ContainsDeleteMarker()
    {
        var original = "line1\nline2\nline3\n";
        var modified = "line1\nline3\n";

        var result = AzureDevOpsService.ComputeUnifiedDiff(original, modified, "/file.cs");

        Assert.IsTrue(result.Contains("-line2"), $"Expected '-line2' in:\n{result}");
    }

    [TestMethod]
    public void ComputeUnifiedDiff_LineModified_ShowsDeleteAndInsert()
    {
        var original = "line1\nold content\nline3\n";
        var modified = "line1\nnew content\nline3\n";

        var result = AzureDevOpsService.ComputeUnifiedDiff(original, modified, "/file.cs");

        Assert.IsTrue(result.Contains("-old content"), $"Expected '-old content' in:\n{result}");
        Assert.IsTrue(result.Contains("+new content"), $"Expected '+new content' in:\n{result}");
    }

    [TestMethod]
    public void ComputeUnifiedDiff_MultipleHunks_ProducesMultipleHeaders()
    {
        // Two changes far apart should produce two @@ hunks
        var originalLines = new string[30];
        var modifiedLines = new string[30];
        for (int i = 0; i < 30; i++)
        {
            originalLines[i] = $"line{i + 1}";
            modifiedLines[i] = $"line{i + 1}";
        }
        modifiedLines[2] = "CHANGED_NEAR_TOP";    // change at line 3
        modifiedLines[27] = "CHANGED_NEAR_BOTTOM"; // change at line 28

        var original = string.Join("\n", originalLines);
        var modified = string.Join("\n", modifiedLines);

        var result = AzureDevOpsService.ComputeUnifiedDiff(original, modified, "/file.cs");

        var hunkCount = result.Split("@@").Length / 2; // each @@ header has opening @@
        Assert.IsTrue(hunkCount >= 2, $"Expected at least 2 hunks, got {hunkCount}. Diff:\n{result}");
        Assert.IsTrue(result.Contains("+CHANGED_NEAR_TOP"));
        Assert.IsTrue(result.Contains("+CHANGED_NEAR_BOTTOM"));
    }

    [TestMethod]
    public void ComputeUnifiedDiff_NewFile_AllLinesAdded()
    {
        var original = "";
        var modified = "line1\nline2\nline3\n";

        var result = AzureDevOpsService.ComputeUnifiedDiff(original, modified, "/file.cs");

        Assert.IsTrue(result.Contains("+line1"), $"Expected '+line1' in:\n{result}");
        Assert.IsTrue(result.Contains("+line2"), $"Expected '+line2' in:\n{result}");
        Assert.IsTrue(result.Contains("+line3"), $"Expected '+line3' in:\n{result}");
    }

    [TestMethod]
    public void ParseChangedLineRanges_ExtractsHunkRanges()
    {
        var diff = "@@ -1,5 +1,6 @@\n context\n-old\n+new\n+added\n context\n context\n@@ -20,3 +21,4 @@\n context\n+another add\n context";

        var ranges = AzureDevOpsService.ParseChangedLineRanges(diff);

        Assert.AreEqual(2, ranges.Count, "Expected 2 hunk ranges");
        Assert.AreEqual((1, 6), ranges[0], "First hunk should be lines 1-6");
        Assert.AreEqual((21, 24), ranges[1], "Second hunk should be lines 21-24");
    }

    [TestMethod]
    public void ParseChangedLineRanges_EmptyDiff_ReturnsEmpty()
    {
        var ranges = AzureDevOpsService.ParseChangedLineRanges("");
        Assert.AreEqual(0, ranges.Count);
    }

    [TestMethod]
    public void ParseChangedLineRanges_RoundTripsWithComputeUnifiedDiff()
    {
        var original = "line1\nline2\nline3\nline4\nline5\nline6\nline7\nline8\nline9\nline10\n";
        var modified = "line1\nline2\nCHANGED\nline4\nline5\nline6\nline7\nline8\nline9\nline10\n";

        var diff = AzureDevOpsService.ComputeUnifiedDiff(original, modified, "/test.cs");
        var ranges = AzureDevOpsService.ParseChangedLineRanges(diff);

        // The change is at line 3 in the modified file
        Assert.IsTrue(ranges.Count >= 1, $"Expected at least 1 range, got {ranges.Count}");
        // The changed line (3) should be within one of the ranges
        Assert.IsTrue(ranges.Any(r => r.Start <= 3 && r.End >= 3),
            $"Line 3 should be in a changed range. Ranges: {string.Join(", ", ranges.Select(r => $"{r.Start}-{r.End}"))}");
    }
}
