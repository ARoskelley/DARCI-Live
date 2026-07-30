using System.Text.Json;
using Darci.Nodes;

namespace Darci.Nodes.Tests.Contract;

public class NodeManifestTests
{
    private static NodeManifest Valid() => new()
    {
        NodeId = NodeKeys.Coding,
        DisplayName = "Coding",
        NodeVersion = "1.0.0",
        Kind = NodeKind.Capability,
        Capabilities = new[]
        {
            new NodeCapabilityDescriptor { Name = Capabilities.CodingWrite, Description = "write code", DeadlineMs = 600_000 },
        },
        Requires = new NodeRequires { ModelClasses = new[] { "code.generate" }, MemoryScopes = new[] { "read:workspace" } },
    };

    [Fact]
    public void ValidManifest_HasNoErrors()
        => Assert.Empty(Valid().Validate());

    [Fact]
    public void UnsupportedContractVersion_IsRejectedWithANamedError()
    {
        var errors = (Valid() with { ContractVersion = "0.9" }).Validate();
        Assert.Contains(errors, e => e.Contains("contract_version") && e.Contains("0.9"));
    }

    [Fact]
    public void MissingOrUnnamespacedNodeId_IsRejected()
    {
        Assert.Contains((Valid() with { NodeId = "" }).Validate(), e => e.Contains("node_id is required"));
        Assert.Contains((Valid() with { NodeId = "coding" }).Validate(), e => e.Contains("must be namespaced"));
    }

    [Fact]
    public void NoCapabilities_IsRejected()
        => Assert.Contains((Valid() with { Capabilities = Array.Empty<NodeCapabilityDescriptor>() }).Validate(),
            e => e.Contains("declares no capabilities"));

    [Fact]
    public void BadlyNamedCapability_IsRejected()
    {
        var m = Valid() with { Capabilities = new[] { new NodeCapabilityDescriptor { Name = "WriteCode" } } };
        Assert.Contains(m.Validate(), e => e.Contains("not a valid namespaced"));
    }

    [Fact]
    public void DuplicateCapabilityWithinOneNode_IsRejected()
    {
        var m = Valid() with
        {
            Capabilities = new[]
            {
                new NodeCapabilityDescriptor { Name = Capabilities.CodingWrite },
                new NodeCapabilityDescriptor { Name = Capabilities.CodingWrite },
            },
        };
        Assert.Contains(m.Validate(), e => e.Contains("declared more than once"));
    }

    [Fact]
    public void NonPositiveDeadline_IsRejected()
    {
        var m = Valid() with { Capabilities = new[] { new NodeCapabilityDescriptor { Name = Capabilities.CodingWrite, DeadlineMs = 0 } } };
        Assert.Contains(m.Validate(), e => e.Contains("deadline_ms"));
    }

    [Fact]
    public void EnvironmentNode_MustDeclareWorkspaceRoot()
    {
        Assert.Contains((Valid() with { Kind = NodeKind.Environment }).Validate(),
            e => e.Contains("workspace_root"));
        Assert.Empty((Valid() with { Kind = NodeKind.Environment, WorkspaceRoot = "./ws" }).Validate());
    }

    [Fact]
    public void InProcess_IsInferredFromAnAbsentEndpoint()
    {
        Assert.True(Valid().IsInProcess);
        Assert.False((Valid() with { Endpoint = "http://localhost:8412" }).IsInProcess);
    }

    [Fact]
    public void Sha256_IsStable_AndChangesWithTheCapabilitySurface()
    {
        var a = Valid();
        Assert.Equal(a.ComputeSha256(), Valid().ComputeSha256());   // deterministic

        // Adding a capability changes the hash — that is the §14c audit signal.
        var widened = a with
        {
            Capabilities = a.Capabilities.Append(new NodeCapabilityDescriptor { Name = Capabilities.CodingTest }).ToList(),
        };
        Assert.NotEqual(a.ComputeSha256(), widened.ComputeSha256());
    }

    [Fact]
    public void RoundTripsThroughDarciNodeJsonShape()
    {
        var json = JsonSerializer.Serialize(Valid(), ManifestJson.Options);

        // snake_case wire names per the doc's darci-node.json
        Assert.Contains("\"contract_version\"", json);
        Assert.Contains("\"node_id\"", json);
        Assert.Contains("\"model_classes\"", json);
        Assert.Contains("\"emits_untrusted\"", json);
        Assert.Contains("\"deadline_ms\"", json);

        var back = JsonSerializer.Deserialize<NodeManifest>(json, ManifestJson.Options)!;
        Assert.Equal(NodeKeys.Coding, back.NodeId);
        Assert.Equal(Capabilities.CodingWrite, back.Capabilities[0].Name);
        Assert.Equal(600_000, back.Capabilities[0].DeadlineMs);
        Assert.Equal(new[] { "code.generate" }, back.Requires.ModelClasses);
        Assert.Empty(back.Validate());
    }
}
