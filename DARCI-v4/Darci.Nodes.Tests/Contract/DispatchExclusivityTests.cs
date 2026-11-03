using Darci.Nodes;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// SU4 / ADD-4 — DISPATCH EXCLUSIVITY.
///
/// <para>The strangler-fig failure mode for this carve is two live paths with forked bookkeeping: some work
/// flowing through the dispatcher (registered, telemetered, one capability map) and some still calling a node
/// directly. That would be invisible in ordinary tests and would quietly split the audit trail.</para>
///
/// <para>So this test makes a bypass LOUD: it scans production source for direct
/// <see cref="INode.HandleAsync"/> invocations and asserts the allow-list. Adding a bypass fails the build's
/// test run and forces the author to justify it, rather than discovering it months later.</para>
/// </summary>
public class DispatchExclusivityTests
{
    /// <summary>
    /// Production files permitted to invoke <c>HandleAsync</c> directly, and why.
    /// </summary>
    private static readonly Dictionary<string, string> AllowedHandleAsyncCallers = new(StringComparer.OrdinalIgnoreCase)
    {
        // THE dispatch path: the adapter is how the core invokes a packet-native node.
        ["LegacyPacketNodeAdapter.cs"] = "the one legitimate INode invocation — the dispatch path itself",

        // Not a node bypass: this is IGapHandler.HandleAsync, a different interface entirely.
        ["KnowledgeNode.cs"] = "invokes IGapHandler.HandleAsync (gap handling), not INode.HandleAsync",
    };

    [Fact]
    public void NoProductionCodeBypassesTheDispatcher()
    {
        var root = FindRepoRoot();
        Assert.NotNull(root);

        var offenders = new List<string>();
        foreach (var file in ProductionSourceFiles(root!))
        {
            var name = Path.GetFileName(file);
            if (AllowedHandleAsyncCallers.ContainsKey(name)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains(".HandleAsync(", StringComparison.Ordinal))
                    offenders.Add($"{name}:{i + 1}: {lines[i].Trim()}");
        }

        Assert.True(offenders.Count == 0,
            "Direct node invocation found outside the dispatch path. Route through INodeRouter/NodeDispatcher " +
            "so the call is registered, telemetered, and resolved from the one capability map — or, if this is " +
            "genuinely not an INode call, add the file to AllowedHandleAsyncCallers with a reason.\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void OnlyOneRouterImplementationExists_SoRoutingCannotFork()
    {
        var root = FindRepoRoot();
        var implementors = ProductionSourceFiles(root!)
            .Where(f => File.ReadAllText(f).Contains(": INodeRouter", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "NodeRouter.cs" }, implementors);
    }

    [Fact]
    public void TheDispatcherIsTheOnlyThingThatBuildsAnInvocation()
    {
        // If something else starts hand-rolling NodeInvocations, telemetry and correlation projection
        // (ADD-2's goal_id mapping) would be duplicated and could drift apart.
        var root = FindRepoRoot();
        var builders = ProductionSourceFiles(root!)
            .Where(f => File.ReadAllText(f).Contains("new NodeInvocation", StringComparison.Ordinal))
            .Select(Path.GetFileName)
            .ToList();

        Assert.Equal(new[] { "NodeDispatcher.cs" }, builders);
    }

    private static IEnumerable<string> ProductionSourceFiles(string repoRoot) =>
        Directory.EnumerateFiles(repoRoot, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(f => !f.Contains(".Tests", StringComparison.Ordinal))
            .OrderBy(f => f, StringComparer.Ordinal);

    /// <summary>Walk up to the DARCI-v4 solution directory.</summary>
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
