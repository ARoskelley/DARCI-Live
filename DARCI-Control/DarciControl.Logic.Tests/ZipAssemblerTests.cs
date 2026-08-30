#nullable enable

using System.IO.Compression;
using DarciControl.Logic.Nodes;
using DarciControl.Logic.Packaging;

namespace DarciControl.Logic.Tests;

/// <summary>
/// Writing the zip. These build a REAL archive and read it back, because the failures worth catching here
/// are about what physically ends up in the file — an excluded folder that slipped in, a secret that
/// survived a directory copy, a README that never got written.
/// </summary>
public sealed class ZipAssemblerTests : IDisposable
{
    private readonly string _root;
    private readonly string _repo;
    private readonly string _core;

    public ZipAssemblerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"darci-zipasm-{Guid.NewGuid():N}");
        _repo = Path.Combine(_root, "repo");
        _core = Path.Combine(_root, "published");

        Directory.CreateDirectory(Path.Combine(_repo, "DARCI-v4"));
        Directory.CreateDirectory(_core);

        // A published core, complete with the build noise a real publish leaves behind.
        File.WriteAllText(Path.Combine(_core, "Darci.Api.exe"), "binary");
        File.WriteAllText(Path.Combine(_core, "appsettings.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_core, "obj"));
        File.WriteAllText(Path.Combine(_core, "obj", "junk.cache"), "noise");
        Directory.CreateDirectory(Path.Combine(_core, "Data"));
        File.WriteAllText(Path.Combine(_core, "Data", "darci.db"), "someone else's data");

        File.WriteAllText(Path.Combine(_repo, "DARCI-v4", "host-profile.json"), """
            {"profile_id":"t","providers":{"ollama":{"kind":"ollama","base_url":"http://localhost:11434"}},
             "classes":{"embed.text":{"provider":"ollama","model":"nomic-embed-text"}}}
            """);
        File.WriteAllText(Path.Combine(_repo, ".env.local.example"), "DARCI_NEO4J_PASSWORD=\n");
        File.WriteAllText(Path.Combine(_repo, "Get-DarciRequiredModels.ps1"), "# resolver");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    private ZipBuildRequest Request(params string[] nodes) => new()
    {
        RepoRoot = _repo,
        OutputPath = Path.Combine(_root, "darci.zip"),
        SelectedNodeIds = nodes,
    };

    private (ZipBuildResult Result, List<string> Entries) BuildAndRead(ZipBuildRequest request, IReadOnlyList<NodeCatalogEntry> catalog)
    {
        var plan = ZipPlan.Create(request, catalog, _core);
        var readme = ZipAssembler.BuildReadme(request, plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));
        var result = ZipAssembler.Write(plan, request.OutputPath, readme);

        var entries = new List<string>();
        if (result.Success)
        {
            using var archive = ZipFile.OpenRead(request.OutputPath);
            entries.AddRange(archive.Entries.Select(e => e.FullName));
        }

        return (result, entries);
    }

    [Fact]
    public void ProducesAZipContainingTheCoreAndAReadme()
    {
        var (result, entries) = BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());

        Assert.True(result.Success, result.Error);
        Assert.True(result.Bytes > 0);
        Assert.Contains("darci/core/Darci.Api.exe", entries);
        Assert.Contains("darci/README.md", entries);
        Assert.Contains("darci/host-profile.json", entries);
        Assert.Contains("darci/Start-DARCI.ps1", entries);
    }

    [Fact]
    public void ExcludesBuildNoiseAndOtherPeoplesData()
    {
        // Shipping the developer's own darci.db would hand a stranger this machine's memories.
        var (_, entries) = BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());

        Assert.DoesNotContain(entries, e => e.Contains("/obj/"));
        Assert.DoesNotContain(entries, e => e.Contains("/Data/"));
    }

    [Fact]
    public void ASecretInsideACopiedDirectory_DoesNotSurvive()
    {
        // The plan refuses a secret it can see; this is the second net, for one hiding inside a folder
        // that is copied wholesale.
        File.WriteAllText(Path.Combine(_core, ".env.local"), "DARCI_NEO4J_PASSWORD=hunter2");

        var (result, entries) = BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());

        Assert.True(result.Success, result.Error);
        Assert.DoesNotContain(entries, e => e.EndsWith(".env.local", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void IncludesOnlyTheSelectedNodesFolders()
    {
        var coding = Path.Combine(_repo, "DARCI-v4", "nodes", "darci.coding");
        var knowledge = Path.Combine(_repo, "DARCI-v4", "nodes", "darci.knowledge");
        Directory.CreateDirectory(coding);
        Directory.CreateDirectory(knowledge);
        File.WriteAllText(Path.Combine(coding, "darci-node.json"), """{"node_id":"darci.coding"}""");
        File.WriteAllText(Path.Combine(knowledge, "darci-node.json"), """{"node_id":"darci.knowledge"}""");

        var catalog = new[]
        {
            new NodeCatalogEntry("darci.coding", "Coding", "1.0.0", new[] { "coding.write" }, coding, false),
            new NodeCatalogEntry("darci.knowledge", "Knowledge", "1.0.0", new[] { "knowledge.answer" }, knowledge, false),
        };

        var (result, entries) = BuildAndRead(Request("darci.coding"), catalog);

        Assert.True(result.Success, result.Error);
        Assert.Contains("darci/nodes/darci.coding/darci-node.json", entries);
        Assert.DoesNotContain(entries, e => e.Contains("darci.knowledge"));
        Assert.Equal(new[] { "darci.coding" }, result.IncludedNodeIds);
    }

    [Fact]
    public void TheReadmeInTheZip_IsTheGeneratedOne()
    {
        var (_, _) = BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());

        using var archive = ZipFile.OpenRead(Path.Combine(_root, "darci.zip"));
        using var reader = new StreamReader(archive.GetEntry("darci/README.md")!.Open());
        var readme = reader.ReadToEnd();

        Assert.Contains("ollama pull nomic-embed-text", readme);
        Assert.Contains("bare core", readme);
    }

    [Fact]
    public void ThePackagedLauncher_IsGeneratedForTheZipLayout_NotCopiedFromTheRepo()
    {
        // Caught by actually extracting and running a zip, not by unit tests: the repo's Start-DARCI.ps1
        // resolves DARCI-v4\Darci.Api and starts the core with `dotnet run`. Neither exists in a zip, and
        // `dotnet run` needs the SDK the self-contained publish exists to make unnecessary. Copying it
        // verbatim hands the recipient a launcher that cannot launch, on a machine where they cannot
        // easily tell why.
        var (_, entries) = BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());
        Assert.Contains("darci/Start-DARCI.ps1", entries);

        var script = ReadEntry("darci/Start-DARCI.ps1");

        Assert.Contains(@"core\Darci.Api.exe", script);
        Assert.DoesNotContain("dotnet run", script);
        Assert.DoesNotContain("DARCI-v4", script);
    }

    [Fact]
    public void ThePackagedLauncher_PointsTheCoreAtWhatShippedBesideIt()
    {
        BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());
        var script = ReadEntry("darci/Start-DARCI.ps1");

        // Without these, a previous install's environment silently wins and the core loads someone
        // else's nodes and profile.
        Assert.Contains("DARCI_NODES_PATH", script);
        Assert.Contains("DARCI_HOST_PROFILE", script);
    }

    [Fact]
    public void TheRepoOnlyPreflightScript_IsNotShipped()
    {
        // Test-DARCIEnvironment.ps1 checks for the solution and a DARCI-v4 folder; in a zip it would
        // report confident failures about a perfectly good install.
        var (_, entries) = BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());

        Assert.DoesNotContain(entries, e => e.EndsWith("Test-DARCIEnvironment.ps1", StringComparison.Ordinal));
        // The model resolver DOES ship — the launcher calls it, and it handles the packaged layout.
        Assert.Contains("darci/Get-DarciRequiredModels.ps1", entries);
    }

    [Fact]
    public void ThePackagedLauncher_IsPureAscii_AndShipsWithABom()
    {
        // Found by RUNNING a generated launcher, not by asserting on its text. Windows PowerShell 5.1
        // reads a .ps1 as ANSI unless it has a BOM, so BOM-less UTF-8 turned the em-dashes in this script
        // into mojibake — and mojibake inside a script is a parse error on the recipient's machine, not a
        // cosmetic blemish. Belt and braces: emit the BOM, and keep the content ASCII anyway.
        BuildAndRead(Request(), Array.Empty<NodeCatalogEntry>());

        var script = PackagedStartScript.Build();
        Assert.All(script, c => Assert.True(c < 128, $"non-ASCII character '{c}' in the packaged launcher"));

        using var archive = ZipFile.OpenRead(Path.Combine(_root, "darci.zip"));
        using var stream = archive.GetEntry("darci/Start-DARCI.ps1")!.Open();
        var head = new byte[3];
        Assert.Equal(3, stream.Read(head, 0, 3));
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, head);
    }

    private string ReadEntry(string entryPath)
    {
        using var archive = ZipFile.OpenRead(Path.Combine(_root, "darci.zip"));
        using var reader = new StreamReader(archive.GetEntry(entryPath)!.Open());
        return reader.ReadToEnd();
    }

    [Fact]
    public void RebuildingOverAnExistingZip_ReplacesItRatherThanFailing()
    {
        var request = Request();
        BuildAndRead(request, Array.Empty<NodeCatalogEntry>());
        var (second, entries) = BuildAndRead(request, Array.Empty<NodeCatalogEntry>());

        Assert.True(second.Success, second.Error);
        Assert.Contains("darci/README.md", entries);
    }
}
