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

        var firstPassReports = await Task.WhenAll(jobs.Select(job => RunAgentSafeAsync(job, linkedCts.Token)));

        // Change 6: gap-fill pass before quality gate
        var gapReports = await RunGapFillPassAsync(trimmedQuestion, firstPassReports, session.Id, ct);
        var reports = firstPassReports.Concat(gapReports).ToArray();

        var successfulReports = reports.Where(report => report.IsSuccess).ToList();
        if (successfulReports.Count == 0)
        {
            await _store.CompleteSessionAsync(session.Id, "failed");
            return ResearchOutcome.Failed(trimmedQuestion);
        }

        // Change 4: quality gate before synthesis
        const float QualityThreshold = 0.35f;
        var qualityReports = successfulReports
            .Where(r => r.Confidence >= QualityThreshold)
            .OrderByDescending(r => r.Confidence)
            .ToList();
        var reportsForSynthesis = qualityReports.Count > 0 ? qualityReports : successfulReports;

        var synthesisPrompt = DeepResearchPrompts.BuildSynthesisPrompt(trimmedQuestion, reportsForSynthesis);
        var finalAnswer = await _toolbox.GenerateAsync(synthesisPrompt, ct);
        var aggregateConfidence = reportsForSynthesis.Average(report => report.Confidence);

        // Change 5: build numbered citations list
        var citations = reportsForSynthesis
            .Select((r, i) => new ResearchCitation
            {
                Number = i + 1,
                AgentType = r.AgentType,
                SubQuestion = r.SubQuestion,
                SourceRef = r.SourceRef,
                Confidence = r.Confidence,
            })
            .ToList();

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
            Citations = citations,
            IsUncertain = aggregateConfidence < 0.45f
        };
    }

    private async Task<List<AgentReport>> RunGapFillPassAsync(
        string originalQuestion,
        IReadOnlyList<AgentReport> firstPassReports,
        string sessionId,
        CancellationToken ct)
    {
        var qualityCount = firstPassReports.Count(r => r.IsSuccess && r.Confidence >= 0.35f);
        if (qualityCount >= firstPassReports.Count / 2) return new List<AgentReport>();

        _logger.LogInformation("Gap fill: {Quality}/{Total} quality results, running follow-up",
            qualityCount, firstPassReports.Count);

        var coveredTopics = firstPassReports
            .Where(r => r.IsSuccess && r.Confidence >= 0.35f)
            .Select(r => r.SubQuestion);
        var missedTopics = firstPassReports
            .Where(r => !r.IsSuccess || r.Confidence < 0.35f)
            .Select(r => r.SubQuestion);

        var gapPrompt = DeepResearchPrompts.BuildGapFillPrompt(originalQuestion, coveredTopics, missedTopics);
        var gapResponse = await _toolbox.GenerateAsync(gapPrompt, ct);
        var followUpQuestions = ParseSubQuestions(gapResponse);

        if (followUpQuestions.Length == 0) return new List<AgentReport>();

        using var gapCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, gapCts.Token);

        var gapJobs = new List<ResearchAgentJob>();
        foreach (var q in followUpQuestions.Take(3))
        {
            var agentType = q.ToLowerInvariant().ContainsAny(
                "study", "gene", "protein", "clinical") ? "pubmed" : "web";
            gapJobs.Add(await _store.CreateAgentJobAsync(sessionId, q, agentType));
        }

        return (await Task.WhenAll(
            gapJobs.Select(j => RunAgentSafeAsync(j, linkedCts.Token))
        )).ToList();
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

        // Tier 1: PubMed triggers (medical/scientific empirical questions)
        var pubmedTriggers = new[]
        {
            "study", "trial", "clinical", "randomized", "cohort", "meta-analysis",
            "systematic review", "evidence", "efficacy", "safety", "outcome",
            "gene", "genetic", "genomic", "snp", "variant", "mutation", "allele",
            "protein", "enzyme", "receptor", "pathway", "expression", "folding",
            "dna", "rna", "mrna", "chromosome", "epigenetic",
            "diabetes", "cancer", "tumor", "insulin", "glucose", "hba1c",
            "predisposition", "risk factor", "prevalence", "incidence",
            "diagnosis", "prognosis", "treatment", "therapy", "drug",
            "biomarker", "assay", "screening",
            "molecule", "compound", "binding", "inhibitor", "agonist",
            "pharmacokinetic", "dose", "concentration",
        };
        if (pubmedTriggers.Any(t => normalized.Contains(t))) return "pubmed";

        // Tier 2: Graph (semantic match against existing knowledge)
        try
        {
            var embedding = await _toolbox.GetEmbeddingAsync(subQuestion, ct);
            var matches = await _graph.SemanticSearchAsync(embedding.ToArray(), limit: 1, ct);
            if (matches.Count > 0 && matches[0].Score > 0.7f) return "graph";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Semantic graph routing failed: {Q}", subQuestion);
        }

        // Tier 3: Reasoning (definitional / explanatory)
        var reasoningTriggers = new[]
        { "what is", "define", "explain", "describe", "how does", "what are" };
        if (reasoningTriggers.Any(t => normalized.StartsWith(t, StringComparison.Ordinal)))
            return "reasoning";

        // Default: Web
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

        if (lower.ContainsAny("gene", "protein", "disease", "biology", "cell",
            "diabetes", "cancer", "clinical", "genome", "dna", "rna",
            "enzyme", "receptor", "drug", "treatment", "diagnosis"))
            return "biology";

        if (lower.ContainsAny("chemical", "compound", "chemistry", "molecule",
            "reaction", "catalyst", "polymer", "organic", "inorganic"))
            return "chemistry";

        if (lower.ContainsAny("engineer", "cad", "assembly", "geometry",
            "circuit", "pcb", "mechanical", "structural", "simulation"))
            return "engineering";

        if (lower.ContainsAny("math", "proof", "equation", "theorem",
            "calculus", "algebra", "statistics", "probability"))
            return "math";

        return "general";
    }

}

internal static class StringExtensions
{
    internal static bool ContainsAny(this string source, params string[] values)
        => values.Any(v => source.Contains(v, StringComparison.OrdinalIgnoreCase));
}
