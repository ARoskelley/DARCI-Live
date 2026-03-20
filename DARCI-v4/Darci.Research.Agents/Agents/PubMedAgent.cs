#nullable enable

using System.Diagnostics;
using System.Text.Json;
using Darci.Memory.Confidence;
using Darci.Research;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents.Agents;

public sealed class PubMedAgent : IResearchAgent
{
    private readonly IResearchStore _store;
    private readonly IConfidenceTracker _confidence;
    private readonly IResearchToolbox _toolbox;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<PubMedAgent> _logger;

    public PubMedAgent(
        IResearchStore store,
        IConfidenceTracker confidence,
        IResearchToolbox toolbox,
        IHttpClientFactory httpClientFactory,
        ILogger<PubMedAgent> logger)
    {
        _store = store;
        _confidence = confidence;
        _toolbox = toolbox;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public string AgentType => "pubmed";

    public async Task<AgentReport> RunAsync(string jobId, string sessionId, string subQuestion, CancellationToken ct)
    {
        var startedAt = DateTime.UtcNow;
        var stopwatch = Stopwatch.StartNew();

        try
        {
            await _store.UpdateAgentJobAsync(jobId, "running", assignedAt: startedAt);

            var client = _httpClientFactory.CreateClient();
            client.BaseAddress = new Uri("https://eutils.ncbi.nlm.nih.gov/entrez/eutils/");

            var searchUrl = $"esearch.fcgi?db=pubmed&retmax=5&retmode=json&term={Uri.EscapeDataString(subQuestion)}";
            var searchJson = await client.GetStringAsync(searchUrl, ct);
            var pmids = ParsePmids(searchJson);

            if (pmids.Count == 0)
            {
                return await RunReasoningFallbackAsync(jobId, sessionId, subQuestion, startedAt, stopwatch, ct);
            }

            var summaryUrl = $"esummary.fcgi?db=pubmed&retmode=json&id={string.Join(",", pmids)}";
            var summaryJson = await client.GetStringAsync(summaryUrl, ct);
            var articles = ParsePubMedSummaries(summaryJson).Take(3).ToList();

            if (articles.Count == 0)
            {
                return await RunReasoningFallbackAsync(jobId, sessionId, subQuestion, startedAt, stopwatch, ct);
            }

            var abstracts = await FetchAbstractsAsync(pmids.Take(3), client, ct);
            var hasAbstracts = !string.IsNullOrWhiteSpace(abstracts);
            var confidence = hasAbstracts ? 0.75f : 0.55f;

            string prompt;
            if (hasAbstracts)
            {
                prompt = $"""
Summarize the following PubMed abstracts into 2-5 factual sentences.
Clearly state what was studied, what was found, and any caveats.
Question: {subQuestion}
Abstracts:
{abstracts}
""";
            }
            else
            {
                var notes = string.Join(Environment.NewLine, articles.Select(article => $"- {article}"));
                prompt = $"""
Summarize the following PubMed article results into 2-5 sentences.
Question: {subQuestion}
Results:
{notes}
""";
            }

            var summary = await _toolbox.GenerateAsync(prompt, ct);
            var sourceRef = string.Join(",", pmids.Select(id => $"PMID:{id}"));

            await _store.AddResultAsync(sessionId, "pubmed", summary, "summary", relevanceScore: confidence);
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
            _logger.LogWarning(ex, "PubMedAgent failed for job {JobId}; using reasoning fallback", jobId);
            return await RunReasoningFallbackAsync(jobId, sessionId, subQuestion, startedAt, stopwatch, ct, ex.Message);
        }
    }

    private async Task<AgentReport> RunReasoningFallbackAsync(
        string jobId,
        string sessionId,
        string subQuestion,
        DateTime startedAt,
        Stopwatch stopwatch,
        CancellationToken ct,
        string? fallbackReason = null)
    {
        try
        {
            var synthesis = await _confidence.SynthesizeAsync(
                subQuestion,
                getEmbedding: text => _toolbox.GetEmbeddingAsync(text, ct),
                ct: ct);

            var claims = string.Join(Environment.NewLine, synthesis.SupportingClaims.Select(claim => $"- {claim.Statement}"));
            var prompt = $"""
Based only on the following established claims, answer: {subQuestion}.
If you cannot answer, say so.
Claims:
{claims}
""";
            var summary = await _toolbox.GenerateAsync(prompt, ct);
            var confidence = synthesis.IsUncertain
                ? Math.Min(synthesis.AggregateConf, 0.35f)
                : synthesis.AggregateConf;

            if (!string.IsNullOrWhiteSpace(fallbackReason))
            {
                summary = $"{summary}\n\nFallback note: {fallbackReason}";
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
                Error = fallbackReason,
                Duration = stopwatch.Elapsed
            };
        }
        catch (Exception ex)
        {
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

    private async Task<string> FetchAbstractsAsync(IEnumerable<string> pmids, HttpClient client, CancellationToken ct)
    {
        try
        {
            var ids = string.Join(",", pmids);
            var url = $"efetch.fcgi?db=pubmed&rettype=abstract&retmode=text&id={ids}";
            var text = await client.GetStringAsync(url, ct);
            return string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PubMed efetch failed; falling back to title-only synthesis");
            return string.Empty;
        }
    }

    private static List<string> ParsePmids(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("esearchresult", out var result)
            || !result.TryGetProperty("idlist", out var idList))
        {
            return new List<string>();
        }

        return idList.EnumerateArray()
            .Select(item => item.GetString())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToList();
    }

    private static List<string> ParsePubMedSummaries(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("result", out var result))
        {
            return new List<string>();
        }

        var summaries = new List<string>();
        if (!result.TryGetProperty("uids", out var uids))
        {
            return summaries;
        }

        foreach (var uid in uids.EnumerateArray().Select(item => item.GetString()).Where(item => !string.IsNullOrWhiteSpace(item)))
        {
            if (!result.TryGetProperty(uid!, out var entry))
            {
                continue;
            }

            var title = entry.TryGetProperty("title", out var titleValue)
                ? titleValue.GetString()
                : null;
            var source = entry.TryGetProperty("source", out var sourceValue)
                ? sourceValue.GetString()
                : null;
            var pubDate = entry.TryGetProperty("pubdate", out var dateValue)
                ? dateValue.GetString()
                : null;

            var pieces = new[] { title, source, pubDate }
                .Where(piece => !string.IsNullOrWhiteSpace(piece));
            summaries.Add(string.Join(" | ", pieces));
        }

        return summaries;
    }
}
