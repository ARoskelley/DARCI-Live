#nullable enable

using Darci.Research.Agents;
using Darci.Tools.Ollama;
using Microsoft.Extensions.Logging;

namespace Darci.Tools;

/// <summary>
/// Lightweight research-facing toolbox used by the deep-research stack.
/// This stays separate from <see cref="Toolkit"/> so startup does not form
/// a DI cycle through the deep-research orchestrator.
/// </summary>
public sealed class ResearchToolbox : IResearchToolbox
{
    private readonly IOllamaClient _ollama;
    private readonly ILogger<ResearchToolbox> _logger;

    public ResearchToolbox(IOllamaClient ollama, ILogger<ResearchToolbox> logger)
    {
        _ollama = ollama;
        _logger = logger;
    }

    public Task<string> GenerateAsync(string prompt, CancellationToken ct = default)
        => _ollama.Generate(prompt);

    public Task<List<float>> GetEmbeddingAsync(string text, CancellationToken ct = default)
        => _ollama.GetEmbedding(text);

    public async Task<string> SearchWebAsync(string query, CancellationToken ct = default)
    {
        // DARCI currently uses local LLM knowledge as a safe fallback when no
        // external search provider is configured.
        _logger.LogWarning("Web search provider is not configured; using local model knowledge for: {Query}", query);

        var prompt = $"""
Based on your existing knowledge, provide information about: {query}

Be concise and factual. If you do not know something, say so plainly.
""";

        return await _ollama.Generate(prompt);
    }
}
