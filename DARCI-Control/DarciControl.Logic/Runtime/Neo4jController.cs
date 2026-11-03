#nullable enable

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DarciControl.Logic.Runtime;

/// <summary>
/// Finds and starts a local Neo4j, when the user wants one.
///
/// <para><b>Entirely optional, by design.</b> The core runs the knowledge graph on SQLite and — since the
/// configured-but-unreachable fix — falls back to it with a warning rather than failing to start. So
/// nothing here is on the critical path: if Neo4j is absent, unfindable, or refuses to start, DARCI still
/// runs. This exists to save a trip to a terminal, not to gate anything.</para>
///
/// <para>The platform split is the launcher name: <c>neo4j.bat</c> on Windows, <c>neo4j</c> on Linux.
/// <b>The Linux path is unverified</b> — written correctly, never run, because this machine is Windows.</para>
/// </summary>
public sealed class Neo4jController
{
    private static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);

    /// <summary>The launcher's filename on this OS.</summary>
    public static string LauncherName => IsWindows ? "neo4j.bat" : "neo4j";

    /// <summary>
    /// Locate the launcher: NEO4J_HOME first, then PATH, then the conventional install roots. Returns
    /// null when Neo4j simply is not installed, which is an ordinary answer rather than a problem.
    /// </summary>
    public static string? FindLauncher()
    {
        var home = Environment.GetEnvironmentVariable("NEO4J_HOME");
        if (!string.IsNullOrWhiteSpace(home))
        {
            var candidate = Path.Combine(home, "bin", LauncherName);
            if (File.Exists(candidate)) return candidate;
        }

        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "")
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), LauncherName);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // A malformed PATH entry is not worth failing the search over.
            }
        }

        return ConventionalRoots()
            .Where(Directory.Exists)
            .SelectMany(root =>
            {
                try { return Directory.EnumerateDirectories(root, "neo4j*"); }
                catch (Exception) { return Enumerable.Empty<string>(); }
            })
            .Select(dir => Path.Combine(dir, "bin", LauncherName))
            .FirstOrDefault(File.Exists);
    }

    private static IEnumerable<string> ConventionalRoots() => IsWindows
        ? new[] { @"C:\neo4j", @"C:\Program Files\neo4j" }
        : new[] { "/opt", "/usr/local", "/usr/share" };

    /// <summary>
    /// Start Neo4j in console mode and return once bolt answers, or false on timeout.
    ///
    /// <para>Console rather than a service: installing or touching a system service is a bigger,
    /// longer-lived change than a launcher button should be making on someone's machine without asking.</para>
    /// </summary>
    public static async Task<bool> StartAsync(TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var launcher = FindLauncher();
        if (launcher is null) return false;

        try
        {
            var startInfo = new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            startInfo.ArgumentList.Add("console");

            // Neo4j needs a JVM; if JAVA_HOME is unset it will say so on its own, more clearly than a
            // guess from here would.
            Process.Start(startInfo);
        }
        catch (Exception)
        {
            return false;
        }

        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(90));
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await IsListeningAsync(ct)) return true;
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }

        return false;
    }

    /// <summary>
    /// Is something answering on the bolt port? THE one probe — <see cref="Prerequisites.PrerequisiteChecker"/>
    /// calls this rather than keeping its own, because two probes with different timeouts is how the same
    /// screen ends up saying "Neo4j is not running" and "Neo4j is listening" at once. (It did.)
    ///
    /// <para>Connects to 127.0.0.1 rather than "localhost": that name commonly resolves to ::1 first, and
    /// a Neo4j bound to IPv4 only is then reported as absent by whichever probe times out before falling
    /// through. The core does the authoritative connect-and-authenticate; this only answers "is something
    /// there", so the UI can say which store will be used.</para>
    /// </summary>
    public static async Task<bool> IsListeningAsync(CancellationToken ct = default)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(3));
            await client.ConnectAsync(System.Net.IPAddress.Loopback, 7687, cts.Token);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
