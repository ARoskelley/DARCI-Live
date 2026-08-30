#nullable enable

using System.Diagnostics;
using DarciControl.Logic.Packaging;
using DarciControl.Logic.Prerequisites;

namespace DarciControl.Logic.Runtime;

/// <summary>Outcome of trying to bring the core up.</summary>
public sealed record CoreStartResult(bool Ready, CoreProcessRecord? Record, string Detail);

/// <summary>
/// Starts and stops the DARCI core, and — the part that actually matters — refuses to leave one running
/// that nobody owns.
///
/// <para><b>Why the executable and not <c>dotnet run</c>.</b> <c>dotnet run</c> builds and then launches
/// the app as a CHILD process. Killing the <c>dotnet</c> parent does not reliably kill that child, so
/// every "stop" would leave a core still holding port 5081 and still writing to the database — the exact
/// orphaning this class exists to prevent. Running the built executable directly gives one process with
/// one pid, which is the whole reason lifecycle control is possible at all.</para>
///
/// <para><b>Reattach rather than trust.</b> The app can crash or be killed; the core it started would
/// survive. So the pid is written to disk with its start time, and a later run resolves that record back
/// to a live process — verifying identity, because pids get recycled — and offers to stop it. Combined
/// with stopping on graceful exit, a core outliving its owner becomes recoverable instead of invisible.</para>
/// </summary>
public sealed class CoreLauncher
{
    private readonly DarciPaths _paths;
    private readonly PrerequisiteChecker _checker;
    private readonly string? _recordPath;

    public CoreLauncher(DarciPaths paths, PrerequisiteChecker? checker = null, string? recordPath = null)
    {
        _paths = paths;
        _checker = checker ?? new PrerequisiteChecker(paths);
        _recordPath = recordPath;
    }

    /// <summary>
    /// The core executable for this OS, or null when the repo has not been built yet.
    ///
    /// <para>Looks in the packaged layout first, then a repo build. Release before Debug: if both exist,
    /// the release build is the one a person means.</para>
    /// </summary>
    public string? FindCoreExecutable()
    {
        var exeName = TargetPlatform.Host.ExecutableName;

        var candidates = new[]
        {
            Path.Combine(_paths.Root, "core", exeName),                                          // unzipped install
            Path.Combine(_paths.Root, "DARCI-v4", "Darci.Api", "bin", "Release", "net8.0", exeName),
            Path.Combine(_paths.Root, "DARCI-v4", "Darci.Api", "bin", "Debug", "net8.0", exeName),
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>A core this app previously started that is STILL RUNNING — an orphan to adopt.</summary>
    public CoreProcessRecord? FindAdoptable()
    {
        var record = CoreProcessRecord.Load(_recordPath);
        if (record is null) return null;

        using var process = record.TryResolve();
        if (process is not null) return record;

        // The record outlived the process; clear it so it cannot mislead later.
        CoreProcessRecord.Clear(_recordPath);
        return null;
    }

    public async Task<CoreStartResult> StartAsync(
        string apiUrl = "http://localhost:5081",
        TimeSpan? readinessTimeout = null,
        Action<string>? onOutput = null,
        CancellationToken ct = default)
    {
        var statusUrl = $"{apiUrl.TrimEnd('/')}/status";

        // Already up — whether we started it or not. Starting a second one would fight for the port and
        // the database, so say so instead.
        if (await _checker.CheckCoreAsync(statusUrl, ct) is { State: PrereqState.Ok })
            return new CoreStartResult(true, FindAdoptable(), "The core was already running.");

        var exe = FindCoreExecutable();
        if (exe is null)
        {
            return new CoreStartResult(false, null,
                "No built core was found. Build the solution first (dotnet build DARCI-v4/DARCI.sln).");
        }

        var startInfo = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!,
        };
        startInfo.ArgumentList.Add("--urls");
        startInfo.ArgumentList.Add(apiUrl);

        // Point the core at this checkout's nodes and profile, so the control centre and the core never
        // disagree about which DARCI is being run.
        if (Directory.Exists(_paths.NodesPath)) startInfo.Environment["DARCI_NODES_PATH"] = _paths.NodesPath;
        if (File.Exists(_paths.HostProfilePath)) startInfo.Environment["DARCI_HOST_PROFILE"] = _paths.HostProfilePath;

        Process process;
        try
        {
            process = Process.Start(startInfo)!;
        }
        catch (Exception ex)
        {
            return new CoreStartResult(false, null, $"Could not start the core: {ex.GetBaseException().Message}");
        }

        if (onOutput is not null)
        {
            process.OutputDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            process.ErrorDataReceived += (_, e) => { if (e.Data is not null) onOutput(e.Data); };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }

        // Record BEFORE waiting for readiness. A core that starts and then hangs is exactly the case where
        // an untracked pid becomes an orphan nobody can find.
        var record = new CoreProcessRecord
        {
            Pid = process.Id,
            StartedAtUtc = process.StartTime.ToUniversalTime(),
            ExecutablePath = exe,
            StatusUrl = statusUrl,
        };
        record.Save(_recordPath);

        var ready = await _checker.WaitForCoreAsync(
            statusUrl, readinessTimeout ?? TimeSpan.FromMinutes(3), ct);

        if (ready) return new CoreStartResult(true, record, "The core is ready.");

        // Do not leave a half-started process behind just because we gave up waiting.
        await StopAsync(ct);
        return new CoreStartResult(false, null,
            "The core did not become ready in time and was stopped. Check the log for a startup error.");
    }

    /// <summary>
    /// Stop the core we started. Safe to call when there is nothing to stop.
    ///
    /// <para>Asks first, then insists: <see cref="Process.CloseMainWindow"/> is useless for a console
    /// host, so this gives the process a moment to exit on its own and kills it if it will not. Losing a
    /// little graceful shutdown is better than leaving the port held.</para>
    /// </summary>
    public async Task<bool> StopAsync(CancellationToken ct = default)
    {
        var record = CoreProcessRecord.Load(_recordPath);
        if (record is null) return false;

        using var process = record.TryResolve();
        if (process is null)
        {
            CoreProcessRecord.Clear(_recordPath);
            return false;
        }

        try
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync(ct);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
        finally
        {
            CoreProcessRecord.Clear(_recordPath);
        }
    }

    /// <summary>
    /// Best-effort stop for process exit. Synchronous and swallowing, because it runs from a shutdown
    /// handler where throwing achieves nothing and blocking forever is worse than a missed cleanup.
    /// </summary>
    public void StopOnExit()
    {
        try
        {
            StopAsync().Wait(TimeSpan.FromSeconds(10));
        }
        catch (Exception)
        {
            // Nothing useful to do while the process is going away.
        }
    }
}
