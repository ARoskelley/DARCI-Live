#nullable enable

using Darci.Research.Agents.Models;

namespace Darci.Research.Agents;

public interface IDeepResearchOrchestrator
{
    Task<ResearchOutcome> RunDeepResearchAsync(string question, string userId, CancellationToken ct = default);
}

public interface IResearchToolbox
{
    Task<string> GenerateAsync(string prompt, CancellationToken ct = default);
    Task<List<float>> GetEmbeddingAsync(string text, CancellationToken ct = default);
    Task<string> SearchWebAsync(string query, CancellationToken ct = default);
}
