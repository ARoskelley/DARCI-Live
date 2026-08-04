using Darci.Memory.Graph;

namespace Darci.Research.Agents.Tests.Contract;

/// <summary>
/// P2c.4 — MEMORY EXCLUSIVITY (the ADD-4 pattern applied to the knowledge graph).
///
/// <para>The strangler-fig failure mode: some node code goes through the broker while some still holds
/// <see cref="IKnowledgeGraph"/> directly. Both paths work, so nothing fails — but scopes are enforced on
/// only half the traffic, and the §6.1 denial log becomes a lie by omission. This scans production source and
/// makes a bypass a TEST FAILURE rather than a silent second path.</para>
/// </summary>
public class MemoryExclusivityTests
{
    /// <summary>
    /// Files permitted to reference <see cref="IKnowledgeGraph"/> directly, and why. Everything else must go
    /// through <see cref="IMemoryBroker"/>.
    ///
    /// <para>The line follows doc §3: the CORE owns the knowledge graph, so core services legitimately hold
    /// it. The broker mediates NODE access — it is not a wall between the core and its own store.</para>
    /// </summary>
    private static readonly Dictionary<string, string> AllowedDirectGraphAccess = new(StringComparer.OrdinalIgnoreCase)
    {
        // The graph itself and the broker that fronts it.
        ["IKnowledgeGraph.cs"] = "the interface",
        ["KnowledgeGraph.cs"] = "the implementation",
        ["MemoryBroker.cs"] = "the broker — the one thing that legitimately holds the graph",

        // Core-internal services (doc §3): part of the core, not nodes.
        ["MemoryStore.cs"] = "core memory service",
        ["ConfidenceTracker.cs"] = "core confidence service; the graph is its backing store",
        ["KgEnrichmentService.cs"] = "core enrichment service",

        // Composition root: wires the graph into the broker and serves the /knowledge endpoints.
        ["Program.cs"] = "composition root + core-owned /knowledge REST endpoints",
    };

    [Fact]
    public void NoNodeCodeTouchesTheKnowledgeGraphDirectly()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var offenders = new List<string>();
        foreach (var file in ProductionSourceFiles(root!))
        {
            var name = Path.GetFileName(file);
            if (AllowedDirectGraphAccess.ContainsKey(name)) continue;

            var text = File.ReadAllText(file);
            if (text.Contains("IKnowledgeGraph", StringComparison.Ordinal))
                offenders.Add(name);
        }

        Assert.True(offenders.Count == 0,
            "Node code must reach the knowledge graph through IMemoryBroker so its declared scopes are " +
            "enforced and denials are logged (doc §6.1). Offending file(s): " + string.Join(", ", offenders) +
            ". If this is genuinely core-internal, add it to AllowedDirectGraphAccess with a reason.");
    }

    [Fact]
    public void TheConvertedNodeConsumers_NoLongerReferenceTheGraph()
    {
        // Named explicitly so a regression points straight at the file that regressed.
        var root = FindRepoRoot();
        foreach (var converted in new[]
                 {
                     "InnovationSynthesizer.cs", "KnowledgeAssessor.cs",
                     "DeepResearchOrchestrator.cs", "GraphResearchAgent.cs", "CodingContextBuilder.cs",
                 })
        {
            var path = ProductionSourceFiles(root!).FirstOrDefault(f => Path.GetFileName(f) == converted);
            Assert.True(path is not null, $"expected to find {converted}");

            var text = File.ReadAllText(path!);
            Assert.DoesNotContain("IKnowledgeGraph", text);
            Assert.Contains("IMemoryBroker", text);
            Assert.Contains("MemoryAccess", text);   // it declares WHO it is and WHAT it may do
        }
    }

    [Fact]
    public void EveryNodeConsumerDeclaresScopesItsManifestGrants()
    {
        // Guards the drift risk in hand-declared MemoryAccess: a consumer must not ask for a scope its
        // node's reviewed manifest does not grant, or it would fail at runtime with PERMISSION_DENIED.
        var root = FindRepoRoot();
        var manifests = Directory
            .EnumerateFiles(Path.Combine(root!, "nodes"), "darci-node.json", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToList();

        Assert.NotEmpty(manifests);

        // Every scope the code declares must appear in some manifest's memory_scopes.
        foreach (var (file, scopes) in DeclaredScopesByFile(root!))
        {
            foreach (var scope in scopes)
            {
                Assert.True(
                    manifests.Any(m => m.Contains($"\"{scope}\"", StringComparison.Ordinal)),
                    $"{file} declares memory scope '{scope}', which no darci-node.json grants. " +
                    "Either add it to that node's requires.memory_scopes or stop using it.");
            }
        }
    }

    private static IEnumerable<(string File, IReadOnlyList<string> Scopes)> DeclaredScopesByFile(string root)
    {
        foreach (var file in ProductionSourceFiles(root))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("MemoryAccess.ForNode", StringComparison.Ordinal)) continue;

            var scopes = new List<string>();
            foreach (var scope in MemoryScopes.All)
            {
                // Matches the constant form used in code, e.g. MemoryScopes.ReadKnowledge.
                var constantName = scope switch
                {
                    MemoryScopes.ReadKnowledge => nameof(MemoryScopes.ReadKnowledge),
                    MemoryScopes.WriteKnowledge => nameof(MemoryScopes.WriteKnowledge),
                    MemoryScopes.ReadDocuments => nameof(MemoryScopes.ReadDocuments),
                    MemoryScopes.ReadWorkspace => nameof(MemoryScopes.ReadWorkspace),
                    _ => null,
                };
                if (constantName is not null && text.Contains($"MemoryScopes.{constantName}", StringComparison.Ordinal))
                    scopes.Add(scope);
            }

            if (scopes.Count > 0) yield return (Path.GetFileName(file), scopes);
        }
    }

    private static IEnumerable<string> ProductionSourceFiles(string repoRoot) =>
        Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains(".Tests", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DARCI.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
