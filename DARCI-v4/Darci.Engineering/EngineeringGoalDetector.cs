using Darci.Shared;   // EngineeringGoalSpec lives here
using Microsoft.Extensions.Logging;

namespace Darci.Engineering;

/// <summary>
/// Determines whether a DARCI goal is an engineering task that should be
/// delegated to the EngineeringOrchestrator.
///
/// Detection is keyword-based. Single strong keywords ("bracket", "3d print") are
/// enough; edge cases can later be upgraded to an LLM classifier.
/// </summary>
public class EngineeringGoalDetector
{
    private readonly ILogger<EngineeringGoalDetector> _logger;

    private static readonly string[] EngineeringKeywords =
    {
        "design", "cad", "3d print", "bracket", "housing", "enclosure",
        "part", "mount", "fixture", "assembly", "mechanism", "gear",
        "shaft", "bushing", "socket", "plate", "shell", "fillet",
        "chamfer", "hole", "boss", "rib", "wall thickness", "tolerance",
        "clearance", "interference", "stl", "step file", "mesh",
        "extrude", "printable", "manufacturable", "prosthetic",
        "hydraulic", "exoskeleton", "actuator", "joint",
    };

    public EngineeringGoalDetector(ILogger<EngineeringGoalDetector> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Returns an <see cref="EngineeringGoalSpec"/> if the goal is an engineering task,
    /// or null if it is not.
    /// </summary>
    public EngineeringGoalSpec? Detect(string goalTitle, string? goalDescription = null)
    {
        var text       = $"{goalTitle} {goalDescription}".ToLowerInvariant();
        var matchCount = EngineeringKeywords.Count(kw => text.Contains(kw));

        if (matchCount == 0) return null;

        _logger.LogInformation(
            "Detected engineering goal ({Count} keyword{Plural}): {Title}",
            matchCount, matchCount == 1 ? "" : "s", goalTitle);

        var constraints = ExtractConstraints(text);

        return new EngineeringGoalSpec
        {
            Description = goalTitle,
            Constraints = constraints.Count > 0 ? constraints : null,
            ToolId      = "geometry_workbench",
        };
    }

    /// <summary>
    /// Heuristic extraction of dimensional constraints from natural language.
    /// Examples: "5mm wall" → min_wall constraint, "10mm hole" → hole feature.
    /// </summary>
    private static Dictionary<string, object> ExtractConstraints(string text)
    {
        var constraints = new Dictionary<string, object>();

        var wallMatch = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+(?:\.\d+)?)\s*mm\s*wall");
        if (wallMatch.Success)
        {
            constraints["min_wall"] = new Dictionary<string, object>
            {
                ["type"]     = "printability",
                ["min_wall"] = float.Parse(wallMatch.Groups[1].Value),
            };
        }

        var holeMatch = System.Text.RegularExpressions.Regex.Match(
            text, @"(\d+(?:\.\d+)?)\s*mm\s*hole");
        if (holeMatch.Success)
        {
            constraints["has_hole"] = new Dictionary<string, object>
            {
                ["type"]         = "feature",
                ["feature_type"] = "hole",
            };
        }

        return constraints;
    }
}
