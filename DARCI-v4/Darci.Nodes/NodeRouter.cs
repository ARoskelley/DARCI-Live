#nullable enable

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes;

/// <summary>
/// The routing seam. Resolves a packet's target node, persists the packet at every hop, and hands it to the
/// node through the ONE dispatch point (<see cref="NodeDispatcher"/>). Persistence happens before and after
/// the node runs so a crash mid-handoff still leaves a recoverable, watchdog-reapable record.
///
/// <para><b>Phase 1 (SU3):</b> resolution moved from a compiled-in <see cref="Capability"/> enum scan to the
/// string-keyed <see cref="INodeRegistry"/>, and invocation moved to <see cref="NodeDispatcher"/>. The public
/// surface (<see cref="INodeRouter.DispatchAsync"/>) is deliberately UNCHANGED, so every existing call site
/// and test keeps working — that is the behavior-preservation contract of this sub-unit.</para>
/// </summary>
public sealed class NodeRouter : INodeRouter
{
    private readonly INodeRegistry _registry;
    private readonly NodeDispatcher _dispatcher;
    private readonly INodePacketStore _store;
    private readonly ILogger<NodeRouter> _logger;
    private readonly IGapStore? _gaps;

    /// <summary>
    /// THE ONLY public constructor — deliberately. This type previously had a second, convenience constructor,
    /// and the DI container could not choose between them: <c>AddSingleton&lt;INodeRouter, NodeRouter&gt;()</c>
    /// failed at host start with "the following constructors are ambiguous" while every unit test stayed green
    /// (tests pick a constructor themselves). <c>[ActivatorUtilitiesConstructor]</c> does NOT fix that — the
    /// built-in container's constructor selection ignores it. So the convenience path is a static factory
    /// (<see cref="ForNodes"/>) instead, leaving exactly one constructor and no way to reintroduce the
    /// ambiguity. See DiActivationTests.
    /// </summary>
    public NodeRouter(
        INodeRegistry registry,
        NodeDispatcher dispatcher,
        INodePacketStore store,
        ILogger<NodeRouter> logger,
        IGapStore? gaps = null)
    {
        _registry = registry;
        _dispatcher = dispatcher;
        _store = store;
        _logger = logger;
        _gaps = gaps;
    }

    /// <inheritdoc />
    public bool CanServe(string capability) =>
        !string.IsNullOrWhiteSpace(capability) && _registry.Resolve(capability) is not null;

    /// <summary>
    /// CONVENIENCE factory for packet-native <see cref="INode"/>s: wraps each in a
    /// <see cref="LegacyPacketNodeAdapter"/> with a synthesized manifest and registers it. Capability
    /// ownership is strict here too — two nodes claiming the same verb is a registration error, not a
    /// silently-resolved race.
    /// </summary>
    public static NodeRouter ForNodes(IEnumerable<INode> nodes, INodePacketStore store, ILogger<NodeRouter> logger)
        => new(BuildLegacyRegistry(nodes), new NodeDispatcher(NullLogger<NodeDispatcher>.Instance), store, logger);

    public async Task<NodePacket> DispatchAsync(NodePacket packet, CancellationToken ct = default)
    {
        // Persist the packet up front so it exists even if resolution or the node fails.
        if (packet.State == NodeState.Created)
        {
            packet = packet.Transition(NodeId.Orchestrator, NodeState.Routed,
                $"Routed (address={packet.Address?.ToString() ?? "—"}, capability={packet.RequestedCapability?.ToString() ?? "—"})");
            await _store.CreatePacketAsync(packet, ct);
        }
        else
        {
            await _store.SavePacketAsync(packet, ct);
        }

        var (registration, capability) = Resolve(packet);
        if (registration is null)
        {
            // NOT a failure (doc §5.4 missing-environment). Nothing was attempted, so calling this Failed
            // would feed phantom negative evidence into the confidence and campaign paths — and it would
            // lie to a collaborator running a core without this node. Blocked is terminal, so the packet
            // does not leak as an active orphan waiting for a node that cannot appear at runtime; the
            // ACTIONABLE half goes into a durable GapRecord below, which survives restarts.
            var blocked = packet.State.IsTerminal()
                ? packet
                : packet.Transition(NodeId.Orchestrator, NodeState.Blocked,
                    "No node serves this capability — nothing was attempted.",
                    success: null,
                    error: $"No node matches address={packet.Address?.ToString() ?? "—"} / capability={capability}.");
            await _store.SavePacketAsync(blocked, ct);
            _logger.LogWarning(
                "NodeRouter: no node serves capability {Cap} (address={Addr}) for packet {Id} — blocked, not failed.",
                capability, packet.Address, packet.Id);

            await RecordUnavailableCapabilityGapAsync(packet, capability, ct);
            return blocked;
        }

        _logger.LogInformation("NodeRouter dispatching packet {Id} to {Node}.", packet.Id, registration.NodeId);

        try
        {
            var result = await _dispatcher.DispatchAsync(registration, packet, capability, ct);
            await _store.SavePacketAsync(result, ct);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var legacyNodeId = CapabilityKey.ToLegacyNode(registration.NodeId) ?? NodeId.Orchestrator;
            _logger.LogError(ex, "NodeRouter: node {Node} threw handling packet {Id}.", legacyNodeId, packet.Id);
            var aborted = packet.State.IsTerminal()
                ? packet
                : packet.Transition(legacyNodeId, NodeState.Failed,
                    $"Node {legacyNodeId} threw while handling the packet.",
                    success: false, error: $"{ex.GetType().Name}: {ex.Message}");
            await _store.SavePacketAsync(aborted, ct);
            return aborted;
        }
    }

    /// <summary>
    /// The restart-safe home for "this core cannot do X". The packet terminates; the standing need does
    /// not live in it. Best-effort by design — a core with no gap store still degrades correctly, it just
    /// has nowhere to write the note, and failing to record a gap must never turn a clean degradation into
    /// an exception.
    /// </summary>
    private async Task RecordUnavailableCapabilityGapAsync(NodePacket packet, string capability, CancellationToken ct)
    {
        if (_gaps is null) return;

        try
        {
            await _gaps.AddAsync(new GapRecord
            {
                CorrelationId = packet.CorrelationId,
                OriginPacketId = packet.Id,
                OriginNode = NodeId.Orchestrator,
                Question = $"No node serves capability '{capability}'.",
                Intent = packet.Payload.Intent,
                Missing = $"a node providing capability {capability}",
                Status = GapStatus.Open,
            }, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Could not record a gap for unavailable capability {Cap} (non-fatal).", capability);
        }
    }

    /// <summary>
    /// Explicit address wins; otherwise the node registered for the requested capability. Both are resolved
    /// from the packet's canonical STRING keys, so an external capability with no <see cref="Capability"/>
    /// member routes exactly like a built-in one.
    /// </summary>
    private (NodeRegistration? Registration, string Capability) Resolve(NodePacket packet)
    {
        var requestedCapability = packet.EffectiveCapabilityKey ?? "";

        if (packet.EffectiveAddressKey is { } addressKey)
        {
            var byAddress = _registry.ResolveNode(addressKey);
            if (byAddress is not null)
            {
                // Prefer the requested capability when this node actually serves it, so telemetry and the
                // per-invocation deadline reflect the real verb rather than a guess.
                var capability = byAddress.Manifest.Capabilities.Any(c => c.Name == requestedCapability)
                    ? requestedCapability
                    : byAddress.Manifest.Capabilities.Count == 1
                        ? byAddress.Manifest.Capabilities[0].Name
                        : requestedCapability;
                return (byAddress, capability);
            }
        }

        if (requestedCapability.Length > 0)
        {
            var byCapability = _registry.Resolve(requestedCapability);
            if (byCapability is not null) return (byCapability, requestedCapability);
        }

        return (null, requestedCapability);
    }

    private static INodeRegistry BuildLegacyRegistry(IEnumerable<INode> nodes)
    {
        var registry = new NodeRegistry(NullLogger<NodeRegistry>.Instance);
        foreach (var node in nodes)
            registry.Register(LegacyPacketNodeAdapter.ForLegacyNode(node));
        return registry;
    }
}
