#nullable enable

using System.Diagnostics;
using System.Text;
using Darci.Memory.Graph;
using Darci.Research;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents.Agents;

public sealed class GraphResearchAgent : IResearchAgent
{
    private readonly IResearchStore _store;
    private readonly IKnowledgeGraph _graph;
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<GraphResearchAgent> _logger;

    public GraphResearchAgent(
        IResearchStore store,
        IKnowledgeGraph graph,
        IResearchToolbox toolbox,
        ILogger<GraphResearchAgent> logger)
    {
        _store = store;
        _graph = graph;
        _toolbox = toolbox;
        _logger = logger;
    }

    public string AgentType => "graph";

    public async Task<AgentReport> RunAsync(string jobId, string sessionId, string subQuestion, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _store.UpdateAgentJobAsync(jobId, "running", assignedAt: startedAt);

            var entities = await _graph.SearchEntitiesAsync(subQuestion, limit: 3, ct: ct);
            if (entities.Count == 0)
            {
                const string error = "No graph knowledge for this sub-question";
                await _store.UpdateAgentJobAsync(
                    jobId,
                    "failed",
                    error: error,
                    assignedAt: startedAt,
                    completedAt: DateTime.UtcNow);

                return new AgentReport
                {
                    JobId = jobId,
                    AgentType = AgentType,
                    SubQuestion = subQuestion,
                    IsSuccess = false,
                    Error = error,
                    Duration = stopwatch.Elapsed
                };
            }

            var builder = new StringBuilder();
            foreach (var entity in entities)
            {
                var neighbours = await _graph.GetNeighboursAsync(entity.Id, depth: 2, ct: ct);
                var map = neighbours.Entities.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);
                foreach (var relation in neighbours.Relations)
                {
                    if (!map.TryGetValue(relation.FromEntityId, out var from) || !map.TryGetValue(relation.ToEntityId, out var to))
                    {
                        continue;
                    }

                    builder.AppendLine($"{from.Name} ({from.EntityType}) - {relation.RelationType} -> {to.Name}");
                }
            }

            var prompt = $"""
Summarize the following knowledge-graph relations into 2-5 sentences that answer the sub-question.
Sub-question: {subQuestion}
Relations:
{builder}
""";
            var summary = await _toolbox.GenerateAsync(prompt, ct);
            var confidence = entities.Average(entity => entity.Confidence);

            await _store.AddResultAsync(sessionId, "graph", summary, "summary", relevanceScore: confidence);
            await _store.UpdateAgentJobAsync(
                jobId,
                "done",
                resultSummary: summary,
                confidence: confidence,
                assignedAt: startedAt,
                completedAt: DateTime.UtcNow);

            return new AgentReport
            {
                JobId = jobId,
                AgentType = AgentType,
                SubQuestion = subQuestion,
                IsSuccess = true,
                Summary = summary,
                Confidence = confidence,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GraphResearchAgent failed for job {JobId}", jobId);
            await _store.UpdateAgentJobAsync(
                jobId,
                "failed",
                error: ex.Message,
                assignedAt: startedAt,
                completedAt: DateTime.UtcNow);

            return new AgentReport
            {
                JobId = jobId,
                AgentType = AgentType,
                SubQuestion = subQuestion,
                IsSuccess = false,
                Error = ex.Message,
                Duration = stopwatch.Elapsed
            };
        }
    }
}
