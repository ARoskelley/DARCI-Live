using Darci.Engineering;
using Darci.Tools;
using Microsoft.Extensions.Logging;

namespace Darci.Core;

/// <summary>
/// Generates a Bill of Materials markdown file for a completed engineering project.
/// Uses the LLM to produce a structured table based on the goal description and
/// any extracted constraints. BOM generation is best-effort — failure is logged,
/// not thrown.
/// </summary>
public sealed class BomGenerator
{
    private readonly IToolkit _toolkit;
    private readonly ILogger<BomGenerator> _logger;

    public BomGenerator(IToolkit toolkit, ILogger<BomGenerator> logger)
    {
        _toolkit = toolkit;
        _logger = logger;
    }

    public async Task<string> GenerateBomAsync(
        string description,
        EngineeringResult result,
        Dictionary<string, object>? constraints,
        CancellationToken ct)
    {
        var constraintText = constraints?.Count > 0
            ? string.Join("\n", constraints.Select(kv => $"  {kv.Key}: {kv.Value}"))
            : "  No specific constraints extracted.";

        var prompt = $"""
Generate a Bill of Materials (BOM) for the following engineering project.
Format as a markdown table with columns: Part | Description | Quantity | Material | Notes
Include fasteners, structural members, actuators, electronics, and consumables.
Base the BOM on the project description and any extracted constraints.
If a value is unknown, write 'TBD'.

Project: {description}
Engineering score: {result.FinalScore:P0}
Extracted constraints:
{constraintText}
""";

        try
        {
            var bom = await _toolkit.Generate(prompt);
            return $"# Bill of Materials\n\n**Project:** {description}\n\n{bom}\n";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BOM generation failed for '{Description}'", description);
            return $"# Bill of Materials\n\n**Project:** {description}\n\nBOM generation failed: {ex.Message}\n";
        }
    }
}
