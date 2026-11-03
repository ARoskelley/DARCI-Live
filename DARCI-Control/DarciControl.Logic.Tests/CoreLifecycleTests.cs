#nullable enable

using System.Diagnostics;
using DarciControl.Logic.Runtime;

namespace DarciControl.Logic.Tests;

/// <summary>
/// Core process lifecycle — the part that must not go wrong, because both failure modes are bad in a way
/// the user cannot see: an orphaned core keeps holding port 5081 and writing to the database with nobody
/// owning it, and a careless stop can kill an unrelated program that inherited a recycled pid.
/// </summary>
public sealed class CoreLifecycleTests : IDisposable
{
    private readonly string _recordPath;

    public CoreLifecycleTests()
    {
        _recordPath = Path.Combine(Path.GetTempPath(), $"darci-core-record-{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { if (File.Exists(_recordPath)) File.Delete(_recordPath); } catch { /* best effort */ }
    }

    private static CoreProcessRecord ForCurrentProcess(DateTime? startedAt = null)
    {
        using var self = Process.GetCurrentProcess();
        return new CoreProcessRecord
        {
            Pid = self.Id,
            StartedAtUtc = startedAt ?? self.StartTime.ToUniversalTime(),
            ExecutablePath = "irrelevant",
            StatusUrl = "http://localhost:5081/status",
        };
    }

    [Fact]
    public void ARecordRoundTripsThroughDisk()
    {
        var record = ForCurrentProcess();
        record.Save(_recordPath);

        var loaded = CoreProcessRecord.Load(_recordPath);

        Assert.NotNull(loaded);
        Assert.Equal(record.Pid, loaded!.Pid);
        Assert.Equal(record.StatusUrl, loaded.StatusUrl);
    }

    [Fact]
    public void ARecordResolves_WhenThePidAndStartTimeBothMatch()
    {
        // The current process is a convenient stand-in for a live core.
        using var resolved = ForCurrentProcess().TryResolve();
        Assert.NotNull(resolved);
    }

    [Fact]
    public void ARecordDoesNotResolve_WhenTheStartTimeDisagrees()
    {
        // THE IMPORTANT ONE. Operating systems recycle pids, so a stored pid alone is a licence to kill
        // whatever inherited the number. Matching the start time is what makes "is this still my core"
        // answerable instead of assumed — without it, Stop could terminate a stranger's program.
        var impostor = ForCurrentProcess(startedAt: DateTime.UtcNow.AddHours(-5));

        Assert.Null(impostor.TryResolve());
    }

    [Fact]
    public void ARecordDoesNotResolve_WhenTheProcessIsGone()
    {
        var record = new CoreProcessRecord
        {
            Pid = 999_999,   // implausible, and gone if it ever existed
            StartedAtUtc = DateTime.UtcNow,
            ExecutablePath = "gone",
            StatusUrl = "http://localhost:5081/status",
        };

        Assert.Null(record.TryResolve());
    }

    [Fact]
    public void MissingOrCorruptRecords_LoadAsNull_RatherThanThrowing()
    {
        // A launcher that throws on a damaged state file would be unable to start at all — the worst
        // possible reaction to a file it wrote itself.
        Assert.Null(CoreProcessRecord.Load(Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}.json")));

        File.WriteAllText(_recordPath, "{ not json");
        Assert.Null(CoreProcessRecord.Load(_recordPath));
    }

    [Fact]
    public void AStaleRecord_IsNotAdoptable_AndIsClearedAway()
    {
        // A record that outlived its process must not keep offering a Stop that would do nothing, or
        // worse, act on a recycled pid later.
        new CoreProcessRecord
        {
            Pid = 999_999,
            StartedAtUtc = DateTime.UtcNow,
            ExecutablePath = "gone",
            StatusUrl = "http://localhost:5081/status",
        }.Save(_recordPath);

        var launcher = new CoreLauncher(
            new Logic.Prerequisites.DarciPaths { Root = Path.GetTempPath() }, recordPath: _recordPath);

        Assert.Null(launcher.FindAdoptable());
        Assert.False(File.Exists(_recordPath));
    }

    [Fact]
    public async Task StoppingWithNothingRecorded_IsHarmless()
    {
        var launcher = new CoreLauncher(
            new Logic.Prerequisites.DarciPaths { Root = Path.GetTempPath() }, recordPath: _recordPath);

        Assert.False(await launcher.StopAsync());
    }

    [Fact]
    public void TheCoreExecutable_IsFoundInTheRepoBuild()
    {
        // Launching the built executable rather than `dotnet run` is what makes lifecycle control possible:
        // `dotnet run` starts the app as a CHILD, so killing it leaves the real core holding the port.
        var launcher = new CoreLauncher(
            new Logic.Prerequisites.DarciPaths { Root = RequiredModelsTests.FindRepoRoot() });

        var exe = launcher.FindCoreExecutable();

        Assert.NotNull(exe);
        Assert.EndsWith(Logic.Packaging.TargetPlatform.Host.ExecutableName, exe);
    }

    [Fact]
    public void TheNeo4jLauncherName_FollowsTheOs()
    {
        // neo4j.bat on Windows, neo4j on Linux. The Linux branch is written but UNVERIFIED here.
        var expected = System.Runtime.InteropServices.RuntimeInformation
            .IsOSPlatform(System.Runtime.InteropServices.OSPlatform.Windows) ? "neo4j.bat" : "neo4j";

        Assert.Equal(expected, Neo4jController.LauncherName);
    }

    [Fact]
    public void FindingNeo4j_DoesNotThrow_WhenItIsNotInstalled()
    {
        // Neo4j is entirely optional - the core falls back to SQLite - so absence is an ordinary answer.
        var exception = Record.Exception(() => Neo4jController.FindLauncher());
        Assert.Null(exception);
    }
}
