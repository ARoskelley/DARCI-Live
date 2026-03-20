#nullable enable

using System.Diagnostics;
using System.Text.RegularExpressions;
using Darci.Research;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents.Agents;

public sealed class WebResearchAgent : IResearchAgent
{
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IResearchStore _store;
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<WebResearchAgent> _logger;

    public WebResearchAgent(IResearchStore store, IResearchToolbox toolbox, ILogger<WebResearchAgent> logger)
    {
        _store = store;
        _toolbox = toolbox;
        _logger = logger;
    }

    public string AgentType => "web";

    public async Task<AgentReport> RunAsync(string jobId, string sessionId, string subQuestion, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _store.UpdateAgentJobAsync(jobId, "running", assignedAt: startedAt);

            var raw = await _toolbox.SearchWebAsync(subQuestion, ct);
            var confidence = ScoreSourceQuality(raw, subQuestion);

            var prompt = $"""
Summarize the following research notes into 2-5 factual sentences.
If the notes are weak or uncertain, say that plainly.
Notes:
{raw}
""";
            var summary = await _toolbox.GenerateAsync(prompt, ct);
            if (confidence < 0.25f)
            {
                summary = "Note: source quality is low for this result. " + summary;
            }

            var sourceRef = UrlRegex.Match(raw).Success ? UrlRegex.Match(raw).Value : null;

            await _store.AddResultAsync(sessionId, "web", summary, "summary", relevanceScore: confidence);
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
                SourceRef = sourceRef,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WebResearchAgent failed for job {JobId}", jobId);
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

    private static float ScoreSourceQuality(string rawResult, string subQuestion)
    {
        float score = 0.35f;
        var lower = rawResult.ToLowerInvariant();

        if (lower.Contains(".gov") || lower.Contains(".edu"))             score = Math.Max(score, 0.72f);
        if (lower.Contains("pubmed") || lower.Contains("ncbi"))          score = Math.Max(score, 0.80f);
        if (lower.Contains("nejm.org") || lower.Contains("bmj.com"))     score = Math.Max(score, 0.85f);
        if (lower.Contains("who.int") || lower.Contains("cdc.gov"))      score = Math.Max(score, 0.78f);
        if (lower.Contains("cochrane"))                                   score = Math.Max(score, 0.88f);
        if (lower.Contains("nature.com") || lower.Contains("science.org")) score = Math.Max(score, 0.82f);
        if (lower.Contains("arxiv.org"))                                  score = Math.Max(score, 0.65f);

        if (lower.Contains("wikipedia"))                                  score = Math.Min(score, 0.45f);
        if (lower.Contains("reddit") || lower.Contains("quora"))         score = Math.Min(score, 0.25f);

        int numericMatches = Regex.Matches(rawResult, @"\d+\.?\d*\s*%").Count;
        if (numericMatches >= 3) score = Math.Min(score + 0.05f, 0.92f);

        return Math.Clamp(score, 0.10f, 0.92f);
    }
}
