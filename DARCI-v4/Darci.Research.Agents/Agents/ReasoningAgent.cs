#nullable enable

using System.Diagnostics;
using System.Text;
using Darci.Memory.Confidence;
using Darci.Research;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents.Agents;

public sealed class ReasoningAgent : IResearchAgent
{
    private readonly IResearchStore _store;
    private readonly IConfidenceTracker _confidence;
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<ReasoningAgent> _logger;

    public ReasoningAgent(
        IResearchStore store,
        IConfidenceTracker confidence,
        IResearchToolbox toolbox,
        ILogger<ReasoningAgent> logger)
    {
        _store = store;
        _confidence = confidence;
        _toolbox = toolbox;
        _logger = logger;
    }

    public string AgentType => "reasoning";

    public async Task<AgentReport> RunAsync(string jobId, string sessionId, string subQuestion, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _store.UpdateAgentJobAsync(jobId, "running", assignedAt: startedAt);

            var synthesis = await _confidence.SynthesizeAsync(
                subQuestion,
                getEmbedding: text => _toolbox.GetEmbeddingAsync(text, ct),
                ct: ct);

            var claimsText = new StringBuilder();
            foreach (var claim in synthesis.SupportingClaims.Take(8))
            {
                claimsText.Append("- ")
                    .Append(claim.Statement)
                    .Append(" (confidence ")
                    .Append(claim.Confidence.ToString("P0"))
                    .AppendLine(")");
            }

            var prompt = $"""
Based only on the following established claims, answer: {subQuestion}.
If you cannot answer, say so.
Claims:
{claimsText}
""";

            var summary = await _toolbox.GenerateAsync(prompt, ct);
            var confidence = synthesis.IsUncertain
                ? Math.Min(synthesis.AggregateConf, 0.35f)
                : synthesis.AggregateConf;

            if (synthesis.IsUncertain && !string.IsNullOrWhiteSpace(synthesis.UncertaintyReason))
            {
                summary = $"{summary}\n\nUncertainty: {synthesis.UncertaintyReason}";
            }

            await _store.AddResultAsync(sessionId, "reasoning", summary, "summary", relevanceScore: confidence);
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
            _logger.LogWarning(ex, "ReasoningAgent failed for job {JobId}", jobId);
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
