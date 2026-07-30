#nullable enable

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>An audit row: what capability surface this core registered, when, and from which manifest.</summary>
public sealed record NodeRegistrationRecord
{
    public string NodeId { get; init; } = "";
    public string NodeVersion { get; init; } = "";
    public string ContractVersion { get; init; } = "";
    public string ManifestSha256 { get; init; } = "";
    public IReadOnlyList<string> Capabilities { get; init; } = Array.Empty<string>();
    public string? SourcePath { get; init; }
    public DateTime RegisteredAt { get; init; } = DateTime.UtcNow;

    /// <summary>True when this registration's manifest hash differs from the previously recorded one — i.e.
    /// the capability surface CHANGED. The §14c audit signal.</summary>
    public bool SurfaceChanged { get; init; }
}

/// <summary>
/// Records every node registration for audit (Phase E §14c): extending DARCI's capability surface is an
/// upward crossing, so it must be traceable to a human act. The human act is the reviewed manifest merged
/// into the repo; this store is the durable record that it happened, including the manifest SHA-256 so a
/// changed surface is detectable rather than assumed.
/// </summary>
public interface INodeRegistrationStore
{
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>Record a registration. Returns the record, with <see cref="NodeRegistrationRecord.SurfaceChanged"/>
    /// set when the manifest hash differs from the most recent one for this node.</summary>
    Task<NodeRegistrationRecord> RecordAsync(NodeRegistrationRecord record, CancellationToken ct = default);

    Task<IReadOnlyList<NodeRegistrationRecord>> GetHistoryAsync(string nodeId, CancellationToken ct = default);
    Task<IReadOnlyList<NodeRegistrationRecord>> GetLatestAsync(CancellationToken ct = default);
}

public sealed class SqliteNodeRegistrationStore : INodeRegistrationStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteNodeRegistrationStore> _logger;

    public SqliteNodeRegistrationStore(string connectionString, ILogger<SqliteNodeRegistrationStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS node_registrations (
                seq INTEGER PRIMARY KEY AUTOINCREMENT,
                node_id TEXT NOT NULL,
                node_version TEXT NOT NULL,
                contract_version TEXT NOT NULL,
                manifest_sha256 TEXT NOT NULL,
                capabilities_json TEXT NOT NULL,
                source_path TEXT NULL,
                registered_at TEXT NOT NULL,
                surface_changed INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS ix_node_registrations_node ON node_registrations(node_id, registered_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("Node registration audit store initialized.");
    }

    public async Task<NodeRegistrationRecord> RecordAsync(NodeRegistrationRecord record, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Compare against the most recent hash for this node to detect a changed capability surface.
        string? previousSha;
        await using (var prev = conn.CreateCommand())
        {
            prev.CommandText = "SELECT manifest_sha256 FROM node_registrations WHERE node_id = $id ORDER BY seq DESC LIMIT 1";
            prev.Parameters.AddWithValue("$id", record.NodeId);
            previousSha = (await prev.ExecuteScalarAsync(ct)) as string;
        }

        var changed = previousSha is not null && !string.Equals(previousSha, record.ManifestSha256, StringComparison.Ordinal);
        var toStore = record with { SurfaceChanged = changed };

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                INSERT INTO node_registrations
                    (node_id, node_version, contract_version, manifest_sha256, capabilities_json, source_path, registered_at, surface_changed)
                VALUES ($id, $ver, $cver, $sha, $caps, $src, $at, $changed)
                """;
            cmd.Parameters.AddWithValue("$id", toStore.NodeId);
            cmd.Parameters.AddWithValue("$ver", toStore.NodeVersion);
            cmd.Parameters.AddWithValue("$cver", toStore.ContractVersion);
            cmd.Parameters.AddWithValue("$sha", toStore.ManifestSha256);
            cmd.Parameters.AddWithValue("$caps", JsonSerializer.Serialize(toStore.Capabilities));
            cmd.Parameters.AddWithValue("$src", (object?)toStore.SourcePath ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$at", ToIso(toStore.RegisteredAt));
            cmd.Parameters.AddWithValue("$changed", changed ? 1 : 0);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (changed)
            _logger.LogWarning(
                "Capability surface CHANGED for node {NodeId}: manifest sha256 {Old} → {New}. " +
                "This should correspond to a reviewed manifest change (Phase E §14c).",
                toStore.NodeId, ShortHash(previousSha), ShortHash(toStore.ManifestSha256));

        return toStore;
    }

    /// <summary>First 16 chars of a hash for logging. Never throws — the audit path must not be the thing
    /// that crashes the app just because a hash was shorter than expected.</summary>
    private static string ShortHash(string? hash) =>
        string.IsNullOrEmpty(hash) ? "(none)" : hash.Length <= 16 ? hash : hash[..16];

    public async Task<IReadOnlyList<NodeRegistrationRecord>> GetHistoryAsync(string nodeId, CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM node_registrations WHERE node_id = $id ORDER BY seq";
        cmd.Parameters.AddWithValue("$id", nodeId);
        return await ReadAll(cmd, ct);
    }

    public async Task<IReadOnlyList<NodeRegistrationRecord>> GetLatestAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM node_registrations
            WHERE seq IN (SELECT MAX(seq) FROM node_registrations GROUP BY node_id)
            ORDER BY node_id
            """;
        return await ReadAll(cmd, ct);
    }

    private static async Task<IReadOnlyList<NodeRegistrationRecord>> ReadAll(SqliteCommand cmd, CancellationToken ct)
    {
        var list = new List<NodeRegistrationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var srcOrd = reader.GetOrdinal("source_path");
            list.Add(new NodeRegistrationRecord
            {
                NodeId = reader.GetString(reader.GetOrdinal("node_id")),
                NodeVersion = reader.GetString(reader.GetOrdinal("node_version")),
                ContractVersion = reader.GetString(reader.GetOrdinal("contract_version")),
                ManifestSha256 = reader.GetString(reader.GetOrdinal("manifest_sha256")),
                Capabilities = Deserialize(reader.GetString(reader.GetOrdinal("capabilities_json"))),
                SourcePath = reader.IsDBNull(srcOrd) ? null : reader.GetString(srcOrd),
                RegisteredAt = FromIso(reader.GetString(reader.GetOrdinal("registered_at"))),
                SurfaceChanged = reader.GetInt32(reader.GetOrdinal("surface_changed")) != 0,
            });
        }
        return list;
    }

    private static List<string> Deserialize(string json)
    {
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? new(); } catch { return new(); }
    }

    private static string ToIso(DateTime v) => v.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime FromIso(string v) => DateTime.Parse(v, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();
}
