#nullable enable

namespace Darci.Nodes;

/// <summary>One brokered model call, attributed to the invocation that caused it.</summary>
public sealed record ModelCallRecord(
    string TraceId,
    string GoalId,
    string ModelClass,
    string ResolvedModel,
    string ProviderKind,
    DateTime StartedAt,
    long DurationMs,
    int TokensIn,
    int TokensOut,
    bool Succeeded,
    string? Purpose = null,
    string? Error = null);

/// <summary>
/// AMBIENT INVOCATION SCOPE (Phase 2 fork F1, option a).
///
/// <para>The attribution problem: a model call happens deep inside a node's own code, many frames below the
/// dispatcher, and the doc's telemetry wants to know which invocation it belonged to. Threading a context
/// object through every call would touch ~57 call sites; instead the dispatcher opens an ambient scope and
/// the broker reads it. Call sites stay untouched and attribution is automatic.</para>
///
/// <para><b>Known limitation, named deliberately:</b> <see cref="AsyncLocal{T}"/> flows into awaited work but
/// NOT into fire-and-forget work started before the scope, nor into calls made after the scope closes. Such
/// calls are recorded as UNATTRIBUTED (empty trace/goal) rather than being misattributed to the wrong
/// invocation — an unattributed row is honest; a wrong one corrupts the analysis this data exists for.</para>
/// </summary>
public static class ModelCallScope
{
    private static readonly AsyncLocal<ScopeState?> Current = new();

    /// <summary>The invocation currently in scope, if any.</summary>
    public static (string TraceId, string GoalId)? CurrentInvocation =>
        Current.Value is { } s ? (s.TraceId, s.GoalId) : null;

    /// <summary>Open a scope for one node invocation. Dispose to close it.</summary>
    public static IDisposable Begin(string traceId, string goalId)
    {
        var state = new ScopeState(traceId, goalId, Current.Value);
        Current.Value = state;
        return new Handle(state);
    }

    /// <summary>Record a model call against the current invocation (no-op outside a scope's aggregation,
    /// but the call is still returned to the sink as unattributed).</summary>
    internal static void Attribute(ModelCallRecord call)
    {
        var state = Current.Value;
        if (state is null) return;
        state.Add(call);
    }

    /// <summary>The calls made during the current scope — used by the dispatcher to roll up totals.</summary>
    public static IReadOnlyList<ModelCallRecord> CurrentCalls =>
        Current.Value?.Snapshot() ?? Array.Empty<ModelCallRecord>();

    private sealed class ScopeState
    {
        private readonly object _gate = new();
        private readonly List<ModelCallRecord> _calls = new();

        public ScopeState(string traceId, string goalId, ScopeState? parent)
        {
            TraceId = traceId;
            GoalId = goalId;
            Parent = parent;
        }

        public string TraceId { get; }
        public string GoalId { get; }
        public ScopeState? Parent { get; }

        public void Add(ModelCallRecord call)
        {
            lock (_gate) _calls.Add(call);
        }

        public IReadOnlyList<ModelCallRecord> Snapshot()
        {
            lock (_gate) return _calls.ToList();
        }
    }

    private sealed class Handle : IDisposable
    {
        private readonly ScopeState _state;
        private bool _disposed;

        public Handle(ScopeState state) => _state = state;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            // Restore the parent scope so nested invocations (a node routing a child packet) attribute
            // correctly rather than leaking into whatever ran next.
            if (ReferenceEquals(Current.Value, _state)) Current.Value = _state.Parent;
        }
    }
}

/// <summary>Where per-call model telemetry goes. Separate from <see cref="INodeTelemetrySink"/> because the
/// grain is different: many model calls per invocation.</summary>
public interface IModelCallSink
{
    void Record(ModelCallRecord call);
}

/// <summary>Discards per-call model telemetry.</summary>
public sealed class NullModelCallSink : IModelCallSink
{
    public static NullModelCallSink Instance { get; } = new();
    public void Record(ModelCallRecord call) { }
}
