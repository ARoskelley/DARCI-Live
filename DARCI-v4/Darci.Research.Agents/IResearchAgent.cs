#nullable enable

using Darci.Research.Agents.Models;

namespace Darci.Research.Agents;

public interface IResearchAgent
{
    string AgentType { get; }

    Task<AgentReport> RunAsync(
        string jobId,
        string sessionId,
        string subQuestion,
        CancellationToken ct);
}
