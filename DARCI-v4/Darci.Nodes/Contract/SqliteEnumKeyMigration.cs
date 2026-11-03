#nullable enable

using System.Text;
using Microsoft.Data.Sqlite;

namespace Darci.Nodes;

/// <summary>
/// SU5 — the ADDITIVE schema migration from enum-ordinal columns to canonical STRING keys.
///
/// <para>Node ids and capabilities are persisted as INTEGER enum ordinals in several tables. That cannot
/// represent an external node's capability (there is no <see cref="Capability"/> member for
/// <c>acme.simulate_thermal</c>), so the string keys have to become the durable form. This migration is
/// deliberately NON-DESTRUCTIVE: it ADDS a <c>*_key</c> TEXT column beside each ordinal column, backfills it
/// from the existing ordinals, and dual-writes from then on. No column is dropped and no existing read path
/// changes, so an older build can still read the database and this sub-unit cannot lose data.</para>
///
/// <para>The backfill SQL is generated from <see cref="CapabilityKey"/> itself, so the stored strings can
/// never drift from the in-code mapping.</para>
/// </summary>
public static class SqliteEnumKeyMigration
{
    /// <summary>Add a column if it is not already present. SQLite has no ADD COLUMN IF NOT EXISTS.</summary>
    public static async Task<bool> EnsureColumnAsync(
        SqliteConnection conn, string table, string column, string typeDecl, CancellationToken ct = default)
    {
        if (await ColumnExistsAsync(conn, table, column, ct)) return false;

        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {typeDecl}";
        await alter.ExecuteNonQueryAsync(ct);
        return true;
    }

    public static async Task<bool> ColumnExistsAsync(
        SqliteConnection conn, string table, string column, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Backfill <paramref name="keyColumn"/> from the ordinal <paramref name="ordinalColumn"/> for rows that
    /// do not have it yet. Returns the number of rows updated.
    /// </summary>
    public static async Task<int> BackfillCapabilityKeysAsync(
        SqliteConnection conn, string table, string ordinalColumn, string keyColumn, CancellationToken ct = default)
        => await BackfillAsync(conn, table, ordinalColumn, keyColumn, CapabilityCaseSql(ordinalColumn), ct);

    public static async Task<int> BackfillNodeKeysAsync(
        SqliteConnection conn, string table, string ordinalColumn, string keyColumn, CancellationToken ct = default)
        => await BackfillAsync(conn, table, ordinalColumn, keyColumn, NodeCaseSql(ordinalColumn), ct);

    private static async Task<int> BackfillAsync(
        SqliteConnection conn, string table, string ordinalColumn, string keyColumn, string caseSql, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            $"UPDATE {table} SET {keyColumn} = {caseSql} " +
            $"WHERE {keyColumn} IS NULL AND {ordinalColumn} IS NOT NULL";
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>CASE expression mapping a <see cref="Capability"/> ordinal column to its canonical string.
    /// Generated from the enum + <see cref="CapabilityKey.From(Capability)"/> so it cannot drift.</summary>
    public static string CapabilityCaseSql(string ordinalColumn) =>
        CaseSql(ordinalColumn, Enum.GetValues<Capability>().Select(c => ((int)c, CapabilityKey.From(c))));

    /// <summary>CASE expression mapping a <see cref="NodeId"/> ordinal column to its canonical string.</summary>
    public static string NodeCaseSql(string ordinalColumn) =>
        CaseSql(ordinalColumn, Enum.GetValues<NodeId>().Select(n => ((int)n, CapabilityKey.From(n))));

    private static string CaseSql(string ordinalColumn, IEnumerable<(int Ordinal, string Key)> mappings)
    {
        var sb = new StringBuilder("CASE ").Append(ordinalColumn);
        foreach (var (ordinal, key) in mappings)
            sb.Append(" WHEN ").Append(ordinal).Append(" THEN '").Append(key.Replace("'", "''")).Append('\'');
        sb.Append(" ELSE NULL END");
        return sb.ToString();
    }
}
