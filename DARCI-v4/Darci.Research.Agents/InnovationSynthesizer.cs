#nullable enable

using System.Text;
using System.Text.Json;
using Darci.Memory.Graph;
using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// The GENERATOR of the innovation node: recombines the KG/DR substrate into a candidate hypothesis, or
/// honestly concludes it is unsolvable. Deliberately does NOT judge its own output — the critic/review is
/// a separate agent (generator ≠ evaluator). Phase D adds diverse-candidate generation: each candidate is
/// prompted to DIFFER from ones already produced (forced within-cycle diversity = anti-mode-fixation).
/// </summary>
public interface IInnovationSynthesizer
{
    /// <summary>Single candidate with no diversity constraints (Phase B compatibility).</summary>
    Task<InnovationProposal> SynthesizeAsync(InnovationRequest request, CancellationToken ct = default);

    /// <summary>
    /// One candidate told to use a DIFFERENT mechanism / KG region than <paramref name="avoidHypotheses"/>
    /// (those already produced this cycle + archive), and NOT to repropose <paramref name="failedHypotheses"/>
    /// (empirically retracted ideas).
    /// </summary>
    Task<InnovationProposal> GenerateCandidateAsync(
        InnovationRequest request,
        IReadOnlyList<string> avoidHypotheses,
        IReadOnlyList<string> failedHypotheses,
        CancellationToken ct = default);
}

public sealed class OllamaInnovationSynthesizer : IInnovationSynthesizer
{
    private const double InitialHypothesisScore = 0.3;   // capped to Innovated (always IsLow) downstream

    /// <summary>This node's memory access: its id plus the scopes its manifest declares. Read-only — the
    /// innovation node reads the graph for context and never writes to it.</summary>
    private static readonly MemoryAccess Access =
        MemoryAccess.ForNode("darci.innovation", new[] { MemoryScopes.ReadKnowledge });

    private readonly IResearchToolbox _toolbox;
    private readonly IMemoryBroker _memory;
    private readonly ILogger<OllamaInnovationSynthesizer> _logger;

    public OllamaInnovationSynthesizer(IResearchToolbox toolbox, IMemoryBroker memory, ILogger<OllamaInnovationSynthesizer> logger)
    {
        _toolbox = toolbox;
        _memory = memory;
        _logger = logger;
    }

    public Task<InnovationProposal> SynthesizeAsync(InnovationRequest request, CancellationToken ct = default)
        => GenerateCandidateAsync(request, Array.Empty<string>(), Array.Empty<string>(), ct);

    public async Task<InnovationProposal> GenerateCandidateAsync(
        InnovationRequest request, IReadOnlyList<string> avoidHypotheses, IReadOnlyList<string> failedHypotheses, CancellationToken ct = default)
    {
        var relatedContext = await GatherGraphContextAsync(request.Question, ct);

        string raw;
        try
        {
            raw = await _toolbox.GenerateAsync(BuildPrompt(request, relatedContext, avoidHypotheses, failedHypotheses), ct);
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
            var entities = await _memory.SearchEntitiesAsync(Access, question, limit: 6, ct: ct);
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

    private static string BuildPrompt(InnovationRequest req, string relatedContext,
        IReadOnlyList<string> avoid, IReadOnlyList<string> failed)
    {
        var facts = req.FactList.Count > 0
            ? string.Join("\n", req.FactList.Select(f => $"- {f}"))
            : "(none gathered)";
        var gaps = req.GapList.Count > 0
            ? string.Join("\n", req.GapList.Select(g => $"- {g}"))
            : "(none)";

        var diversity = avoid.Count > 0
            ? "\nAlready tried this session — your candidate MUST use a DIFFERENT mechanism / different part\n" +
              "of the knowledge than ALL of these (do not merely refine them):\n" +
              string.Join("\n", avoid.Select(a => $"- {a}")) + "\n"
            : "";
        var negatives = failed.Count > 0
            ? "\nDo NOT repropose these approaches — they were TRIED and FAILED empirically:\n" +
              string.Join("\n", failed.Select(f => $"- {f}")) + "\n"
            : "";

        return $$"""
You are an innovation synthesizer. Known research and knowledge-graph facts about a problem are below,
but they did NOT already answer it. Produce ONE candidate solution by finding an INTERSECTION/novel
combination of the KNOWN material — or, honestly, conclude it cannot be solved with known information.
{{diversity}}{{negatives}}

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
