#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Persists knowledge-gap records (SQLite). Retrievable by status (for the living loop and learning
/// passes) and by correlation (to trace a gap back to the work that produced it).
/// </summary>
public interface IGapStore
{
    Task InitializeAsync(CancellationToken ct = default);
    Task AddAsync(GapRecord gap, CancellationToken ct = default);
    Task UpdateAsync(GapRecord gap, CancellationToken ct = default);
    Task<GapRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<GapRecord>> GetByStatusAsync(string status, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<GapRecord>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default);
}

/// <summary>
/// Turns a deferred gap into a living-loop goal. Defined here (Darci.Nodes) so the gap handler stays
/// goal-store agnostic; implemented where IGoalManager lives. Returns the created goal id, or null.
/// </summary>
public interface IGapGoalSink
{
    Task<string?> CreateGoalForGapAsync(GapRecord gap, CancellationToken ct = default);
}
