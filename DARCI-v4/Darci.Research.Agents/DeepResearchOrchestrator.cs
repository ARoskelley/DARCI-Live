#nullable enable

using System.Text.Json;
using Darci.Memory.Confidence;
using Darci.Memory.Graph;
using Darci.Research;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

public sealed class DeepResearchOrchestrator : IDeepResearchOrchestrator
{
    private readonly IResearchStore _store;
    private readonly IResearchAgentFactory _agentFactory;
    private readonly IKnowledgeGraph _graph;
    private readonly IConfidenceTracker _confidence;
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<DeepResearchOrchestrator> _logger;

    public DeepResearchOrchestrator(
        IResearchStore store,
        IResearchAgentFactory agentFactory,
        IKnowledgeGraph graph,
        IConfidenceTracker confidence,
        IResearchToolbox toolbox,
        ILogger<DeepResearchOrchestrator> logger)
    {
        _store = store;
        _agentFactory = agentFactory;
        _graph = graph;
        _confidence = confidence;
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<ResearchOutcome> RunDeepResearchAsync(string question, string userId, CancellationToken ct = default)
    {
        var trimmedQuestion = question.Trim();
        var domain = DetectDomain(trimmedQuestion);

        var decompositionPrompt = DeepResearchPrompts.BuildDecompositionPrompt(trimmedQuestion);
        var decompositionResponse = await _toolbox.GenerateAsync(decompositionPrompt, ct);
        var subQuestions = ParseSubQuestions(decompositionResponse);
        if (subQuestions.Length == 0)
        {
            subQuestions = new[] { trimmedQuestion };
        }

        var session = await _store.CreateSessionAsync(
            title: trimmedQuestion,
            description: $"Deep research requested by {userId}",
            createdBy: "DeepResearch",
            tags: new[] { "deep_research", domain });

        var jobs = new List<ResearchAgentJob>(subQuestions.Length);
        foreach (var subQuestion in subQuestions)
        {
            var agentType = await SelectAgentTypeAsync(subQuestion, ct);
            jobs.Add(await _store.CreateAgentJobAsync(session.Id, subQuestion, agentType));
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var reports = await Task.WhenAll(jobs.Select(job => RunAgentSafeAsync(job, linkedCts.Token)));
        var successfulReports = reports.Where(report => report.IsSuccess).ToList();
        if (successfulReports.Count == 0)
        {
            await _store.CompleteSessionAsync(session.Id, "failed");
            return ResearchOutcome.Failed(trimmedQuestion);
        }

        var synthesisPrompt = DeepResearchPrompts.BuildSynthesisPrompt(trimmedQuestion, successfulReports);
        var finalAnswer = await _toolbox.GenerateAsync(synthesisPrompt, ct);
        var aggregateConfidence = successfulReports.Average(report => report.Confidence);

        await _store.AddResultAsync(
            session.Id,
            source: "synthesis",
            content: finalAnswer,
            resultType: "synthesis",
            tags: new[] { "deep_research", domain },
            relevanceScore: aggregateConfidence);

        await _store.CompleteSessionAsync(session.Id);

        await _graph.IngestMemoryAsync(
            finalAnswer,
            new[] { "deep_research", domain },
            getEmbedding: text => _toolbox.GetEmbeddingAsync(text, ct),
            llmExtract: prompt => _toolbox.GenerateAsync(prompt, ct),
            ct: ct);

        await _confidence.AddClaimAsync(
            finalAnswer,
            domain,
            "reasoning",
            sourceRef: session.Id,
            sourceQuality: aggregateConfidence,
            ct: ct);

        return new ResearchOutcome
        {
            IsSuccess = true,
            SessionId = session.Id,
            Question = trimmedQuestion,
            FinalAnswer = finalAnswer,
            Confidence = aggregateConfidence,
            AgentReports = reports,
            IsUncertain = aggregateConfidence < 0.45f
        };
    }

    private async Task<AgentReport> RunAgentSafeAsync(ResearchAgentJob job, CancellationToken ct)
    {
        try
        {
            var agent = _agentFactory.Create(job.AgentType);
            return await agent.RunAsync(job.Id, job.SessionId, job.SubQuestion, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deep research agent {AgentType} crashed for job {JobId}", job.AgentType, job.Id);
            await _store.UpdateAgentJobAsync(
                job.Id,
                status: "failed",
                error: ex.Message,
                assignedAt: DateTime.UtcNow,
                completedAt: DateTime.UtcNow);

            return new AgentReport
            {
                JobId = job.Id,
                AgentType = job.AgentType,
                SubQuestion = job.SubQuestion,
                IsSuccess = false,
                Error = ex.Message,
                Duration = TimeSpan.Zero
            };
        }
    }

    private async Task<string> SelectAgentTypeAsync(string subQuestion, CancellationToken ct)
    {
        var normalized = subQuestion.Trim().ToLowerInvariant();
        if (normalized.Contains("study")
            || normalized.Contains("trial")
            || normalized.Contains("pubmed")
            || normalized.Contains("research")
            || normalized.Contains("evidence"))
        {
            return "pubmed";
        }

        try
        {
            var embedding = await _toolbox.GetEmbeddingAsync(subQuestion, ct);
            var matches = await _graph.SemanticSearchAsync(embedding.ToArray(), limit: 1, ct);
            if (matches.Count > 0 && matches[0].Score > 0.7f)
            {
                return "graph";
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Semantic graph routing failed for sub-question: {SubQuestion}", subQuestion);
        }

        if (normalized.StartsWith("what is", StringComparison.Ordinal)
            || normalized.StartsWith("define", StringComparison.Ordinal)
            || normalized.StartsWith("explain", StringComparison.Ordinal))
        {
            return "reasoning";
        }

        return "web";
    }

    private static string[] ParseSubQuestions(string response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return Array.Empty<string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<string[]>(response);
            if (parsed is { Length: > 0 })
            {
                return parsed
                    .Where(item => !string.IsNullOrWhiteSpace(item))
                    .Select(item => item.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToArray();
            }
        }
        catch
        {
            // Fall back to line parsing.
        }

        return response
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => line.TrimStart('-', '*', '1', '2', '3', '4', '5', '6', '.', ' '))
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToArray();
    }

    private static string DetectDomain(string question)
    {
        var lower = question.ToLowerInvariant();
        if (lower.Contains("gene") || lower.Contains("protein") || lower.Contains("disease") || lower.Contains("biology"))
        {
            return "biology";
        }

        if (lower.Contains("chemical") || lower.Contains("compound") || lower.Contains("chemistry"))
        {
            return "chemistry";
        }

        if (lower.Contains("engineer") || lower.Contains("cad") || lower.Contains("assembly") || lower.Contains("geometry"))
        {
            return "engineering";
        }

        if (lower.Contains("math") || lower.Contains("proof") || lower.Contains("equation"))
        {
            return "math";
        }

        return "general";
    }
}
