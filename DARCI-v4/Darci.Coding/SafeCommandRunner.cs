#nullable enable

using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Darci.Coding;

public sealed class SafeCommandRunner : ISafeCommandRunner
{
    private const int DefaultTimeoutSeconds = 90;
    private const int MaxTimeoutSeconds = 600;
    private const int OutputTailChars = 12_000;

    private readonly ICodingWorkspaceStore _store;
    private readonly ILogger<SafeCommandRunner> _logger;

    public SafeCommandRunner(ICodingWorkspaceStore store, ILogger<SafeCommandRunner> logger)
    {
        _store = store;
        _logger = logger;
    }

    public Task<CodingCommandRun> RunAsync(string workspaceId, CodingCommandRequest request, CancellationToken ct = default) =>
        RunCoreAsync(workspaceId, "", request, ct);

    public Task<CodingCommandRun> RunForTaskAsync(string workspaceId, string taskId, CodingCommandRequest request, CancellationToken ct = default) =>
        RunCoreAsync(workspaceId, taskId, request, ct);

    private async Task<CodingCommandRun> RunCoreAsync(string workspaceId, string taskId, CodingCommandRequest request, CancellationToken ct)
    {
        var workspace = await _store.GetWorkspaceAsync(workspaceId, ct)
            ?? throw new InvalidOperationException($"Coding workspace not found: {workspaceId}");

        var started = DateTime.UtcNow;
        var args = request.Arguments ?? Array.Empty<string>();
        var workingDirectory = ResolveWorkingDirectory(workspace.RootPath, request.WorkingSubdirectory);
        var display = BuildDisplayCommand(request.Command, args);
        var allowed = IsAllowed(request.Command, args);

        if (!allowed)
        {
            var denied = new CodingCommandRun
            {
                WorkspaceId = workspace.Id,
                TaskId = taskId,
                WorkingDirectory = workingDirectory,
                Command = request.Command,
                Arguments = args,
                DisplayCommand = display,
                WasAllowed = false,
                ExitCode = null,
                Error = "Command is not in the coding allowlist.",
                StartedAt = started,
                CompletedAt = DateTime.UtcNow
            };
            await _store.AddCommandRunAsync(denied, ct);
            return denied;
        }

        var timeout = TimeSpan.FromSeconds(Math.Clamp(request.TimeoutSeconds ?? DefaultTimeoutSeconds, 1, MaxTimeoutSeconds));
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        var completed = DateTime.UtcNow;
        var timedOut = false;
        int? exitCode = null;
        string? error = null;

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = request.Command,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                process.StartInfo.ArgumentList.Add(arg);
            }

            process.OutputDataReceived += (_, e) => AppendLine(stdout, e.Data);
            process.ErrorDataReceived += (_, e) => AppendLine(stderr, e.Data);

            if (!process.Start())
            {
                error = "Process failed to start.";
            }
            else
            {
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var waitTask = process.WaitForExitAsync(ct);
                var timeoutTask = Task.Delay(timeout, ct);
                var finished = await Task.WhenAny(waitTask, timeoutTask);
                if (finished == timeoutTask)
                {
                    timedOut = true;
                    TryKill(process);
                }
                else
                {
                    await waitTask;
                    exitCode = process.ExitCode;
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            error = ex.Message;
            _logger.LogWarning(ex, "Safe coding command failed: {Command}", display);
        }
        finally
        {
            completed = DateTime.UtcNow;
        }

        var run = new CodingCommandRun
        {
            WorkspaceId = workspace.Id,
            TaskId = taskId,
            WorkingDirectory = workingDirectory,
            Command = request.Command,
            Arguments = args,
            DisplayCommand = display,
            ExitCode = exitCode,
            TimedOut = timedOut,
            WasAllowed = true,
            StdoutTail = Tail(stdout.ToString()),
            StderrTail = Tail(stderr.ToString()),
            Error = error,
            StartedAt = started,
            CompletedAt = completed
        };

        await _store.AddCommandRunAsync(run, ct);
        return run;
    }

    private static string ResolveWorkingDirectory(string rootPath, string? subdirectory)
    {
        var normalizedRoot = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var target = string.IsNullOrWhiteSpace(subdirectory)
            ? normalizedRoot
            : Path.GetFullPath(Path.Combine(normalizedRoot, subdirectory));

        if (!target.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(normalizedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && !target.StartsWith(normalizedRoot + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Working directory must stay inside the coding workspace root.");
        }

        if (!Directory.Exists(target))
        {
            throw new DirectoryNotFoundException($"Working directory does not exist: {target}");
        }

        return target;
    }

    private static bool IsAllowed(string command, string[] args)
    {
        var name = Path.GetFileName(command).ToLowerInvariant();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name switch
        {
            "dotnet" => args.Length >= 1 && IsOneOf(args[0], "restore", "build", "test"),
            "npm" or "npm.cmd" => IsAllowedNpm(args),
            "python" or "py" => IsAllowedPython(args),
            "pytest" => true,
            "cargo" => args.Length >= 1 && IsOneOf(args[0], "build", "check", "test"),
            "go" => args.Length >= 1 && IsOneOf(args[0], "build", "test"),
            "git" => IsAllowedGit(args),
            _ => false
        };
    }

    private static bool IsAllowedGit(string[] args)
    {
        if (args.Length == 0) return false;
        var sub = args[0].ToLowerInvariant();
        // Read-only
        if (IsOneOf(sub, "status", "diff", "log", "show", "rev-parse")) return true;
        // Stash (allowed without extra args or with "pop")
        if (sub == "stash")
        {
            return args.Length == 1 || (args.Length >= 2 && IsOneOf(args[1], "pop", "list", "show"));
        }
        // Commit with -am flag (checkpoint commits)
        if (sub == "commit")
        {
            return args.Any(a => a.StartsWith("-", StringComparison.Ordinal));
        }
        // Checkout for branch/file restore (restricted to HEAD)
        if (sub == "checkout") return true;
        // Reset --hard to HEAD or a full commit SHA (40 hex chars) from a stored checkpoint.
        if (sub == "reset" && args.Length >= 3 && IsOneOf(args[1], "--hard", "--soft", "--mixed"))
        {
            var target = args[2];
            return IsOneOf(target, "HEAD")
                || (target.Length >= 7 && target.All(c => char.IsAsciiHexDigit(c)));
        }
        // Add for staging
        if (sub == "add") return true;
        return false;
    }

    private static bool IsAllowedNpm(string[] args)
    {
        if (args.Length == 1 && IsOneOf(args[0], "test")) return true;
        if (args.Length >= 2 && IsOneOf(args[0], "run") && IsOneOf(args[1], "build", "test", "lint", "check")) return true;
        return false;
    }

    private static bool IsAllowedPython(string[] args)
    {
        return args.Length >= 2
            && IsOneOf(args[0], "-m")
            && IsOneOf(args[1], "pytest", "unittest", "py_compile");
    }

    private static bool IsOneOf(string value, params string[] allowed) =>
        allowed.Any(item => string.Equals(value, item, StringComparison.OrdinalIgnoreCase));

    private static string BuildDisplayCommand(string command, IEnumerable<string> args) =>
        string.Join(" ", new[] { command }.Concat(args.Select(QuoteIfNeeded)));

    private static string QuoteIfNeeded(string value) =>
        value.Contains(' ') ? $"\"{value.Replace("\"", "\\\"")}\"" : value;

    private static void AppendLine(StringBuilder builder, string? data)
    {
        if (data is null)
        {
            return;
        }

        builder.AppendLine(data);
        if (builder.Length > OutputTailChars * 2)
        {
            builder.Remove(0, builder.Length - OutputTailChars);
        }
    }

    private static string Tail(string value) =>
        value.Length <= OutputTailChars ? value : value[^OutputTailChars..];

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best-effort timeout cleanup.
        }
    }
}
