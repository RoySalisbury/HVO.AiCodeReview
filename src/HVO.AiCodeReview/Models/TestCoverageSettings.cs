namespace AiCodeReview.Models;

/// <summary>
/// Configuration for test coverage gap detection.
/// Controls whether the reviewer flags production code changes that lack
/// corresponding test file changes in the same PR.
/// This is a static analysis check (file name matching), not a code coverage tool.
/// </summary>
public class TestCoverageSettings
{
    public const string SectionName = "TestCoverage";

    /// <summary>
    /// Whether test coverage gap detection is enabled.
    /// When disabled, no test gap observations are generated.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Convention patterns for test file naming. Each pattern uses {Name} as a
    /// placeholder for the production class name (without extension).
    /// Example: "{Name}Tests.cs" matches FooTests.cs for Foo.cs.
    /// </summary>
    public List<string> TestFilePatterns { get; set; } = new()
    {
        "{Name}Tests.cs",
        "{Name}Test.cs",
        "{Name}_Tests.cs",
        "{Name}_Test.cs",
        "{Name}Spec.cs",
    };

    /// <summary>
    /// Directory convention mappings. Maps production source directories to
    /// their corresponding test directories. Paths are case-insensitive and
    /// use forward slashes.
    /// Example: "src/" → "tests/" means a file in src/Foo/Bar.cs would look
    /// for tests in tests/Foo/BarTests.cs (or similar per TestFilePatterns).
    /// </summary>
    public Dictionary<string, string> DirectoryMappings { get; set; } = new()
    {
        ["src/"] = "tests/",
    };

    /// <summary>
    /// File extensions that are considered production code.
    /// Only files with these extensions will be checked for test coverage gaps.
    /// </summary>
    public List<string> ProductionFileExtensions { get; set; } = new()
    {
        ".cs",
    };

    /// <summary>
    /// Path patterns (case-insensitive contains match) for files that naturally
    /// lack tests and should not be flagged: config, models, DTOs, migrations, etc.
    /// </summary>
    public List<string> ExcludedPathPatterns { get; set; } = new()
    {
        "/Models/",
        "/DTOs/",
        "/Migrations/",
        "/Properties/",
        "appsettings",
        "Program.cs",
        "Startup.cs",
        ".Designer.cs",
        ".g.cs",
        ".generated.cs",
        "GlobalUsings.cs",
        "AssemblyInfo.cs",
    };
}
