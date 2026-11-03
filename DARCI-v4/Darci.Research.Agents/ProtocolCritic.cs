#nullable enable

using System.Text;
using System.Text.Json;
using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>The protocol critic's falsification of a validation DESIGN (not its verdict): the failure modes
/// the pre-registered protocol does NOT exercise. Attached to the authorization proposal so the human
/// approves (or hardens) the test plan — far harder to sycophantically rubber-stamp than a conclusion.</summary>
public sealed record ProtocolCritique(
    IReadOnlyList<string> UnexercisedFailureModes,
    bool Adequate,
    string Summary);

/// <summary>
/// Falsifies a validation PROTOCOL before authorization (§14b): "what failure mode does this protocol NOT
/// exercise?" A separate agent from the generator/coordinator — the anti-validation-theater guard.
/// </summary>
public interface IProtocolCritic
{
    Task<ProtocolCritique> FalsifyAsync(ValidationCampaign campaign, CancellationToken ct = default);
}

public sealed class OllamaProtocolCritic : IProtocolCritic
{
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<OllamaProtocolCritic> _logger;

    public OllamaProtocolCritic(IResearchToolbox toolbox, ILogger<OllamaProtocolCritic> logger)
    {
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<ProtocolCritique> FalsifyAsync(ValidationCampaign campaign, CancellationToken ct = default)
    {
        string raw;
        try { raw = await _toolbox.GenerateAsync(BuildPrompt(campaign), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Protocol critic unavailable; returning a conservative not-adequate critique.");
            // Fail closed: if we cannot falsify the design, do not imply it is sound.
            return new ProtocolCritique(
                new[] { "Protocol critic was unavailable — the design has NOT been falsified." },
                Adequate: false,
                Summary: "Critic unavailable; human should scrutinize the protocol manually.");
        }

        return Parse(raw) ?? new ProtocolCritique(
            new[] { "Protocol critique could not be parsed." }, Adequate: false, "Unparseable critic output.");
    }

    private static string BuildPrompt(ValidationCampaign c)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a rigorous, skeptical test reviewer. You are shown a HYPOTHESIS and a proposed");
        sb.AppendLine("VALIDATION PROTOCOL. Do NOT evaluate whether the hypothesis is true. Instead, FALSIFY THE");
        sb.AppendLine("PROTOCOL: list concrete failure modes / risks this protocol would NOT catch even if every");
        sb.AppendLine("step passed. Judge whether the protocol is adequate to justify the promotion it seeks.");
        sb.AppendLine();
        sb.AppendLine($"HYPOTHESIS: {c.HypothesisSnapshot}");
        sb.AppendLine($"PROMOTION SOUGHT: {c.TargetStage} (domain: {c.Domain})");
        sb.AppendLine("PROTOCOL STEPS:");
        foreach (var s in c.Protocol)
            sb.AppendLine($"- [{s.Kind} @ {s.Environment}] {s.Description} | criterion: {s.Criteria.Metric} {s.Criteria.Comparator} {s.Criteria.Threshold}");
        sb.AppendLine();
        sb.AppendLine("Respond with ONLY this JSON (no prose):");
        sb.AppendLine("""{"unexercisedFailureModes": ["..."], "adequate": true|false, "summary": "one sentence"}""");
        sb.AppendLine("JSON:");
        return sb.ToString();
    }

    internal static ProtocolCritique? Parse(string raw)
    {
        var json = JsonExtraction.FirstObject(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var modes = new List<string>();
            if (root.TryGetProperty("unexercisedFailureModes", out var m) && m.ValueKind == JsonValueKind.Array)
                foreach (var e in m.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        modes.Add(e.GetString()!.Trim());
            var adequate = root.TryGetProperty("adequate", out var a) && a.ValueKind == JsonValueKind.True;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString()?.Trim() ?? "" : "";
            return new ProtocolCritique(modes, adequate, summary);
        }
        catch { return null; }
    }
}
