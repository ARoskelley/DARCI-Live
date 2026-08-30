#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Carries the startup <see cref="NodeDiscoveryReport"/> so diagnostics can show what the core found —
/// including what it SKIPPED and why, which is the half that is otherwise invisible.
///
/// <para>A holder rather than a return value because the registry is built inside a DI factory: the report
/// is a by-product of construction, and this is the seam that lets it out without making
/// <see cref="INodeRegistry"/> know about discovery.</para>
/// </summary>
public sealed class NodeDiscoveryReportHolder
{
    public NodeDiscoveryReport Report { get; set; } = new();
}
