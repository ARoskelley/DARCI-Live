#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Wraps an existing packet-native <see cref="INode"/> in the contract-shaped <see cref="INodeAdapter"/>
/// surface (doc §9 Phase 1: "existing subsystems become in-process nodes behind adapters").
///
/// <para><b>F1a — the transitional side-channel.</b> Today's nodes are packet-native: they call
/// <c>packet.Transition(...)</c> / <c>WithSlot(...)</c> and return a transitioned packet carrying their own
/// log entries. Rather than rewrite all of them now (which would break "no behavior change" and balloon this
/// sub-unit), the adapter passes the LIVE packet through <see cref="NodeInvocation.PacketRef"/> and hands the
/// node's returned packet back via <see cref="NodeResult.PacketRef"/>. Both are <c>[JsonIgnore]</c>d, so this
/// affordance physically cannot cross a process boundary — an out-of-process node could never rely on it.</para>
///
/// <para>When nodes are de-legacied (Phase 3+), they implement <see cref="INodeAdapter"/> directly, the
/// PacketRef properties are deleted, and payload-only becomes mandatory.</para>
/// </summary>
public sealed class LegacyPacketNodeAdapter : INodeAdapter
{
    private readonly INode _node;

    public LegacyPacketNodeAdapter(INode node, NodeManifest manifest)
    {
        _node = node;
        Manifest = manifest;
    }

    public NodeManifest Manifest { get; }

    /// <summary>The wrapped node — exposed so the dispatcher can attribute log entries to its NodeId.</summary>
    public INode Node => _node;

    /// <summary>
    /// Synthesize a manifest for a legacy in-process node from its declared <see cref="INode.Id"/> and
    /// <see cref="INode.Capabilities"/>, via the transitional enum→string bridge. Used by the compatibility
    /// path so existing DI/tests keep working verbatim; manifest-driven registration is preferred and takes
    /// precedence when a real `darci-node.json` exists for the node.
    /// </summary>
    public static NodeManifest SynthesizeManifest(INode node) => new()
    {
        ContractVersion = NodeContractVersion.Current,
        NodeId = CapabilityKey.From(node.Id),
        DisplayName = node.Id.ToString(),
        NodeVersion = "0.0.0-legacy",
        Kind = NodeKind.Capability,
        Endpoint = null,   // in-process
        Capabilities = node.Capabilities
            .Select(c => new NodeCapabilityDescriptor
            {
                Name = CapabilityKey.From(c),
                Description = $"legacy in-process capability {c}",
            })
            .OrderBy(c => c.Name, StringComparer.Ordinal)
            .ToList(),
    };

    public static LegacyPacketNodeAdapter ForLegacyNode(INode node) => new(node, SynthesizeManifest(node));

    public async Task<NodeResult> InvokeAsync(NodeInvocation invocation, CancellationToken ct = default)
    {
        var packet = invocation.PacketRef
            ?? throw new InvalidOperationException(
                $"Legacy adapter for '{Manifest.NodeId}' requires an in-process PacketRef; " +
                "a payload-only invocation cannot be served until this node is de-legacied.");

        var resultPacket = await _node.HandleAsync(packet, ct);

        // Derive the contract-shaped outcome from what the node actually did to the work record.
        var (outcome, error, dependency) = Classify(resultPacket);

        return new NodeResult
        {
            TraceId = invocation.TraceId,
            Outcome = outcome,
            Error = error,
            Dependency = dependency,
            Confidence = resultPacket.LastEntry?.Confidence ?? Confidence.Unassessed,
            Taint = invocation.Taint,   // Phase 1: taint is carried, not computed
            Payload = resultPacket.Payload.Slots,
            PacketRef = resultPacket,
        };
    }

    private static (NodeOutcome, NodeError?, NodeDependency?) Classify(NodePacket packet) => packet.State switch
    {
        NodeState.Succeeded => (NodeOutcome.Ok, null, null),

        // The node parked the work record awaiting something external — Rev 0.1.1's `blocked`, NOT an error.
        NodeState.AwaitingDependency => (NodeOutcome.Blocked, null,
            new NodeDependency(DependencyKind.HumanDecision,
                packet.LastEntry?.Decision ?? "awaiting an external dependency", packet.Id)),

        NodeState.Failed => (NodeOutcome.Error,
            NodeError.Of(NodeErrorCode.Internal, packet.LastEntry?.Error ?? packet.LastEntry?.Decision ?? "node reported failure"),
            null),

        NodeState.Aborted => (NodeOutcome.Error,
            NodeError.Of(NodeErrorCode.DeadlineExceeded, packet.LastEntry?.Error ?? "aborted"), null),

        // Still active (e.g. a long-running node that returns Working for the caller to poll) — not an error.
        _ => (NodeOutcome.Ok, null, null),
    };
}
