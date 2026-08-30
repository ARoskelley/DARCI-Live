#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>The result of a sandbox proof-of-concept run attached to an entry as objective evidence. The
/// <see cref="Weight"/> is the amount this run was actually allowed to contribute (post cap) — a compiler
/// can't be flattered, but nor can the node lift its own entry by testing itself all day.</summary>
public sealed record ProofOfConcept(
    bool Passed,
    string Summary,
    IReadOnlyDictionary<string, double> Measurements,
    double Weight,
    string? ChildPacketId);

public sealed class SandboxPoCOptions
{
    /// <summary>Weight a single sandbox run may contribute.</summary>
    public double PerRunWeight { get; init; } = 0.25;
    /// <summary>HARD ceiling on the TOTAL self-generated (sandbox) evidence weight for one entry — the node
    /// cannot exceed this no matter how many times it tests itself.</summary>
    public double SandboxWeightCap { get; init; } = 0.5;
    public Capability Capability { get; init; } = Capability.RunTests;
    public NodeId Environment { get; init; } = NodeId.Coding;
}

/// <summary>
/// The objective / sandbox proof-of-concept gate (decision #4 expansion, §8 layer 4). The innovation flow
/// routes a candidate to the coding node for a sandboxed build/dry-run and attaches the result as
/// PROOF-OF-CONCEPT evidence BEFORE a human sees the proposal — a critic can be flattered, a compiler
/// cannot. Self-generated evidence is WEIGHT-CAPPED (Fable): its total contribution to an entry is bounded,
/// and it never changes provenance or confidence — only real deployed outcomes and humans move trust.
/// </summary>
public interface ISandboxPoCGate
{
    /// <summary>Run a sandbox PoC for the entry and append weight-capped evidence. Returns null if no
    /// sandbox environment exists (not an error — just no PoC available).</summary>
    Task<ProofOfConcept?> AttachAsync(InnovatedKnowledgeRecord entry, CancellationToken ct = default);

    /// <summary>Total self-generated sandbox evidence weight already accumulated for an entry.</summary>
    Task<double> AccumulatedSandboxWeightAsync(string entryId, CancellationToken ct = default);
}

public sealed class SandboxPoCGate : ISandboxPoCGate
{
    /// <summary>Ledger CorrelationRoot marking an evidence event as self-generated sandbox PoC (so its
    /// weight can be summed and capped, and distinguished from real deployed-outcome evidence).</summary>
    public const string SandboxRoot = "sandbox:poc";

    private readonly INodeRouter _router;
    private readonly IInnovatedKnowledgeStore _innovated;
    private readonly IReadOnlyList<INode> _nodes;
    private readonly SandboxPoCOptions _options;
    private readonly ILogger<SandboxPoCGate> _logger;

    public SandboxPoCGate(
        INodeRouter router,
        IInnovatedKnowledgeStore innovated,
        IEnumerable<INode> nodes,
        SandboxPoCOptions options,
        ILogger<SandboxPoCGate> logger)
    {
        _router = router;
        _innovated = innovated;
        _nodes = nodes.ToList();
        _options = options;
        _logger = logger;
    }

    public async Task<double> AccumulatedSandboxWeightAsync(string entryId, CancellationToken ct = default)
    {
        var revs = await _innovated.GetRevisionsAsync(entryId, ct);
        return revs.Where(r => r.CorrelationRoot == SandboxRoot).Sum(r => r.Weight);
    }

    public async Task<ProofOfConcept?> AttachAsync(InnovatedKnowledgeRecord entry, CancellationToken ct = default)
    {
        if (!EnvironmentExists())
        {
            _logger.LogDebug("No sandbox environment ({Cap}/{Env}); skipping PoC for {Id}.", _options.Capability, _options.Environment, entry.Id);
            return null;
        }

        var child = NodePacket.Create(
            intent: $"Sandbox proof-of-concept for hypothesis: {entry.Hypothesis}",
            address: _options.Environment,
            capability: _options.Capability,
            correlationId: string.IsNullOrEmpty(entry.CorrelationId) ? entry.Id : entry.CorrelationId,
            slots: new Dictionary<string, string> { [PacketSlots.Question] = entry.Hypothesis });

        var result = await _router.DispatchAsync(child, ct);

        // Nothing ran, so there is nothing to learn. Writing FailureEvidence here would demote a hypothesis
        // because the OPERATOR is missing a sandbox node — punishing an idea for the host's configuration.
        if (result.State == NodeState.Blocked)
        {
            _logger.LogInformation(
                "Sandbox PoC for {Id} skipped: no node serves the sandbox capability — no evidence recorded.",
                entry.Id);
            return null;
        }

        var passed = result.State.IsSuccess() && (result.LastEntry?.Success ?? false);
        var measurements = ParseMeasurements(result.Payload.Slot(PacketSlots.StepMeasurements));

        // WEIGHT CAP: clamp this run's contribution so cumulative sandbox weight never exceeds the ceiling.
        var used = await AccumulatedSandboxWeightAsync(entry.Id, ct);
        var weight = Math.Max(0.0, Math.Min(_options.PerRunWeight, _options.SandboxWeightCap - used));

        // Append evidence — provenance and confidence are UNCHANGED (self-testing never moves trust).
        var fresh = await _innovated.GetAsync(entry.Id, ct) ?? entry;
        await _innovated.UpdateAsync(
            fresh,
            new LedgerEvent(
                passed ? LedgerEventKind.SuccessEvidence : LedgerEventKind.FailureEvidence,
                $"sandbox PoC {(passed ? "passed" : "failed")}",
                CorrelationRoot: SandboxRoot,
                Weight: weight,
                Note: "self-generated sandbox proof-of-concept (weight-capped)"),
            ct);

        _logger.LogInformation("Sandbox PoC for {Id}: {Result} (weight {W:0.###}, cumulative {C:0.###}/{Cap}).",
            entry.Id, passed ? "passed" : "failed", weight, used + weight, _options.SandboxWeightCap);

        return new ProofOfConcept(passed, result.LastEntry?.Decision ?? "", measurements, weight, result.Id);
    }

    private bool EnvironmentExists()
        => _nodes.Any(n => n.Id == _options.Environment || n.Capabilities.Contains(_options.Capability));

    private static IReadOnlyDictionary<string, double> ParseMeasurements(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new Dictionary<string, double>();
        try { return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, double>>(json) ?? new(); }
        catch { return new Dictionary<string, double>(); }
    }
}
