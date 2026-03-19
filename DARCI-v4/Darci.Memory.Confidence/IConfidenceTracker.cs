#nullable enable

using Darci.Memory.Confidence.Models;

namespace Darci.Memory.Confidence;

public interface IConfidenceTracker
{
    Task InitializeAsync(CancellationToken ct = default);

    Task<KnowledgeClaim> AddClaimAsync(
        string statement,
        string domain,
        string sourceType,
        string? sourceRef = null,
        float sourceQuality = 0.5f,
        string[]? entityIds = null,
        string[]? relationIds = null,
        CancellationToken ct = default);

    Task<KnowledgeClaim?> GetClaimAsync(string id, CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeClaim>> GetClaimsForEntityAsync(
        string entityId,
        int limit = 50,
        CancellationToken ct = default);

    Task<IReadOnlyList<KnowledgeClaim>> GetUncertainClaimsAsync(
        float threshold = 0.4f,
        string? domain = null,
        int limit = 30,
        CancellationToken ct = default);

    Task CorroborateAsync(
        string claimId,
        string sourceType,
        string? sourceRef = null,
        float sourcequality = 0.5f,
        CancellationToken ct = default);

    Task<Contradiction> RecordContradictionAsync(
        string claimAId,
        string claimBId,
        float severity,
        CancellationToken ct = default);

    Task<IReadOnlyList<Contradiction>> GetUnresolvedContradictionsAsync(
        string? domain = null,
        CancellationToken ct = default);

    Task ResolveContradictionAsync(
        string contradictionId,
        string resolution,
        CancellationToken ct = default);

    Task<SynthesisResult> SynthesizeAsync(
        string question,
        string? domain = null,
        Func<string, Task<List<float>>>? getEmbedding = null,
        CancellationToken ct = default);

    Task DecayAsync(CancellationToken ct = default);
}
