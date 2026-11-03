#nullable enable

using DarciControl.Logic.Nodes;
using DarciControl.Logic.Packaging;

namespace DarciControl.Logic.Tests;

/// <summary>
/// What a distributable actually contains. These are the mistakes that matter and that a slow end-to-end
/// build would catch late or not at all: shipping a secret, omitting the host profile, silently dropping a
/// node the user ticked, or treating a bare core as a mistake.
/// </summary>
public sealed class ZipPlanTests : IDisposable
{
    private readonly string _repo;
    private readonly string _publishedCore;

    public ZipPlanTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), $"darci-zipplan-{Guid.NewGuid():N}");
        _publishedCore = Path.Combine(_repo, "published");
        Directory.CreateDirectory(_publishedCore);
        Directory.CreateDirectory(Path.Combine(_repo, "DARCI-v4"));

        File.WriteAllText(Path.Combine(_repo, "DARCI-v4", "host-profile.json"), """
            {"profile_id":"t","providers":{"ollama":{"kind":"ollama","base_url":"http://localhost:11434"}},
             "classes":{"chat.balanced":{"provider":"ollama","model":"gemma2:9b"}}}
            """);
        File.WriteAllText(Path.Combine(_repo, ".env.local.example"), "DARCI_NEO4J_PASSWORD=\n");
        File.WriteAllText(Path.Combine(_repo, "Start-DARCI.ps1"), "# start");
        File.WriteAllText(Path.Combine(_repo, "Get-DarciRequiredModels.ps1"), "# models");
        File.WriteAllText(Path.Combine(_repo, "Test-DARCIEnvironment.ps1"), "# preflight");
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { /* best effort */ }
    }

    private ZipBuildRequest Request(params string[] nodes) => new()
    {
        RepoRoot = _repo,
        OutputPath = Path.Combine(_repo, "out.zip"),
        SelectedNodeIds = nodes,
    };

    private static NodeCatalogEntry Node(string id, string folder, string? problem = null) =>
        new(id, id, "1.0.0", new[] { $"{id}.do" }, folder, IsOutOfProcess: false, Problem: problem);

    private string NodeFolder(string id)
    {
        var dir = Path.Combine(_repo, "DARCI-v4", "nodes", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "darci-node.json"), "{}");
        return dir;
    }

    // ── node selection ──

    [Fact]
    public void ASelectedNode_IsIncluded_AndAnUnselectedOneIsAbsentEntirely()
    {
        var catalog = new[] { Node("darci.coding", NodeFolder("darci.coding")), Node("darci.knowledge", NodeFolder("darci.knowledge")) };

        var plan = ZipPlan.Create(Request("darci.coding"), catalog, _publishedCore);

        Assert.Equal(new[] { "darci.coding" }, plan.IncludedNodeIds);
        Assert.Contains(plan.Entries, e => e.EntryPath == "darci/nodes/darci.coding");
        // Not merely disabled — not in the zip at all.
        Assert.DoesNotContain(plan.Entries, e => e.EntryPath.Contains("darci.knowledge"));
    }

    [Fact]
    public void ABareCore_IsAValidProduct_NotAMistake()
    {
        // Phase 3 made a node-free core genuinely work, so the plan must not treat this as an error.
        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);

        Assert.Empty(plan.IncludedNodeIds);
        Assert.Contains(plan.Entries, e => e.EntryPath == "darci/core");
        Assert.Contains(plan.Warnings, w => w.Contains("valid bare core"));
    }

    [Fact]
    public void ASelectedNodeThatDoesNotExist_WarnsRatherThanSilentlyVanishing()
    {
        var plan = ZipPlan.Create(Request("acme.ghost"), Array.Empty<NodeCatalogEntry>(), _publishedCore);

        Assert.Empty(plan.IncludedNodeIds);
        Assert.Contains(plan.Warnings, w => w.Contains("acme.ghost"));
    }

    [Fact]
    public void ANodeWithABrokenManifest_IsNotPackaged_AndSaysWhy()
    {
        var catalog = new[] { Node("acme.broken", NodeFolder("acme.broken"), problem: "manifest is not valid JSON") };

        var plan = ZipPlan.Create(Request("acme.broken"), catalog, _publishedCore);

        Assert.Empty(plan.IncludedNodeIds);
        Assert.Contains(plan.Warnings, w => w.Contains("not valid JSON"));
    }

    // ── what always ships ──

    [Fact]
    public void TheCoreAndHostProfileAndScripts_AlwaysShip()
    {
        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);
        var paths = plan.Entries.Select(e => e.EntryPath).ToList();

        Assert.Contains("darci/core", paths);
        // Without the profile the packaged launcher cannot know what models to check for.
        Assert.Contains("darci/host-profile.json", paths);
        Assert.Contains("darci/Get-DarciRequiredModels.ps1", paths);
        Assert.Contains("darci/.env.local.example", paths);

        // Start-DARCI.ps1 is deliberately NOT planned from the repo — it is generated for the zip layout
        // by the assembler, because the repo one cannot work there. See PackagedStartScript.
        Assert.DoesNotContain("darci/Start-DARCI.ps1", paths);
    }

    [Fact]
    public void AMissingHostProfile_WarnsInsteadOfShippingSilentlyWithout()
    {
        File.Delete(Path.Combine(_repo, "DARCI-v4", "host-profile.json"));

        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);

        Assert.DoesNotContain(plan.Entries, e => e.EntryPath == "darci/host-profile.json");
        Assert.Contains(plan.Warnings, w => w.Contains("host-profile.json"));
    }

    // ── secrets ──

    [Fact]
    public void TheRealEnvLocal_IsNeverPlanned_OnlyTheExample()
    {
        File.WriteAllText(Path.Combine(_repo, ".env.local"), "DARCI_NEO4J_PASSWORD=hunter2");

        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);

        Assert.Contains(plan.Entries, e => e.EntryPath == "darci/.env.local.example");
        Assert.DoesNotContain(plan.Entries, e => e.EntryPath.EndsWith("/.env.local", StringComparison.Ordinal));
        Assert.Empty(plan.FindForbidden());
    }

    [Fact]
    public void AForbiddenFile_IsDetected_IfItEverReachesThePlan()
    {
        // The guard has to be able to FIRE, or it is decoration. A secret leaving this machine inside a
        // zip somebody else unpacks is not a bug you get to fix afterwards.
        var secret = Path.Combine(_repo, ".env.local");
        File.WriteAllText(secret, "DARCI_NEO4J_PASSWORD=hunter2");

        var tampered = new ZipPlan
        {
            Entries = new[] { new ZipEntry(secret, "darci/.env.local", false) },
            IncludedNodeIds = Array.Empty<string>(),
        };

        Assert.NotEmpty(tampered.FindForbidden());

        var result = ZipAssembler.Write(tampered, Path.Combine(_repo, "refused.zip"), "readme");
        Assert.False(result.Success);
        Assert.Contains("never ship", result.Error);
        Assert.False(File.Exists(Path.Combine(_repo, "refused.zip")));
    }

    // ── ONNX opt-in ──

    [Fact]
    public void OnnxModels_AreExcludedByDefault()
    {
        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);

        // Match the ONNX entry exactly. A substring check on "Models" also matches
        // Get-DarciRequiredModels.ps1, which is a legitimate and expected entry.
        Assert.DoesNotContain(plan.Entries, e => e.EntryPath == "darci/core/Models");
    }

    [Fact]
    public void OnnxModels_AreIncludedWhenAskedFor()
    {
        var models = Path.Combine(_repo, "DARCI-v4", "Darci.Brain.Training", "models");
        Directory.CreateDirectory(models);
        File.WriteAllText(Path.Combine(models, "darci_policy.onnx"), "not really a model");

        var plan = ZipPlan.Create(Request() with { IncludeOnnxModels = true }, Array.Empty<NodeCatalogEntry>(), _publishedCore);

        Assert.Contains(plan.Entries, e => e.EntryPath == "darci/core/Models");
    }

    [Fact]
    public void RequestingOnnxModelsThatDoNotExist_WarnsAboutTheFallback()
    {
        var plan = ZipPlan.Create(Request() with { IncludeOnnxModels = true }, Array.Empty<NodeCatalogEntry>(), _publishedCore);

        Assert.Contains(plan.Warnings, w => w.Contains("priority ladder"));
    }

    // ── the README the recipient reads ──

    [Fact]
    public void TheReadme_NamesTheModelsThisZipsProfileActuallyRequires()
    {
        // Generated, not templated: a static README is exactly how "ollama pull gemma4:e4b" survived.
        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);
        var readme = ZipAssembler.BuildReadme(Request(), plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));

        Assert.Contains("ollama pull gemma2:9b", readme);
        Assert.DoesNotContain("gemma4:e4b", readme);
    }

    [Fact]
    public void TheReadme_TellsABareCoreRecipientWhatTheyHave()
    {
        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);
        var readme = ZipAssembler.BuildReadme(Request(), plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));

        Assert.Contains("bare core", readme);
        Assert.Contains("blocked", readme);   // the honest degradation, explained
    }

    [Fact]
    public void TheReadme_IsHonestAboutOnnxBeingAbsent()
    {
        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);
        var readme = ZipAssembler.BuildReadme(Request(), plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));

        Assert.Contains("priority ladder", readme);
    }

    [Fact]
    public void TheReadme_CoversOllamaAsTheOneExternalDependency()
    {
        var plan = ZipPlan.Create(Request(), Array.Empty<NodeCatalogEntry>(), _publishedCore);
        var readme = ZipAssembler.BuildReadme(Request(), plan, Path.Combine(_repo, "DARCI-v4", "host-profile.json"));

        Assert.Contains("ollama.com", readme);
        Assert.Contains("do **not** need the .NET SDK", readme);
    }
}
