using Microsoft.Extensions.Logging;

namespace Darci.Core;

/// <summary>
/// Determines whether a DARCI goal is a coding task that should be routed to the coding node.
/// Keyword-based, mirroring <see cref="Darci.Engineering.EngineeringGoalDetector"/> — the same
/// "cognition classifies, then routes to a node" pattern, kept generic so other node detectors slot in.
/// Returns the coding intent (the goal text) or null if it isn't a coding task.
/// </summary>
public sealed class CodingGoalDetector
{
    private readonly ILogger<CodingGoalDetector> _logger;

    // Distinct from the engineering keyword set to avoid cross-classification ("design" is engineering).
    private static readonly string[] CodingKeywords =
    {
        "implement", "code", "coding", "function", "method", "class ", "refactor",
        "unit test", "unit-test", "bug", "debug", "compile", "build error", "algorithm",
        "api endpoint", "endpoint", "script", "module", "library", "patch", "regression",
        ".cs", ".py", ".ts", ".js", "csproj", "pytest", "dotnet",
    };

    public CodingGoalDetector(ILogger<CodingGoalDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>Returns the coding intent if the goal is a coding task, or null otherwise.</summary>
    public string? Detect(string goalTitle, string? goalDescription = null)
    {
        var text = $"{goalTitle} {goalDescription}".ToLowerInvariant();
        var matchCount = CodingKeywords.Count(kw => text.Contains(kw));
        if (matchCount == 0) return null;

        _logger.LogInformation(
            "Detected coding goal ({Count} keyword{Plural}): {Title}",
            matchCount, matchCount == 1 ? "" : "s", goalTitle);

        return string.IsNullOrWhiteSpace(goalDescription) ? goalTitle : $"{goalTitle} — {goalDescription}";
    }
}
