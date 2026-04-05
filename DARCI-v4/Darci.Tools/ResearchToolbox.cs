#nullable enable

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Darci.Research.Agents;
using Darci.Tools.Ollama;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Darci.Tools;

/// <summary>
/// Research-facing toolbox used by the deep-research stack.
/// Stays separate from <see cref="Toolkit"/> to avoid a DI cycle
/// through the deep-research orchestrator.
///
/// Web search priority:
///   1. Tavily  — if ApiKey is configured
///   2. Local Ollama knowledge — fallback when no key is present
///
/// Firecrawl full-text extraction is optional and only fires when
/// Research:Firecrawl:Enabled = true and a URL is returned by Tavily.
/// </summary>
public sealed class ResearchToolbox : IResearchToolbox
{
    private readonly IOllamaClient _ollama;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _config;
    private readonly ILogger<ResearchToolbox> _logger;

    public ResearchToolbox(
        IOllamaClient ollama,
        IHttpClientFactory httpClientFactory,
        IConfiguration config,
        ILogger<ResearchToolbox> logger)
    {
        _ollama = ollama;
        _httpClientFactory = httpClientFactory;
        _config = config;
        _logger = logger;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        => _ollama.Generate(prompt);

    public Task<List<float>> GetEmbeddingAsync(string text, CancellationToken ct = default)
        => _ollama.GetEmbedding(text);

    public async Task<string> SearchWebAsync(string query, CancellationToken ct = default)
    {
        var tavilyKey = _config["Research:Tavily:ApiKey"];

        if (!string.IsNullOrWhiteSpace(tavilyKey))
        {
            return await SearchViaTavilyAsync(query, tavilyKey, ct);
        }

        _logger.LogWarning(
            "Tavily API key not configured (Research:Tavily:ApiKey). " +
            "Using local model knowledge for: {Query}. " +
            "Results will not reflect live web data.",
            query);

        return await _ollama.Generate(
            $"Based on your existing knowledge, provide information about: {query}\n\n" +
            "Be concise and factual. If you do not know something, say so plainly.");
    }

    // -------------------------------------------------------------------------
    // Tavily
    // -------------------------------------------------------------------------

    private async Task<string> SearchViaTavilyAsync(string query, string apiKey, CancellationToken ct)
    {
        var baseUrl    = _config["Research:Tavily:BaseUrl"]    ?? "https://api.tavily.com";
        var maxResults = int.TryParse(_config["Research:Tavily:MaxResults"], out var n) ? n : 5;
        var depth      = _config["Research:Tavily:SearchDepth"] ?? "advanced";

        try
        {
            var client = _httpClientFactory.CreateClient("tavily");
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", apiKey);

            var body = JsonSerializer.Serialize(new
            {
                query,
                search_depth       = depth,
                max_results        = maxResults,
                include_answer     = true,
                include_raw_content = false
            });

            var response = await client.PostAsync(
                "/search",
                new StringContent(body, Encoding.UTF8, "application/json"),
                ct);

            response.EnsureSuccessStatusCode();

            var json    = await response.Content.ReadAsStringAsync(ct);
            var result  = ParseTavilyResponse(json, query);

            _logger.LogDebug("Tavily returned {Length} chars for query: {Query}", result.Length, query);

            // Optionally deepen the top result via Firecrawl
            var firecrawlEnabled = string.Equals(
                _config["Research:Firecrawl:Enabled"], "true",
                StringComparison.OrdinalIgnoreCase);

            if (firecrawlEnabled)
            {
                var topUrl = ExtractTopUrl(json);
                if (!string.IsNullOrWhiteSpace(topUrl))
                {
                    var fullText = await ScrapeViaFirecrawlAsync(topUrl, ct);
                    if (!string.IsNullOrWhiteSpace(fullText))
                    {
                        result = $"{result}\n\n--- Full text from top source ---\n{fullText}";
                    }
                }
            }

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tavily search failed for query: {Query}. Falling back to local model.", query);

            return await _ollama.Generate(
                $"Based on your existing knowledge, provide information about: {query}\n\n" +
                "Be concise and factual. If you do not know something, say so plainly.");
        }
    }

    private static string ParseTavilyResponse(string json, string query)
    {
        using var doc = JsonDocument.Parse(json);
        var root      = doc.RootElement;

        var builder = new StringBuilder();

        // Tavily's synthesized answer (when include_answer=true)
        if (root.TryGetProperty("answer", out var answer) &&
            !string.IsNullOrWhiteSpace(answer.GetString()))
        {
            builder.AppendLine(answer.GetString());
            builder.AppendLine();
        }

        // Individual result snippets
        if (root.TryGetProperty("results", out var results))
        {
            foreach (var item in results.EnumerateArray())
            {
                var title   = item.TryGetProperty("title",   out var t) ? t.GetString() : null;
                var url     = item.TryGetProperty("url",     out var u) ? u.GetString() : null;
                var content = item.TryGetProperty("content", out var c) ? c.GetString() : null;

                if (!string.IsNullOrWhiteSpace(content))
                {
                    if (!string.IsNullOrWhiteSpace(title))
                        builder.AppendLine($"[{title}]({url})");

                    builder.AppendLine(content);
                    builder.AppendLine();
                }
            }
        }

        return builder.Length > 0
            ? builder.ToString().Trim()
            : $"No web results found for: {query}";
    }

    private static string? ExtractTopUrl(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("results", out var results))
            {
                var first = results.EnumerateArray().FirstOrDefault();
                if (first.ValueKind == JsonValueKind.Object &&
                    first.TryGetProperty("url", out var url))
                {
                    return url.GetString();
                }
            }
        }
        catch { /* non-critical */ }

        return null;
    }

    // -------------------------------------------------------------------------
    // Firecrawl (optional full-text extraction)
    // -------------------------------------------------------------------------

    private async Task<string> ScrapeViaFirecrawlAsync(string url, CancellationToken ct)
    {
        var firecrawlKey = _config["Research:Firecrawl:ApiKey"];
        var baseUrl      = _config["Research:Firecrawl:BaseUrl"] ?? "https://api.firecrawl.dev";

        if (string.IsNullOrWhiteSpace(firecrawlKey))
        {
            _logger.LogDebug("Firecrawl enabled but ApiKey not set — skipping full-text extraction.");
            return string.Empty;
        }

        try
        {
            var client = _httpClientFactory.CreateClient("firecrawl");
            client.BaseAddress = new Uri(baseUrl);
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", firecrawlKey);

            var body = JsonSerializer.Serialize(new
            {
                url,
                formats = new[] { "markdown" }
            });

            var response = await client.PostAsync(
                "/v1/scrape",
                new StringContent(body, Encoding.UTF8, "application/json"),
                ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogDebug("Firecrawl returned {Status} for {Url}", response.StatusCode, url);
                return string.Empty;
            }

            var json = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty("markdown", out var markdown))
            {
                var text = markdown.GetString() ?? string.Empty;
                // Trim to a reasonable size to avoid flooding the context
                return text.Length > 4000 ? text[..4000] + "\n[truncated]" : text;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Firecrawl scrape failed for {Url} — non-critical", url);
        }

        return string.Empty;
    }
}
