#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Persists innovated-knowledge entries + their append-only ledger (SQLite). Confidence is always
/// clamped to provenance on write (<see cref="ProvenancePolicy"/>). The store ENFORCES the governing
/// invariant (§0a): an update that raises an entry's trust rank is rejected unless its ledger event is
/// human-authored. Also stores consumption links so outcomes can be matched + deduped by correlation root.
/// </summary>
public interface IInnovatedKnowledgeStore
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Insert a new entry (confidence clamped) and log the <see cref="LedgerEventKind.Created"/> event.</summary>
    Task AddAsync(InnovatedKnowledgeRecord record, CancellationToken ct = default);

    /// <summary>
    /// Update an entry, appending <paramref name="evt"/> to the ledger. Throws
    /// <see cref="InvalidOperationException"/> if the update raises trust rank with a non-human-authored
    /// event kind (the invariant guard). Confidence is re-clamped to the new provenance.
    /// </summary>
    Task UpdateAsync(InnovatedKnowledgeRecord record, LedgerEvent evt, CancellationToken ct = default);

    Task<InnovatedKnowledgeRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default);
    Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetByProvenanceAsync(Provenance provenance, int limit = 100, CancellationToken ct = default);

    Task<IReadOnlyList<InnovatedRevision>> GetRevisionsAsync(string entryId, CancellationToken ct = default);
    Task<bool> RevertToRevisionAsync(string entryId, int revisionSeq, CancellationToken ct = default);

    // ── Consumption links (the correlation-link fix) ──

    /// <summary>Record that an entry was served into work identified by <paramref name="correlationRoot"/>. Idempotent per root.</summary>
    Task RecordConsumptionAsync(string entryId, string correlationRoot, double weight = 1.0, string? campaignId = null, CancellationToken ct = default);

    /// <summary>Entries that consumed a hypothesis under this correlation root (how outcomes find their entries).</summary>
    Task<IReadOnlyList<InnovatedKnowledgeRecord>> GetEntriesByConsumptionRootAsync(string correlationRoot, CancellationToken ct = default);

    /// <summary>Resolve a pending consumption link. Returns true only if it was newly resolved (retries collapse → false).</summary>
    Task<bool> ResolveConsumptionAsync(string entryId, string correlationRoot, ConsumptionOutcome outcome, CancellationToken ct = default);

    /// <summary>Distinct-root success/failure counts for an entry (the deduped evidence tally).</summary>
    Task<(int Successes, int Failures)> CountDistinctOutcomesAsync(string entryId, CancellationToken ct = default);

    Task<IReadOnlyList<InnovatedConsumption>> GetConsumptionsAsync(string entryId, CancellationToken ct = default);
}
