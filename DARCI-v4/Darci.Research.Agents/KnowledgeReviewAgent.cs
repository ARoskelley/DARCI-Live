#nullable enable

using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// Reviews whether a candidate answer actually fulfills a request. Used twice in the pipeline: once to
/// gate whether the KG answer is good enough (else escalate to deep research) and once after compilation
/// to validate the structured response before returning. This is one of the two stages the old node was
/// missing.
/// </summary>
public interface IKnowledgeReviewAgent
{
    Task<KnowledgeReview> ReviewAsync(
        KnowledgeRequest request, string candidateAnswer, string sourceLabel, CancellationToken ct = default);
}

/// <summary>Ollama-backed reviewer. Emits a strict JSON verdict; on any failure it returns "does not fulfill"
/// so the pipeline escalates rather than wrongly accepting (fail-safe toward more research).</summary>
public sealed class OllamaKnowledgeReviewAgent : IKnowledgeReviewAgent
{
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<OllamaKnowledgeReviewAgent> _logger;

    public OllamaKnowledgeReviewAgent(IResearchToolbox toolbox, ILogger<OllamaKnowledgeReviewAgent> logger)
    {
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<KnowledgeReview> ReviewAsync(
        KnowledgeRequest request, string candidateAnswer, string sourceLabel, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(candidateAnswer))
            return new KnowledgeReview(false, Confidence.Unassessed,
                new[] { "no candidate answer was produced" }, "Empty candidate.");

        var prompt = BuildPrompt(request, candidateAnswer, sourceLabel);
        string raw;
        try
        {
            raw = await _toolbox.GenerateAsync(prompt, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Knowledge reviewer unavailable; treating as not-fulfilled (will escalate).");
            return new KnowledgeReview(false, Confidence.Unassessed,
                new[] { "reviewer unavailable" }, "Review failed; defaulting to escalate.");
        }

        return Parse(raw);
    }

    private static string BuildPrompt(KnowledgeRequest req, string candidate, string source)
        => $$"""
You are a strict reviewer. Decide whether the ANSWER actually and completely addresses the REQUEST.
Respond with ONLY a JSON object, no prose, no markdown:
{"fulfills": true|false, "confidence": 0.0-1.0, "missing": ["..."], "reasoning": "..."}
- fulfills: true only if the answer is directly usable to satisfy the request.
- missing: concrete aspects still unanswered (empty if fulfills).

REQUEST (kind={{req.Kind}}): {{req.Question}}
ANSWER (source={{source}}):
{{candidate}}

JSON:
""";

    internal static KnowledgeReview Parse(string raw)
    {
        var json = JsonExtraction.FirstObject(raw);
        if (json is null)
            return new KnowledgeReview(false, Confidence.Unassessed,
                new[] { "unparseable review" }, "Reviewer returned no JSON.");
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var fulfills = root.TryGetProperty("fulfills", out var f) && f.ValueKind == JsonValueKind.True;
            double score = root.TryGetProperty("confidence", out var c) && c.TryGetDouble(out var d) ? d : -1.0;
            var missing = new List<string>();
            if (root.TryGetProperty("missing", out var m) && m.ValueKind == JsonValueKind.Array)
                foreach (var item in m.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        missing.Add(item.GetString()!.Trim());
            var reasoning = root.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";
            return new KnowledgeReview(fulfills, Confidence.Of(score), missing, reasoning);
        }
        catch
        {
            return new KnowledgeReview(false, Confidence.Unassessed,
                new[] { "unparseable review" }, "Reviewer JSON was invalid.");
        }
    }
}
