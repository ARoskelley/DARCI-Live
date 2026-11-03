#nullable enable

namespace Darci.Nodes;

/// <summary>
/// The outcome of resolving which work context (a coding workspace, an engineering project, …) a
/// packet should operate on when none was specified. The choice is a deliberate, logged decision:
/// the resolving node appends this to the packet log with its <see cref="Confidence"/> and
/// <see cref="Reasoning"/> so the selection is auditable and learnable.
/// </summary>
public sealed record WorkContextResolution(
    string ContextId,
    bool Created,
    Confidence Confidence,
    string Reasoning);

/// <summary>
/// Generic "pick or create the work context for this goal" contract (the workspace-selection seam).
/// Each node type supplies its own implementation — coding resolves a workspace, engineering will
/// resolve a project — but they share this shape: match the intent against existing contexts and,
/// when no confident match exists, create a fresh one. The match decision uses the unified
/// <see cref="Confidence"/> so gap handling is consistent across nodes.
/// </summary>
public interface IWorkContextResolver
{
    Task<WorkContextResolution> ResolveAsync(string intent, CancellationToken ct = default);
}
