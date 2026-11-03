#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// <c>deadline_ms</c> is REQUIRED, and bounded by <see cref="NodeDeadline.MaxMs"/> for an out-of-process
/// node — refused at load with a named error rather than clipped at runtime.
///
/// <para>The behaviour these pin replaces a trap. The core's node HTTP client used to inherit the .NET
/// default of 100s while a manifest could declare any budget it liked, so the excess was silently
/// truncated and the overrun surfaced as <c>DEADLINE_EXCEEDED</c> — indistinguishable from the node just
/// being slow. The ceiling is now one constant shared by the client and the validator, and exceeding it
/// is a startup failure that says so.</para>
/// </summary>
public sealed class DeadlineContractTests
{
    private static NodeManifest Manifest(string? endpoint, params NodeCapabilityDescriptor[] caps) => new()
    {
        NodeId = "acme.deadlines",
        DisplayName = "deadlines",
        NodeVersion = "1.0.0",
        Endpoint = endpoint,
        Capabilities = caps,
    };

    private static NodeCapabilityDescriptor Cap(int? deadlineMs) =>
        new() { Name = "acme.work", Description = "work", DeadlineMs = deadlineMs };

    private const string Remote = "http://127.0.0.1:7901";

    // ── required ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void AManifestWithNoDeadline_IsRejected_AndTheErrorNamesTheField()
    {
        var errors = Manifest(Remote, Cap(null)).Validate();

        var error = Assert.Single(errors);
        Assert.Contains("does not declare deadline_ms", error);
        Assert.Contains("required", error);
        Assert.Contains("180000", error);           // tells the author the bound as well as the field
        Assert.Contains("acme.work", error);        // and which capability
    }

    [Fact]
    public void AMissingDeadline_IsRejectedForAnInProcessNodeToo()
    {
        // The CEILING is transport-scoped; the REQUIREMENT is not. Every capability has to state how long
        // it may take, whoever runs it.
        var errors = Manifest(endpoint: null, Cap(null)).Validate();

        Assert.Contains(errors, e => e.Contains("does not declare deadline_ms"));
    }

    [Fact]
    public void ThereIsNoImplicitDefault_SoAbsenceIsDistinguishableFromAValue()
    {
        // DeadlineMs is int? precisely so "absent" can be refused by name. A non-nullable int with a
        // default would make an unstated deadline indistinguishable from a deliberate 300000.
        Assert.Null(new NodeCapabilityDescriptor().DeadlineMs);
    }

    [Fact]
    public void JsonWithoutDeadlineMs_DeserializesToNull_NotToADefault()
    {
        const string json = """
            {"contract_version":"0.1.1","node_id":"acme.deadlines","node_version":"1.0.0",
             "endpoint":"http://127.0.0.1:7901",
             "capabilities":[{"name":"acme.work","description":"work"}]}
            """;

        var manifest = System.Text.Json.JsonSerializer.Deserialize<NodeManifest>(json, ManifestJson.Options);

        Assert.Null(manifest!.Capabilities[0].DeadlineMs);
        Assert.Contains(manifest.Validate(), e => e.Contains("does not declare deadline_ms"));
    }

    // ── the ceiling, refused loudly ──────────────────────────────────────────────────────────────

    [Fact]
    public void AnOutOfProcessDeadlineAboveTheCeiling_IsRefused_WithAnActionableError()
    {
        var errors = Manifest(Remote, Cap(NodeDeadline.MaxMs + 1)).Validate();

        var error = Assert.Single(errors);
        Assert.Contains("180001", error);                       // what they declared
        Assert.Contains("above the maximum 180000", error);     // and the bound
        Assert.Contains("out-of-process", error);               // why it applies
        Assert.Contains("Lower deadline_ms", error);            // and what to do about it
    }

    [Theory]
    [InlineData(1)]
    [InlineData(15_000)]
    [InlineData(120_000)]                 // darci.modeling's figure
    [InlineData(NodeDeadline.MaxMs)]      // exactly at the ceiling is fine
    public void AnOutOfProcessDeadlineAtOrBelowTheCeiling_IsAccepted(int deadlineMs)
    {
        Assert.Empty(Manifest(Remote, Cap(deadlineMs)).Validate());
    }

    [Fact]
    public void ZeroAndNegativeAreStillRefused_Separately_FromTheCeiling()
    {
        Assert.Contains(Manifest(Remote, Cap(0)).Validate(), e => e.Contains("non-positive"));
        Assert.Contains(Manifest(Remote, Cap(-1)).Validate(), e => e.Contains("non-positive"));
    }

    [Fact]
    public void TheCeilingIsScopedToOutOfProcess_BecauseAnInProcessNodeTouchesNoHttpClient()
    {
        // Deliberate, and the reason is load-bearing: the ceiling exists because an HTTP node is called
        // through a client whose timeout is NodeDeadline.Max. The first-party coding node legitimately
        // declares 3600000 (an hour) and runs IN-PROCESS, so the limit cannot apply to it — and since a
        // first-party manifest failing validation is FATAL by design, applying it would stop the core
        // booting over a bound that does not exist for that transport.
        Assert.Empty(Manifest(endpoint: null, Cap(3_600_000)).Validate());
        Assert.NotEmpty(Manifest(Remote, Cap(3_600_000)).Validate());
    }

    [Fact]
    public void TheCeilingIsCheckedPerCapability_NotJustOnTheFirst()
    {
        var errors = Manifest(Remote,
            new NodeCapabilityDescriptor { Name = "acme.fine", Description = "d", DeadlineMs = 1_000 },
            new NodeCapabilityDescriptor { Name = "acme.toolong", Description = "d", DeadlineMs = 900_000 }).Validate();

        var error = Assert.Single(errors);
        Assert.Contains("acme.toolong", error);
        Assert.DoesNotContain("acme.fine", error);
    }

    // ── the real manifests on disk ───────────────────────────────────────────────────────────────

    [Fact]
    public void EveryManifestInTheRepo_StillLoadsAndValidates()
    {
        // The ripple guard. Requiring deadline_ms and adding a ceiling could have invalidated a shipped
        // manifest, and a first-party one failing validation stops the core booting — so assert against
        // the real files rather than fixtures.
        var nodesDir = FindNodesDirectory();
        var loader = new NodeManifestLoader(NullLogger<NodeManifestLoader>.Instance);

        var (loaded, failures) = loader.LoadAllTolerant(nodesDir);

        Assert.Empty(failures.Select(f => $"{f.Path}: {f.Reason}"));
        Assert.NotEmpty(loaded);
        Assert.All(loaded, m => Assert.Empty(m.Manifest.Validate()));

        // Every capability states a deadline, and every out-of-process one is within the ceiling.
        foreach (var m in loaded)
        {
            foreach (var c in m.Manifest.Capabilities)
            {
                Assert.NotNull(c.DeadlineMs);
                if (!m.Manifest.IsInProcess)
                    Assert.True(c.DeadlineMs <= NodeDeadline.MaxMs,
                        $"{m.Manifest.NodeId}/{c.Name} declares {c.DeadlineMs}ms over HTTP");
            }
        }
    }

    [Fact]
    public void DarciModeling_TheOnlyOutOfProcessNodeShipped_StillPasses()
    {
        var path = Path.Combine(FindNodesDirectory(), "darci.modeling", "darci-node.json");
        Assert.True(File.Exists(path), $"expected the reference node's manifest at {path}");

        var loaded = new NodeManifestLoader(NullLogger<NodeManifestLoader>.Instance).Load(path);

        Assert.Empty(loaded.Manifest.Validate());
        Assert.False(loaded.Manifest.IsInProcess);
        var build = Assert.Single(loaded.Manifest.Capabilities, c => c.Name == "model.build");
        Assert.Equal(120_000, build.DeadlineMs);
        Assert.True(build.DeadlineMs <= NodeDeadline.MaxMs);
    }

    /// <summary>Walk up from the test binary to the repo's nodes/ directory.</summary>
    private static string FindNodesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "DARCI-v4", "nodes");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate DARCI-v4/nodes from the test binary.");
    }
}
