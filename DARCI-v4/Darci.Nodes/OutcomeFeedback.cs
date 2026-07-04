#nullable enable

namespace Darci.Nodes;

/// <summary>
/// A signal from a downstream environment (e.g. the coding node) about whether the work that used an
/// innovated hypothesis actually succeeded. Carried by <see cref="CorrelationId"/> so it can be matched
/// to the innovated entry that fed it. This is the empirical honesty backstop — reality, not the model,
/// gets the last word.
/// </summary>
public sealed record OutcomeFeedback(
    string CorrelationId,
    bool Success,
    string? Evidence = null,
    string? TerminalStatus = null);

/// <summary>Applies an <see cref="OutcomeFeedback"/> to whatever it concerns (Darci.Nodes seam,
/// mirrors <see cref="IGapGoalSink"/>). No-op when nothing matches the correlation id.</summary>
public interface IOutcomeFeedbackSink
{
    Task ApplyAsync(OutcomeFeedback feedback, CancellationToken ct = default);
}

/// <summary>
/// Notified when an entry that had been HUMAN-promoted is automatically demoted by a failure outcome.
/// Lets the UI node surface "your promoted hypothesis just failed" without the demotion waiting on the
/// human (down stays automatic). Optional — no-op / absent until the UI node exists (design-only §14).
/// </summary>
public interface IProvenanceDemotionNotifier
{
    Task NotifyAsync(InnovatedKnowledgeRecord before, InnovatedKnowledgeRecord after, OutcomeFeedback cause, CancellationToken ct = default);
}
