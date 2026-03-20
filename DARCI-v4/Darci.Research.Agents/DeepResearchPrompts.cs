#nullable enable

using System.Text;
using Darci.Research.Agents.Models;

namespace Darci.Research.Agents;

public static class DeepResearchPrompts
{
    public const string DecompositionTemplate = """
You are a research coordinator. Break the following research question into
3-6 specific, answerable sub-questions. Each sub-question should focus on
a distinct aspect. Respond ONLY with a JSON array of strings, no markdown.
Question: {question}
""";

    public const string SynthesisTemplate = """
You are a scientific analyst. Synthesize the following research findings
into a coherent answer. If findings conflict, note the disagreement.
Express appropriate uncertainty if confidence is low.
Original question: {question}
Research findings:
{reports}
""";

    public static string BuildDecompositionPrompt(string question)
        => DecompositionTemplate.Replace("{question}", question);

    public static string BuildSynthesisPrompt(string question, IEnumerable<AgentReport> reports)
    {
        var reportList = reports.ToList();
        var sources = new StringBuilder();
        for (int i = 0; i < reportList.Count; i++)
        {
            var r = reportList[i];
            sources.AppendLine($"[{i + 1}] ({r.AgentType}, confidence {r.Confidence:P0})");
            sources.AppendLine(r.Summary);
            sources.AppendLine();
        }

        return $"""
You are a scientific analyst synthesizing research findings.
Write a coherent, factual answer to the question below.

CITATION RULES (follow exactly):
- After every factual claim, add the citation number in brackets, e.g. [1] or [1,3].
- If findings from different sources conflict, state the disagreement explicitly and cite both sides.
- If confidence is below 45%, begin the answer with: 'Note: evidence on this topic is limited or conflicting.'
- Do not invent facts not present in the sources below.

Question: {question}

Sources:
{sources.ToString().Trim()}
""";
    }

    public static string BuildGapFillPrompt(
        string question,
        IEnumerable<string> coveredTopics,
        IEnumerable<string> missedTopics)
    {
        var covered = string.Join("\n", coveredTopics.Select(t => $"  - {t}"));
        var missed  = string.Join("\n", missedTopics.Select(t => $"  - {t}"));
        return $"""
You are a research coordinator reviewing an incomplete investigation.
Original question: {question}

Topics with good coverage:
{covered}

Topics with poor or missing coverage:
{missed}

Generate 2-3 focused follow-up questions to fill the gaps.
Respond ONLY with a JSON array of strings, no markdown.
""";
    }
}
