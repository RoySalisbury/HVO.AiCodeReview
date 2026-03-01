using AiCodeReview.Models;
using Microsoft.Extensions.Options;

namespace AiCodeReview.Services;

/// <summary>
/// Detects production code files that were changed without corresponding test
/// file changes in the same PR. This is a static analysis check based on file
/// name and directory conventions — it does not measure code coverage.
/// </summary>
public class TestCoverageGapDetector
{
    private readonly TestCoverageSettings _settings;
    private readonly ILogger<TestCoverageGapDetector> _logger;

    public TestCoverageGapDetector(
        IOptions<TestCoverageSettings> settings,
        ILogger<TestCoverageGapDetector> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    /// <summary>
    /// Represents a detected test coverage gap.
    /// </summary>
    public record TestCoverageGap(
        string ProductionFile,
        string ChangeType,
        List<string> ExpectedTestFiles);

    /// <summary>
    /// Analyzes the given file changes and returns a list of production files
    /// that lack corresponding test file changes in the same PR.
    /// </summary>
    public List<TestCoverageGap> DetectGaps(List<FileChange> fileChanges)
    {
        if (!_settings.Enabled)
        {
            _logger.LogDebug("Test coverage gap detection is disabled");
            return new List<TestCoverageGap>();
        }

        // Build a set of all changed file paths (normalized, lowercase) for fast lookup
        var changedPaths = new HashSet<string>(
            fileChanges.Select(f => NormalizePath(f.FilePath)),
            StringComparer.OrdinalIgnoreCase);

        var gaps = new List<TestCoverageGap>();

        foreach (var file in fileChanges)
        {
            var normalizedPath = NormalizePath(file.FilePath);

            // Skip non-production files (test files, non-matching extensions)
            if (!IsProductionFile(normalizedPath))
                continue;

            // Skip excluded patterns (models, DTOs, config, etc.)
            if (IsExcluded(normalizedPath))
                continue;

            // Generate expected test file paths based on conventions
            var expectedTestPaths = GenerateExpectedTestPaths(normalizedPath);

            // Check if any expected test path is in the changed set
            var hasMatchingTestChange = expectedTestPaths
                .Any(tp => changedPaths.Contains(tp));

            if (!hasMatchingTestChange)
            {
                gaps.Add(new TestCoverageGap(
                    file.FilePath,
                    file.ChangeType,
                    expectedTestPaths));

                _logger.LogDebug(
                    "Test coverage gap: {File} ({ChangeType}) — no matching test file changes found. Expected: {Expected}",
                    file.FilePath, file.ChangeType,
                    string.Join(", ", expectedTestPaths.Take(3)));
            }
        }

        if (gaps.Count > 0)
        {
            _logger.LogInformation(
                "Test coverage gap detection found {GapCount} production file(s) without corresponding test changes out of {TotalFiles} changed files",
                gaps.Count, fileChanges.Count);
        }
        else
        {
            _logger.LogDebug("No test coverage gaps detected — all production files have corresponding test changes");
        }

        return gaps;
    }

    /// <summary>
    /// Builds a summary observation string suitable for inclusion in the
    /// review summary markdown.
    /// </summary>
    public string? BuildGapSummary(List<TestCoverageGap> gaps)
    {
        if (gaps.Count == 0)
            return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("### :test_tube: Test Coverage Gaps");
        sb.AppendLine();
        sb.AppendLine("The following production files were modified without corresponding test file changes:");
        sb.AppendLine();

        foreach (var gap in gaps)
        {
            var fileName = Path.GetFileName(gap.ProductionFile);
            sb.AppendLine($"- `{gap.ProductionFile}` ({gap.ChangeType})");
        }

        sb.AppendLine();
        sb.AppendLine("> :information_source: This is an informational observation based on file naming conventions. " +
            "It does not affect the review verdict. Existing tests may already cover these changes.");

        return sb.ToString();
    }

    // ═══════════════════════════════════════════════════════════════════
    // Internal helpers
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>
    /// Determines whether the given path is a production file (not a test file,
    /// has a matching extension).
    /// </summary>
    internal bool IsProductionFile(string normalizedPath)
    {
        var ext = Path.GetExtension(normalizedPath);

        // Must have a production file extension
        if (!_settings.ProductionFileExtensions.Any(e =>
                e.Equals(ext, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Must not already be a test file (contains any test file pattern marker)
        var fileName = Path.GetFileNameWithoutExtension(normalizedPath);
        var testMarkers = new[] { "Tests", "Test", "_Tests", "_Test", "Spec" };
        if (testMarkers.Any(m => fileName.EndsWith(m, StringComparison.OrdinalIgnoreCase)))
            return false;

        // Must not be in a test directory
        if (normalizedPath.Contains("/tests/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.Contains("/test/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("tests/", StringComparison.OrdinalIgnoreCase) ||
            normalizedPath.StartsWith("test/", StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    /// <summary>
    /// Checks whether the path matches any exclusion pattern.
    /// </summary>
    internal bool IsExcluded(string normalizedPath)
    {
        return _settings.ExcludedPathPatterns.Any(pattern =>
            normalizedPath.Contains(pattern, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Generates all expected test file paths based on configured conventions.
    /// </summary>
    internal List<string> GenerateExpectedTestPaths(string productionFilePath)
    {
        var results = new List<string>();
        var fileName = Path.GetFileNameWithoutExtension(productionFilePath);
        var directory = Path.GetDirectoryName(productionFilePath)?.Replace('\\', '/') ?? "";

        foreach (var pattern in _settings.TestFilePatterns)
        {
            var testFileName = pattern.Replace("{Name}", fileName);

            // Try with directory mappings
            foreach (var mapping in _settings.DirectoryMappings)
            {
                if (directory.StartsWith(mapping.Key, StringComparison.OrdinalIgnoreCase) ||
                    productionFilePath.StartsWith(mapping.Key, StringComparison.OrdinalIgnoreCase))
                {
                    // Replace the production directory prefix with the test directory prefix
                    // Use the longer of directory/mapping.Key to avoid out-of-range on Substring
                    var prefixLen = Math.Min(mapping.Key.Length, directory.Length);
                    var testDir = directory.Length > 0
                        ? mapping.Value + directory.Substring(prefixLen)
                        : mapping.Value;

                    var testPath = NormalizePath(Path.Combine(testDir, testFileName));
                    results.Add(testPath);
                }
            }

            // Also try same directory (for projects without src/tests convention)
            var sameDir = NormalizePath(Path.Combine(directory, testFileName));
            if (!results.Contains(sameDir, StringComparer.OrdinalIgnoreCase))
                results.Add(sameDir);
        }

        return results;
    }

    /// <summary>
    /// Normalizes a file path: forward slashes, strip leading slash, lowercase-safe.
    /// </summary>
    internal static string NormalizePath(string path) =>
        path.Replace('\\', '/').TrimStart('/');
}
