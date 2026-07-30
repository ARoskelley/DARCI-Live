#nullable enable

namespace Darci.Nodes;

/// <summary>
/// The contract-shaped invocation surface of a node (doc §5.2 `POST /invoke`, expressed in-process).
///
/// <para>Phase 1 keeps nodes in-process, so an adapter is a thin wrapper over an existing
/// <see cref="INode"/> rather than an HTTP client. The point of introducing it now is that the CORE only
/// ever talks to <see cref="InvokeAsync"/> — so when a node later moves out of process, only the adapter
/// implementation changes and the core is untouched.</para>
///
/// <para>An adapter is deliberately NOT responsible for the work record's lifecycle: it does not park,
/// does not transition state machines, and does not decide retries. That is the core's job (doc §3).</para>
/// </summary>
public interface INodeAdapter
{
    /// <summary>The node's manifest — its identity, capabilities, and requirements.</summary>
    NodeManifest Manifest { get; }

    /// <summary>Execute one capability invocation. Should return promptly; never blocks on a human.</summary>
    Task<NodeResult> InvokeAsync(NodeInvocation invocation, CancellationToken ct = default);

    /// <summary>Liveness/readiness (doc §5.2 `/health`). In-process adapters are ready once constructed.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);
}
