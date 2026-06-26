#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Persists packets and their append-only logs (decision 5: SQLite, parseable for later learning).
/// The store is the pollable surface (decision 1) and the retrieval surface for cross-run learning
/// (decision 3): packets are queryable by id, correlation, and state.
/// </summary>
public interface INodePacketStore
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Insert a new packet header plus all of its current log entries.</summary>
    Task CreatePacketAsync(NodePacket packet, CancellationToken ct = default);

    /// <summary>
    /// Update the packet header and append any log entries not yet persisted. Append-only: existing
    /// log rows are never modified; only entries beyond the stored count are inserted.
    /// </summary>
    Task SavePacketAsync(NodePacket packet, CancellationToken ct = default);

    /// <summary>Load a full packet (header + ordered log), or null if not found.</summary>
    Task<NodePacket?> GetPacketAsync(string packetId, CancellationToken ct = default);

    /// <summary>Pollable status projection without loading the full log.</summary>
    Task<NodePacketStatus?> GetStatusAsync(string packetId, CancellationToken ct = default);

    /// <summary>All packets sharing a correlation id (a parent and its spawned children).</summary>
    Task<IReadOnlyList<NodePacket>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default);

    /// <summary>Packets currently in any of the given states (poll for "what's running / done").</summary>
    Task<IReadOnlyList<NodePacket>> GetByStatesAsync(IReadOnlyList<NodeState> states, int limit = 100, CancellationToken ct = default);

    /// <summary>
    /// Active (non-terminal) packets whose lease has expired as of <paramref name="nowUtc"/>.
    /// Used by the watchdog to reap orphans.
    /// </summary>
    Task<IReadOnlyList<NodePacket>> GetActivePacketsWithExpiredLeaseAsync(DateTime nowUtc, CancellationToken ct = default);

    /// <summary>All active (non-terminal) packets, regardless of lease — used by the startup orphan sweep.</summary>
    Task<IReadOnlyList<NodePacket>> GetActivePacketsAsync(CancellationToken ct = default);
}
