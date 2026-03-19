#nullable enable

using Darci.Research.Agents.Models;

namespace Darci.Research.Agents;

public static class ResearchAgentsExtensions
{
    public static IReadOnlyList<AgentReport> Successful(this IEnumerable<AgentReport> reports)
        => reports.Where(report => report.IsSuccess).ToList();
}
