#nullable enable

using Darci.Research.Agents.Models;

namespace Darci.Research.Agents;

public interface IDeepResearchOrchestrator
{
    /// <summary>
    /// Legacy entry point — equivalent to calling
    /// RunResearchAsync with LearnAndSynthesize mode.
    /// Kept for API endpoint compatibility.
    /// </summary>
    Task<ResearchOutcome> RunDeepResearchAsync(
        string question, string userId, CancellationToken ct = default);

    /// <summary>
    /// Mode-aware entry point used by all internal callers.
    /// LearnOnly    = update graph, no Ollama synthesis.
    /// LearnAndSynthesize = update graph + produce Ollama reply.
    /// </summary>
    Task<ResearchOutcome> RunResearchAsync(
        string question,
        string userId,
        ResearchOrchestrationMode mode,
        CancellationToken ct = default);
}

public interface IResearchToolbox
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    Task<List<float>> GetEmbeddingAsync(string text, CancellationToken ct = default);
    Task<string> SearchWebAsync(string query, CancellationToken ct = default);
}
