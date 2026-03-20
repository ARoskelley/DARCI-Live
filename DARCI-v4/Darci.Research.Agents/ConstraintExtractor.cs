#nullable enable

using System.Text.Json;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// Extracts structured engineering constraints from free-text research findings.
/// Output is a Dictionary suitable for merging into EngineeringGoalSpec.Constraints.
///
/// Always returns (never throws). Returns empty dict on failure — constraint
/// extraction is best-effort and should not block the engineering pipeline.
/// </summary>
public sealed class ConstraintExtractor
{
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<ConstraintExtractor> _logger;

    public ConstraintExtractor(
        IResearchToolbox toolbox,
        ILogger<ConstraintExtractor> logger)
    {
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<Dictionary<string, object>> ExtractAsync(
        ResearchOutcome research,
        string engineeringGoalDescription,
        CancellationToken ct)
    {
        if (!research.IsSuccess || string.IsNullOrWhiteSpace(research.FinalAnswer))
            return new Dictionary<string, object>();

        var prompt = $$"""
You are an engineering constraint extractor.
Given research findings, extract measurable engineering constraints.
Respond ONLY with a valid JSON object. Use null for unknown values.
Use SI units. Include only values explicitly supported by the research.

Required schema (include all keys, null if unknown):
{
    "max_load_n": <max structural load in Newtons or null>,
    "max_mass_kg": <maximum component mass in kg or null>,
    "mount_location": <body mount location string or null>,
    "clearance_mm": <minimum body clearance in mm or null>,
    "wall_thickness_mm": <minimum wall thickness in mm or null>,
    "material": <recommended material or null>,
    "max_torque_nm": <maximum joint torque in Nm or null>,
    "operating_temp_c": <operating temperature range [min,max] or null>,
    "notes": [<array of important non-numeric constraints as strings>]
}

Engineering goal: {{engineeringGoalDescription}}
Research findings:
{{research.FinalAnswer}}
""";

        try
        {
            var json = (await _toolbox.GenerateAsync(prompt, ct)).Trim();

            // Strip markdown fences if present
            if (json.StartsWith("```"))
            {
                var firstNewline = json.IndexOf('\n');
                json = firstNewline >= 0 ? json[(firstNewline + 1)..] : json;
            }
            if (json.EndsWith("```"))
                json = json[..json.LastIndexOf("```")].TrimEnd();

            var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json.Trim());
            if (parsed is null)
                return new Dictionary<string, object>();

            var result = new Dictionary<string, object>();
            foreach (var (key, val) in parsed)
            {
                if (val.ValueKind != JsonValueKind.Null)
                    result[key] = val.ToString();
            }

            _logger.LogInformation(
                "ConstraintExtractor: extracted {Count} constraints from research",
                result.Count);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ConstraintExtractor: failed to extract constraints");
            return new Dictionary<string, object>();
        }
    }
}
