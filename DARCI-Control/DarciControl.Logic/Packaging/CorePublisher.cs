#nullable enable

using System.Diagnostics;

namespace DarciControl.Logic.Packaging;

/// <summary>Result of a self-contained publish.</summary>
public sealed record PublishResult(bool Success, string OutputDirectory, string Log, string? Error = null);

/// <summary>
/// Runs <c>dotnet publish</c> to produce the self-contained core the zip carries.
///
/// <para>Isolated from the rest of packaging because it is the one genuinely slow, environment-dependent
/// step: minutes to run, needs the SDK, and cannot be meaningfully unit-tested. Everything that decides
/// what a zip CONTAINS lives in <see cref="ZipPlan"/> where it can be tested in milliseconds; this is
/// verified end-to-end instead, by building a zip and booting what comes out.</para>
/// </summary>
public sealed class CorePublisher
{
    private readonly Action<string>? _onOutput;

    public CorePublisher(Action<string>? onOutput = null) => _onOutput = onOutput;

    public async Task<PublishResult> PublishAsync(
        string repoRoot, string outputDirectory, string runtime = "win-x64", CancellationToken ct = default)
    {
        var project = Path.Combine(repoRoot, "DARCI-v4", "Darci.Api", "Darci.Api.csproj");
        if (!File.Exists(project))
            return new PublishResult(false, outputDirectory, "", $"Could not find {project}.");

        // Self-contained so the recipient needs no .NET SDK — the single biggest barrier the earlier
        // handoff stalled on. Not trimmed: trimming an app this reflective invites runtime surprises that
        // would surface on someone else's machine, which is the worst place to find them.
        var args = $"publish \"{project}\" -c Release -r {runtime} --self-contained true " +
                   $"-p:PublishSingleFile=false -o \"{outputDirectory}\"";

        var log = new System.Text.StringBuilder();

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("dotnet", args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = repoRoot,
            },
            EnableRaisingEvents = true,
        };

        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);

        void Capture(string? line)
        {
            if (line is null) return;
            log.AppendLine(line);
            _onOutput?.Invoke(line);
        }

        try
        {
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await process.WaitForExitAsync(ct);

            return process.ExitCode == 0
                ? new PublishResult(true, outputDirectory, log.ToString())
                : new PublishResult(false, outputDirectory, log.ToString(), $"dotnet publish exited {process.ExitCode}.");
        }
        catch (Exception ex)
        {
            return new PublishResult(false, outputDirectory, log.ToString(), ex.GetBaseException().Message);
        }
    }
}
