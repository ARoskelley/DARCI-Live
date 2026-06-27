#nullable enable

using System.Text.Json;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// Compiles raw research/KG findings into the rigid <see cref="KnowledgeResponse"/> structure — cutting
/// fluff and forcing the answer into typed fields. The other stage the old node was missing: it is what
/// guarantees a structured, non-prose output regardless of how rambling the underlying synthesis was.
/// </summary>
public interface IKnowledgeCompilerAgent
{
    Task<KnowledgeResponse> CompileAsync(
        KnowledgeRequest request, string rawFindings, IReadOnlyList<ResearchCitation> citations, CancellationToken ct = default);
}

/// <summary>Ollama-backed compiler. Always returns structured output: if the model's JSON can't be parsed,
/// it falls back to a best-effort structuring of the raw text (never a prose blob).</summary>
public sealed class OllamaKnowledgeCompilerAgent : IKnowledgeCompilerAgent
{
    private const int MaxItems = 12;

    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<OllamaKnowledgeCompilerAgent> _logger;

    public OllamaKnowledgeCompilerAgent(IResearchToolbox toolbox, ILogger<OllamaKnowledgeCompilerAgent> logger)
    {
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<KnowledgeResponse> CompileAsync(
        KnowledgeRequest request, string rawFindings, IReadOnlyList<ResearchCitation> citations, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawFindings))
            return KnowledgeResponse.Unanswered("No findings were available to compile.");

        string raw;
        try
        {
            raw = await _toolbox.GenerateAsync(BuildPrompt(request, rawFindings), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Knowledge compiler unavailable; falling back to raw structuring.");
            return Fallback(rawFindings, citations);
        }

        return Parse(raw, citations) ?? Fallback(rawFindings, citations);
    }

    private static string BuildPrompt(KnowledgeRequest req, string rawFindings)
        => $$"""
You are a knowledge compiler. Convert the RESEARCH NOTES into STRICT JSON with EXACTLY this schema:
{"directAnswer": "...", "findings": ["..."], "steps": ["..."], "examples": [{"summary":"...","source":null}], "gaps": ["..."]}
Rules:
- Cut all fluff, boilerplate, and hedging. No markdown, output ONLY the JSON object.
- findings = atomic facts. steps = concrete actions/paths forward. gaps = what is still unknown.
- If the notes do not answer the question, say so in gaps and leave directAnswer empty.

QUESTION (kind={{req.Kind}}): {{req.Question}}
RESEARCH NOTES:
{{rawFindings}}

JSON:
""";

    internal static KnowledgeResponse? Parse(string raw, IReadOnlyList<ResearchCitation> citations)
    {
        var json = JsonExtraction.FirstObject(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var directAnswer = root.TryGetProperty("directAnswer", out var da) ? da.GetString() ?? "" : "";
            var findings = ReadStringArray(root, "findings");
            var steps = ReadStringArray(root, "steps");
            var gaps = ReadStringArray(root, "gaps");

            var examples = new List<KnowledgeCaseStudy>();
            if (root.TryGetProperty("examples", out var ex) && ex.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in ex.EnumerateArray())
                {
                    if (e.ValueKind != JsonValueKind.Object) continue;
                    var summary = e.TryGetProperty("summary", out var s) ? s.GetString() : null;
                    if (string.IsNullOrWhiteSpace(summary)) continue;
                    var source = e.TryGetProperty("source", out var src) && src.ValueKind == JsonValueKind.String
                        ? src.GetString() : null;
                    examples.Add(new KnowledgeCaseStudy(summary!.Trim(), source));
                    if (examples.Count >= MaxItems) break;
                }
            }

            // Require at least *some* structured content, else treat as a parse miss → fallback.
            if (string.IsNullOrWhiteSpace(directAnswer) && findings.Count == 0 && steps.Count == 0 && gaps.Count == 0)
                return null;

            return new KnowledgeResponse
            {
                DirectAnswer = directAnswer.Trim(),
                Findings = findings,
                Steps = steps,
                Examples = examples,
                Gaps = gaps,
                Citations = citations,
            };
        }
        catch
        {
            return null;
        }
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    list.Add(item.GetString()!.Trim());
                    if (list.Count >= MaxItems) break;
                }
        return list;
    }

    /// <summary>Last-resort structuring when the model didn't return usable JSON — split into lines/sentences
    /// so the output is still structured (never a prose blob), and flag that compilation degraded.</summary>
    internal static KnowledgeResponse Fallback(string rawFindings, IReadOnlyList<ResearchCitation> citations)
    {
        var pieces = rawFindings
            .Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .SelectMany(line => line.Split(". ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(s => s.TrimStart('-', '*', '•', ' ').Trim())
            .Where(s => s.Length > 0)
            .Take(MaxItems)
            .ToList();

        return new KnowledgeResponse
        {
            DirectAnswer = pieces.Count > 0 ? pieces[0] : "",
            Findings = pieces,
            Citations = citations,
            Gaps = new[] { "Compiler could not structure the output; findings are raw-split." },
        };
    }
}
