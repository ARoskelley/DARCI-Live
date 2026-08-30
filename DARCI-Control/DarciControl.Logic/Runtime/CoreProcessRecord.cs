#nullable enable

using System.Diagnostics;
using System.Text.Json;

namespace DarciControl.Logic.Runtime;

/// <summary>
/// What we remember about a core we launched, so a later run of the control centre can find it again.
///
/// <para><see cref="StartedAtUtc"/> is not decoration — it is the guard against PID reuse. An operating
/// system recycles process ids freely, so a stored pid alone is a licence to kill an unrelated program
/// that happens to have inherited the number. Matching the start time as well makes "is this still MY
/// core" answerable rather than assumed.</para>
/// </summary>
public sealed record CoreProcessRecord
{
    public required int Pid { get; init; }
    public required DateTime StartedAtUtc { get; init; }
    public required string ExecutablePath { get; init; }
    public required string StatusUrl { get; init; }

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Where the record lives: per-user, outside the repo, and stable across app restarts.</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DarciControl", "core-process.json");

    public void Save(string? path = null)
    {
        var target = path ?? DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, JsonSerializer.Serialize(this, Json));
    }

    public static void Clear(string? path = null)
    {
        var target = path ?? DefaultPath;
        try { if (File.Exists(target)) File.Delete(target); } catch { /* best effort */ }
    }

    public static CoreProcessRecord? Load(string? path = null)
    {
        var target = path ?? DefaultPath;
        try
        {
            return File.Exists(target)
                ? JsonSerializer.Deserialize<CoreProcessRecord>(File.ReadAllText(target), Json)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The live process this record describes, or null if it is gone — or if the pid now belongs to
    /// something else entirely.
    /// </summary>
    public Process? TryResolve()
    {
        try
        {
            var process = Process.GetProcessById(Pid);

            // Same pid is NOT the same process. Without this check, adopting a stale record could stop a
            // stranger's program that happened to be handed the recycled id.
            var started = process.StartTime.ToUniversalTime();
            return Math.Abs((started - StartedAtUtc).TotalSeconds) <= 2 ? process : null;
        }
        catch (Exception)
        {
            // ArgumentException when the pid is gone; Win32Exception when it is not ours to inspect.
            return null;
        }
    }
}
