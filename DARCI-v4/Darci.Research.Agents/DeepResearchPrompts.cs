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
        var builder = new StringBuilder();
        foreach (var report in reports)
        {
            builder.Append('[')
                .Append(report.AgentType)
                .Append("] ")
                .Append(report.Summary)
                .Append(" (confidence ")
                .Append(report.Confidence.ToString("P0"))
                .AppendLine(")");
        }

        return SynthesisTemplate
            .Replace("{question}", question)
            .Replace("{reports}", builder.ToString().Trim());
    }
}
