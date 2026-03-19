#nullable enable

namespace Darci.Research.Agents;

public interface IResearchAgentFactory
{
    IResearchAgent Create(string agentType);
}

public sealed class ResearchAgentFactory : IResearchAgentFactory
{
    private readonly IServiceProvider _services;

    public ResearchAgentFactory(IServiceProvider services)
    {
        _services = services;
    }

    public IResearchAgent Create(string agentType)
    {
        var normalized = string.IsNullOrWhiteSpace(agentType)
            ? "web"
            : agentType.Trim().ToLowerInvariant();

        return normalized switch
        {
            "graph" => Resolve<Agents.GraphResearchAgent>(),
            "reasoning" => Resolve<Agents.ReasoningAgent>(),
            "pubmed" => Resolve<Agents.PubMedAgent>(),
            _ => Resolve<Agents.WebResearchAgent>()
        };
    }

    private T Resolve<T>() where T : IResearchAgent
        => (T)(_services.GetService(typeof(T))
            ?? throw new InvalidOperationException($"Research agent {typeof(T).Name} is not registered."));
}
