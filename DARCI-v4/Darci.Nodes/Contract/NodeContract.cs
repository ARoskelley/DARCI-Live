#nullable enable

namespace Darci.Nodes;

/// <summary>Contract/envelope format version (doc §5.6). The core declares the range it supports.</summary>
public static class NodeContractVersion
{
    /// <summary>The version this core emits.</summary>
    public const string Current = "0.1.1";

    /// <summary>Inclusive range of contract versions this core will register a node for.</summary>
    public static readonly string[] Supported = { "0.1", "0.1.1" };

    public static bool IsSupported(string? version) =>
        version is not null && Array.Exists(Supported, v => string.Equals(v, version, StringComparison.Ordinal));
}

/// <summary>
/// Outcome of a node invocation (doc §5.4, extended in Rev 0.1.1).
/// <para>
/// <b>Rev 0.1.1 amendment:</b> Rev 0.1 had only ok|error, which forced a node with bounded work complete but
/// a goal now waiting on something external to lie — `DEPENDENCY_UNAVAILABLE` means "retry me", which is
/// wrong for a pending human decision. <see cref="Blocked"/> says: "my work finished cleanly; the GOAL is
/// blocked on <see cref="NodeResult.Dependency"/>." The core, which owns the goal/task lifecycle (§3),
/// decides what to do — typically park the work record. The node does not retry and is not retried.
/// </para>
/// </summary>
public enum NodeOutcome
{
    Ok = 0,
    Error = 1,
    /// <summary>Bounded work done; the goal now depends on something outside this invocation.</summary>
    Blocked = 2,
}

/// <summary>What a <see cref="NodeOutcome.Blocked"/> result is waiting on (Rev 0.1.1).</summary>
public enum DependencyKind
{
    /// <summary>A human must decide (approval/authorization). Core parks the work record; see IHumanGate.</summary>
    HumanDecision = 0,
    /// <summary>No environment exists that can run this yet (see ToolingProposal).</summary>
    MissingEnvironment = 1,
    /// <summary>Waiting on a real-world outcome that has not happened yet (e.g. a deployed result).</summary>
    PendingOutcome = 2,
}

/// <summary>The structured "what I'm blocked on" payload of a <see cref="NodeOutcome.Blocked"/> result.</summary>
public sealed record NodeDependency(
    DependencyKind Kind,
    string Detail,
    /// <summary>Optional id of the thing to watch (a proposal id, a campaign id, a correlation root).</summary>
    string? ReferenceId = null);

/// <summary>Error codes (doc §5.4). `Retryable` is advisory to the core; classify honestly.</summary>
public enum NodeErrorCode
{
    InvalidInput = 0,
    PermissionDenied = 1,
    ModelUnavailable = 2,
    DependencyUnavailable = 3,
    DeadlineExceeded = 4,
    NotImplemented = 5,
    Internal = 6,
}

public sealed record NodeError(NodeErrorCode Code, string Message, bool Retryable)
{
    /// <summary>The doc's wire spelling (SCREAMING_SNAKE) for the code.</summary>
    public string WireCode => Code switch
    {
        NodeErrorCode.InvalidInput => "INVALID_INPUT",
        NodeErrorCode.PermissionDenied => "PERMISSION_DENIED",
        NodeErrorCode.ModelUnavailable => "MODEL_UNAVAILABLE",
        NodeErrorCode.DependencyUnavailable => "DEPENDENCY_UNAVAILABLE",
        NodeErrorCode.DeadlineExceeded => "DEADLINE_EXCEEDED",
        NodeErrorCode.NotImplemented => "NOT_IMPLEMENTED",
        NodeErrorCode.Internal => "INTERNAL",
        _ => "INTERNAL",
    };

    /// <summary>The doc's default retry stance per code (§5.4 table). "maybe" is modelled as retryable.</summary>
    public static bool DefaultRetryable(NodeErrorCode code) => code switch
    {
        NodeErrorCode.InvalidInput => false,
        NodeErrorCode.PermissionDenied => false,
        NodeErrorCode.NotImplemented => false,
        _ => true,   // ModelUnavailable / DependencyUnavailable / DeadlineExceeded / Internal
    };

    public static NodeError Of(NodeErrorCode code, string message) => new(code, message, DefaultRetryable(code));
}

/// <summary>Principal trust levels (doc §7). CARRIED BUT NOT ENFORCED in Phase 1 — the trust/taint
/// enforcement model is deferred; these fields exist so the envelope shape is final.</summary>
public enum PrincipalTrust { Untrusted = 0, Collaborator = 1, Operator = 2, System = 3 }

/// <summary>Content taint levels (doc §7). CARRIED BUT NOT ENFORCED in Phase 1 (permissive/no-op).</summary>
public enum TaintLevel { Clean = 0, Derived = 1, Untrusted = 2 }

/// <summary>Who the work is on behalf of. Phase 1 default is the operator; not enforced.</summary>
public sealed record PrincipalRef(PrincipalTrust Trust, string Id)
{
    public static PrincipalRef Operator { get; } = new(PrincipalTrust.Operator, "tinman");
}

/// <summary>Taint marker (doc §5.3/§7). Phase 1: always <see cref="Clean"/> and never checked. Kept on the
/// envelope so the enforcement pass is a behavior change in ONE place, not a schema change everywhere.</summary>
public sealed record TaintRef(TaintLevel Level, IReadOnlyList<string> Sources)
{
    public static TaintRef Clean { get; } = new(TaintLevel.Clean, Array.Empty<string>());

    /// <summary>Taint is monotonic (doc §5.3): a result's taint may never be lower than its request's.</summary>
    public TaintRef RaisedTo(TaintRef other) =>
        other.Level > Level ? other : this;
}

/// <summary>
/// RESERVED, NO-OP in Phase 1 (ADD-5a). The doc's §5.3 `broker{url,token}` field: how an out-of-process node
/// will reach brokered memory/model services. Phase 1 nodes keep their existing injected dependencies —
/// nothing reads this. It exists so the envelope does not change shape when the brokers land (Phase 2).
/// </summary>
public sealed record BrokerRef(string? Url, string? Token)
{
    public static BrokerRef None { get; } = new(null, null);
}
