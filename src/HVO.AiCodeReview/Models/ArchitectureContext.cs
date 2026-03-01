namespace AiCodeReview.Models;

/// <summary>
/// Architecture and convention context parsed from <c>.ai-review.yaml</c> or
/// <c>.ai-review.json</c> in the reviewed repository's root.  Injected into AI
/// review prompts so the reviewer understands the project's tech stack,
/// architecture pattern, and directory structure.
/// </summary>
public class ArchitectureContext
{
    /// <summary>
    /// Architecture pattern (e.g., "clean-architecture", "vertical-slice", "mvc",
    /// "hexagonal", "microservices").
    /// </summary>
    public string? Architecture { get; set; }

    /// <summary>
    /// Tech stack declarations (e.g., ["MediatR", "FluentValidation", "EF Core"]).
    /// </summary>
    public List<string> TechStack { get; set; } = [];

    /// <summary>
    /// Project structure hints mapping logical layers to paths
    /// (e.g., "domain" → "src/Domain/", "api" → "src/API/").
    /// </summary>
    public Dictionary<string, string> Structure { get; set; } = [];

    /// <summary>
    /// Glob patterns for files the reviewer should focus on
    /// (e.g., ["src/**/*.cs"]).
    /// </summary>
    public List<string> FocusPaths { get; set; } = [];

    /// <summary>
    /// Formats the architecture context into a human-readable prompt section
    /// suitable for injection into AI review prompts.
    /// Returns <c>null</c> if all fields are empty (nothing to inject).
    /// </summary>
    public string? ToPromptSection()
    {
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(Architecture))
            parts.Add($"- **Architecture**: {Architecture}");

        if (TechStack.Count > 0)
            parts.Add($"- **Tech Stack**: {string.Join(", ", TechStack)}");

        if (Structure.Count > 0)
        {
            var mappings = Structure.Select(kv => $"{kv.Key} → `{kv.Value}`");
            parts.Add($"- **Structure**: {string.Join(", ", mappings)}");
        }

        if (FocusPaths.Count > 0)
            parts.Add($"- **Focus Paths**: {string.Join(", ", FocusPaths.Select(p => $"`{p}`"))}");

        if (parts.Count == 0)
            return null;

        return $"## Repository Architecture Context\n{string.Join("\n", parts)}";
    }
}
