#nullable enable

using System.IO.Compression;
using DarciControl.Logic.Nodes;
using DarciControl.Logic.Packaging;

namespace DarciControl.Logic.Tests;

/// <summary>
/// Building a distributable for the OTHER operating system.
///
/// <para><b>These tests carry more weight than usual, and it is worth being explicit about why.</b> The
/// Windows target is verified end-to-end elsewhere — a real zip is built, extracted, and booted. The Linux
/// target cannot be: this machine runs Windows. So the Linux path is checked structurally here, and its
/// first real run belongs to whoever boots Linux. Nothing below should be read as "the Linux zip works" —
/// only as "the Linux zip is shaped correctly".</para>
/// </summary>
public sealed class CrossPlatformPackagingTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _core;

    public CrossPlatformPackagingTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"darci-xplat-{Guid.NewGuid():N}");
        _repo = Path.Combine(_root, "repo");
        _core = Path.Combine(_root, "published");

        Directory.CreateDirectory(Path.Combine(_repo, "DARCI-v4"));
        Directory.CreateDirectory(_core);
        File.WriteAllText(Path.Combine(_core, "Darci.Api"), "elf");
        File.WriteAllText(Path.Combine(_repo, "DARCI-v4", "host-profile.json"), """
            {"profile_id":"t","providers":{"ollama":{"kind":"ollama","base_url":"http://localhost:11434"}},
             "classes":{"embed.text":{"provider":"ollama","model":"nomic-embed-text"}}}
            """);
        File.WriteAllText(Path.Combine(_repo, "Get-DarciRequiredModels.ps1"), "# resolver");
        File.WriteAllText(Path.Combine(_repo, ".env.local.example"), "DARCI_NEO4J_PASSWORD=\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ZipBuildRequest Request(TargetPlatform platform) => new()
    {
        RepoRoot = _repo,
        OutputPath = Path.Combine(_root, "darci.zip"),
        Platform = platform,
    };

    private List<string> BuildAndList(TargetPlatform platform)
    {
        var request = Request(platform);
        var plan = ZipPlan.Create(request, Array.Empty<NodeCatalogEntry>(), _core);
        var readme = ZipAssembler.BuildReadme(request, plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));
        var result = ZipAssembler.Write(plan, request.OutputPath, readme, platform);
        Assert.True(result.Success, result.Error);

        using var archive = ZipFile.OpenRead(request.OutputPath);
        return archive.Entries.Select(e => e.FullName).ToList();
    }

    // ── the RID is a parameter, not a constant ──

    [Fact]
    public void TheRuntimeIdentifier_FollowsTheTargetPlatform()
    {
        // Hardcoding win-x64 would produce a Linux zip containing Windows binaries — a package that looks
        // right and cannot run.
        Assert.Equal("win-x64", Request(TargetPlatform.Windows).Runtime);
        Assert.Equal("linux-x64", Request(TargetPlatform.Linux).Runtime);
    }

    [Fact]
    public void TheDefaultTarget_IsTheHostOs()
    {
        // Packaging for the machine you are on is the common case; cross-building is deliberate.
        Assert.Equal(TargetPlatform.Host.Rid, new ZipBuildRequest
        {
            RepoRoot = _repo,
            OutputPath = "x.zip",
        }.Runtime);
    }

    // ── the launcher matches the target ──

    [Fact]
    public void ALinuxZip_ShipsAShellLauncher_NotAPowerShellOne()
    {
        var entries = BuildAndList(TargetPlatform.Linux);

        Assert.Contains("darci/start-darci.sh", entries);
        Assert.DoesNotContain("darci/Start-DARCI.ps1", entries);
    }

    [Fact]
    public void AWindowsZip_ShipsAPowerShellLauncher_NotAShellOne()
    {
        var entries = BuildAndList(TargetPlatform.Windows);

        Assert.Contains("darci/Start-DARCI.ps1", entries);
        Assert.DoesNotContain("darci/start-darci.sh", entries);
    }

    [Fact]
    public void TheLinuxLauncher_RunsTheExtensionlessExecutable()
    {
        var script = PackagedStartScript.Build(TargetPlatform.Linux);

        Assert.Contains("core/Darci.Api", script);
        Assert.DoesNotContain(".exe", script);
        Assert.DoesNotContain(@"core\", script);   // no Windows separators
        Assert.StartsWith("#!/usr/bin/env bash", script);
    }

    [Fact]
    public void TheLinuxLauncher_SetsTheSameEnvironmentTheWindowsOneDoes()
    {
        // Same contract, different syntax: point the core at what shipped beside it.
        var script = PackagedStartScript.Build(TargetPlatform.Linux);

        Assert.Contains("DARCI_NODES_PATH", script);
        Assert.Contains("DARCI_HOST_PROFILE", script);
        Assert.Contains("DARCI_DB_PATH", script);
    }

    [Fact]
    public void TheLinuxLauncher_TreatsOllamaAsAWarningNotAFailure()
    {
        var script = PackagedStartScript.Build(TargetPlatform.Linux);

        Assert.Contains("WARNING", script);
        // `set -e` would abort the launcher the moment a prerequisite probe failed, turning a degraded
        // start into no start at all.
        Assert.DoesNotContain("set -e\n", script);
        Assert.DoesNotContain("set -eu", script);
    }

    [Fact]
    public void TheLinuxLauncher_DerivesModelsFromTheProfile_NotAHardcodedList()
    {
        var script = PackagedStartScript.Build(TargetPlatform.Linux);

        Assert.Contains("DARCI_HOST_PROFILE", script);
        Assert.DoesNotContain("gemma", script);
        Assert.DoesNotContain("nomic-embed-text", script);
    }

    // ── the details a zip silently loses ──

    [Fact]
    public void TheShellLauncher_ShipsWithItsExecutableBitSet()
    {
        // A zip carries Unix permissions only if they are set explicitly, and a .sh without +x fails with
        // a bare "Permission denied" that tells the recipient nothing about what to do.
        var platform = TargetPlatform.Linux;
        var request = Request(platform);
        var plan = ZipPlan.Create(request, Array.Empty<NodeCatalogEntry>(), _core);
        ZipAssembler.Write(plan, request.OutputPath, "readme", platform);

        using var archive = ZipFile.OpenRead(request.OutputPath);
        var entry = archive.GetEntry("darci/start-darci.sh")!;

        // Owner-execute bit within the Unix mode held in the high 16 bits.
        var mode = (entry.ExternalAttributes >> 16) & 0xFFF;
        Assert.True((mode & 0b001_000_000) != 0, $"expected the executable bit, got mode {Convert.ToString(mode, 8)}");
    }

    [Fact]
    public void TheShellLauncher_HasNoByteOrderMark()
    {
        // A .ps1 needs a BOM for Windows PowerShell; a .sh must NOT have one, because bash reads it as
        // part of the shebang and refuses to run the script.
        var platform = TargetPlatform.Linux;
        var request = Request(platform);
        var plan = ZipPlan.Create(request, Array.Empty<NodeCatalogEntry>(), _core);
        ZipAssembler.Write(plan, request.OutputPath, "readme", platform);

        using var archive = ZipFile.OpenRead(request.OutputPath);
        using var stream = archive.GetEntry("darci/start-darci.sh")!.Open();
        var head = new byte[3];
        stream.ReadExactly(head, 0, 3);

        Assert.NotEqual(new byte[] { 0xEF, 0xBB, 0xBF }, head);
    }

    // ── the README follows the target too ──

    [Fact]
    public void TheReadme_TellsALinuxRecipientToRunTheShellScript()
    {
        var request = Request(TargetPlatform.Linux);
        var plan = ZipPlan.Create(request, Array.Empty<NodeCatalogEntry>(), _core);
        var readme = ZipAssembler.BuildReadme(request, plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));

        Assert.Contains("./start-darci.sh", readme);
        Assert.Contains("chmod +x", readme);
        Assert.DoesNotContain(@".\Start-DARCI.ps1", readme);
    }

    [Fact]
    public void TheReadme_TellsAWindowsRecipientToRunThePowerShellScript()
    {
        var request = Request(TargetPlatform.Windows);
        var plan = ZipPlan.Create(request, Array.Empty<NodeCatalogEntry>(), _core);
        var readme = ZipAssembler.BuildReadme(request, plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));

        Assert.Contains(@".\Start-DARCI.ps1", readme);
        Assert.DoesNotContain("chmod +x", readme);
    }
}
