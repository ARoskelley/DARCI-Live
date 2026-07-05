#nullable enable

using System.Text;
using System.Text.Json;
using Darci.Memory.Graph;
using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// The GENERATOR of the innovation node (single-pass, Phase B): recombines the KG/DR substrate into ONE
/// candidate hypothesis, or honestly concludes it is unsolvable with known information. Deliberately does
/// NOT judge its own output — the plausibility review is a separate agent (generator ≠ evaluator).
/// </summary>
public interface IInnovationSynthesizer
{
    Task<InnovationProposal> SynthesizeAsync(InnovationRequest request, CancellationToken ct = default);
}

public sealed class OllamaInnovationSynthesizer : IInnovationSynthesizer
{
    private const double InitialHypothesisScore = 0.3;   // capped to Innovated (always IsLow) downstream

    private readonly IResearchToolbox _toolbox;
    private readonly IKnowledgeGraph _graph;
    private readonly ILogger<OllamaInnovationSynthesizer> _logger;

    public OllamaInnovationSynthesizer(IResearchToolbox toolbox, IKnowledgeGraph graph, ILogger<OllamaInnovationSynthesizer> logger)
    {
        _toolbox = toolbox;
        _graph = graph;
        _logger = logger;
    }

    public async Task<InnovationProposal> SynthesizeAsync(InnovationRequest request, CancellationToken ct = default)
    {
        var relatedContext = await GatherGraphContextAsync(request.Question, ct);

        string raw;
        try
        {
            raw = await _toolbox.GenerateAsync(BuildPrompt(request, relatedContext), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Innovation synthesizer unavailable; concluding unsolvable.");
            return InnovationProposal.CannotSolve(
                "Synthesis model unavailable.", new[] { "retry when the local model is reachable" });
        }

        return Parse(raw)
            ?? InnovationProposal.CannotSolve(
                "Synthesis produced no structured candidate.",
                new[] { "a clearer problem statement or additional data" });
    }

    private async Task<string> GatherGraphContextAsync(string question, CancellationToken ct)
    {
        try
        {
            var entities = await _graph.SearchEntitiesAsync(question, limit: 6, ct: ct);
            if (entities.Count == 0) return "";
            var sb = new StringBuilder("Related concepts in the knowledge graph:\n");
            foreach (var e in entities)
                sb.AppendLine($"- {e.Name}{(string.IsNullOrWhiteSpace(e.Description) ? "" : $": {e.Description}")}");
            return sb.ToString();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Graph context lookup failed (non-fatal).");
            return "";
        }
    }

    private static string BuildPrompt(InnovationRequest req, string relatedContext)
    {
        var facts = req.FactList.Count > 0
            ? string.Join("\n", req.FactList.Select(f => $"- {f}"))
            : "(none gathered)";
        var gaps = req.GapList.Count > 0
            ? string.Join("\n", req.GapList.Select(g => $"- {g}"))
            : "(none)";

        return $$"""
You are an innovation synthesizer. Known research and knowledge-graph facts about a problem are below,
but they did NOT already answer it. Produce ONE candidate solution by finding an INTERSECTION/novel
combination of the KNOWN material — or, honestly, conclude it cannot be solved with known information.

Respond with ONLY this JSON (no prose, no markdown):
{"solvable": true|false,
 "hypothesis": "one concrete candidate solution (empty if not solvable)",
 "reasoning": [{"inference": "...", "citedFacts": ["which known facts this step uses"]}],
 "assumptions": ["..."],
 "requiredExternalInputs": ["if not solvable: the specific external data/experiments needed"]}

Rules:
- Only combine the KNOWN facts/concepts below; do not invent unstated facts.
- If no combination of the known material plausibly solves it, set solvable=false and list exactly what
  external data or experiment would be required. An honest "cannot solve" is a valid, valued answer.

QUESTION: {{req.Question}}
ORIGINATING GOAL: {{req.Intent}}
WHY IT'S STUCK: {{req.FailureContext ?? "(unspecified)"}}
UNMET GAPS:
{{gaps}}
KNOWN FACTS:
{{facts}}
{{relatedContext}}
JSON:
""";
    }

    internal static InnovationProposal? Parse(string raw)
    {
        var json = JsonExtraction.FirstObject(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var solvable = root.TryGetProperty("solvable", out var s) && s.ValueKind == JsonValueKind.True;
            var required = ReadStrings(root, "requiredExternalInputs");
            var assumptions = ReadStrings(root, "assumptions");
            var hypothesis = root.TryGetProperty("hypothesis", out var h) ? h.GetString()?.Trim() ?? "" : "";

            if (!solvable || string.IsNullOrWhiteSpace(hypothesis))
                return new InnovationProposal
                {
                    Status = ProposalStatus.Unsolvable,
                    RequiredExternalInputs = required.Count > 0 ? required : new[] { "external data or experiment (unspecified)" },
                    Assumptions = assumptions,
                    Confidence = Confidence.Unassessed,
                };

            var reasoning = new List<ReasoningLink>();
            if (root.TryGetProperty("reasoning", out var r) && r.ValueKind == JsonValueKind.Array)
            {
                foreach (var step in r.EnumerateArray())
                {
                    if (step.ValueKind != JsonValueKind.Object) continue;
                    var inf = step.TryGetProperty("inference", out var i) ? i.GetString() : null;
                    if (string.IsNullOrWhiteSpace(inf)) continue;
                    var cited = new List<string>();
                    if (step.TryGetProperty("citedFacts", out var cf) && cf.ValueKind == JsonValueKind.Array)
                        foreach (var c in cf.EnumerateArray())
                            if (c.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(c.GetString()))
                                cited.Add(c.GetString()!.Trim());
                    reasoning.Add(new ReasoningLink(inf!.Trim(), cited));
                }
            }

            // Capped to Innovated → always IsLow. Trust is only ever raised by a human event.
            return new InnovationProposal
            {
                Status = ProposalStatus.Proposed,
                Hypothesis = hypothesis,
                Reasoning = reasoning,
                Assumptions = assumptions,
                RequiredExternalInputs = required,
                Provenance = Provenance.Innovated,
                Confidence = ProvenancePolicy.Clamp(Provenance.Innovated, Confidence.Of(InitialHypothesisScore)),
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ReadStrings(JsonElement root, string name)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                    list.Add(item.GetString()!.Trim());
        return list;
    }
}
