#nullable enable

using System.Text;
using System.Text.Json;
using Darci.Nodes;
using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests.Characterization;

/// <summary>
/// SU0 — the CHARACTERIZATION BASELINE harness (the "golden oracle" for the Phase 1 core carve).
///
/// It wires the REAL cross-subsystem graph — <see cref="NodeRouter"/>, <see cref="KnowledgeNode"/>,
/// <see cref="InnovationNode"/>, <see cref="GapHandler"/>, all four SQLite stores,
/// <see cref="InnovatedKnowledgeOutcomeSink"/>, <see cref="HumanGateService"/>,
/// <see cref="CampaignCoordinator"/> — and fakes ONLY two things:
///   1. the LLM boundary (a canned <see cref="IKnowledgePipeline"/> / <see cref="IInnovationLoop"/>), and
///   2. the coding loop's internals (<see cref="FakeCodingNode"/> stands in for CodingNode/CodingAgentLoop,
///      which needs a real workspace, real model, and real process execution — non-deterministic and NOT
///      what we are characterizing. It reproduces the coding node's OBSERVABLE packet-level behavior:
///      gated escalation to a knowledge child, and the terminal OutcomeFeedback emit).
///
/// Purpose: unit tests cannot catch cross-subsystem WIRING drift. This can. Capture a
/// <see cref="BehaviorSnapshot"/> before the carve, then re-run after each risky sub-unit; any diff in the
/// snapshot is behavior change and must be explained or reverted.
/// </summary>
internal sealed class CharacterizationHarness : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly string _dbPath;

    public SqliteNodePacketStore Packets { get; }
    public SqliteInnovatedKnowledgeStore Innovated { get; }
    public SqliteProposalStore Proposals { get; }
    public SqliteGapStore Gaps { get; }
    public SqliteValidationCampaignStore Campaigns { get; }

    public NodeRouter Router { get; }
    public FakeCodingNode Coding { get; }
    public InnovatedKnowledgeOutcomeSink Sink { get; }

    /// <summary>Every OutcomeFeedback the (faked) coding loop emitted: the correlation root it used.</summary>
    public List<string> EmittedOutcomeRoots { get; } = new();

    private readonly List<INode> _nodes = new();

    public CharacterizationHarness(
        KnowledgeResponse? pipelineResponse = null,
        InnovationProposal? innovationResult = null,
        bool codingEscalates = true)
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-char-{Guid.NewGuid():N}.db");
        var conn = $"Data Source={_dbPath}";

        Packets = new SqliteNodePacketStore(conn, NullLogger<SqliteNodePacketStore>.Instance);
        Innovated = new SqliteInnovatedKnowledgeStore(conn, NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        Proposals = new SqliteProposalStore(conn, NullLogger<SqliteProposalStore>.Instance);
        Gaps = new SqliteGapStore(conn, NullLogger<SqliteGapStore>.Instance);
        Campaigns = new SqliteValidationCampaignStore(conn, NullLogger<SqliteValidationCampaignStore>.Instance);
        Packets.InitializeAsync().GetAwaiter().GetResult();
        Innovated.InitializeAsync().GetAwaiter().GetResult();
        Proposals.InitializeAsync().GetAwaiter().GetResult();
        Gaps.InitializeAsync().GetAwaiter().GetResult();
        Campaigns.InitializeAsync().GetAwaiter().GetResult();

        Sink = new InnovatedKnowledgeOutcomeSink(Innovated, new OutcomeFeedbackOptions(),
            NullLogger<InnovatedKnowledgeOutcomeSink>.Instance);

        // Lazy router reference — mirrors the real DI cycle-breaking (node → handler → router → node).
        var lazyRouter = new Lazy<INodeRouter>(() => Router!);

        var gapHandler = new GapHandler(Gaps, lazyRouter, new GapHandlerOptions(),
            NullLogger<GapHandler>.Instance);

        // REAL InnovationNode; only the loop (the LLM half) is faked.
        var innovationNode = new InnovationNode(
            new FakeInnovationLoop(innovationResult ?? InnovationProposal.CannotSolve(
                "no known combination works", new[] { "the TB-7 spec section" })),
            Innovated, NullLogger<InnovationNode>.Instance, Proposals);

        // REAL KnowledgeNode; only the pipeline (the LLM half) is faked.
        var knowledgeNode = new KnowledgeNode(
            new FakePipeline(pipelineResponse ?? KnowledgeResponse.Unanswered("the TB-7 checksum rule is unknown")),
            NullLogger<KnowledgeNode>.Instance, gapHandler, lazyRouter);

        Coding = new FakeCodingNode(lazyRouter, Sink, EmittedOutcomeRoots, codingEscalates);

        _nodes.Add(Coding);
        _nodes.Add(knowledgeNode);
        _nodes.Add(innovationNode);

        Router = NodeRouter.ForNodes(_nodes, Packets, NullLogger<NodeRouter>.Instance);
    }

    public IReadOnlyList<INode> Nodes => _nodes;

    public HumanGateService Gate(ICampaignCoordinator? coordinator = null) =>
        new(Proposals, Innovated, Packets, NullLogger<HumanGateService>.Instance, coordinator);

    public CampaignCoordinator Coordinator(IProtocolCritic? critic = null) =>
        new(Campaigns, Innovated, Proposals, Router, Packets, Gaps,
            critic ?? new FakeAdequateProtocolCritic(), _nodes,
            NullLogger<CampaignCoordinator>.Instance);

    /// <summary>Run the top-level coding task exactly as the API does: create → route → dispatch.</summary>
    public async Task<NodePacket> RunCodingTaskAsync(string intent = "implement the TB-7 encoder")
    {
        var packet = NodePacket.Create(intent, capability: Capability.WriteCode);
        return await Router.DispatchAsync(packet);
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    // ────────────────────────────── snapshot ──────────────────────────────

    /// <summary>
    /// Capture the normalized, deterministic behavior snapshot for a correlation root. Ids and timestamps
    /// are replaced with stable labels so the snapshot is comparable across runs; what remains is the
    /// SHAPE of the behavior (chains, states, table contents, evidence kinds) plus the correlation-root
    /// IDENTITY relationships that the evidence loop depends on.
    /// </summary>
    public async Task<BehaviorSnapshot> SnapshotAsync(string correlationRoot)
    {
        var packets = await Packets.GetByCorrelationAsync(correlationRoot);
        var chains = packets
            .Select(p => new PacketChain(
                Capability: p.RequestedCapability?.ToString() ?? "—",
                Address: p.Address?.ToString() ?? "—",
                States: string.Join(">", p.Log.Select(l => l.StateAfter.ToString())),
                Terminal: p.State.ToString(),
                LeaseHeld: p.LeaseExpiresAt is not null))
            .OrderBy(c => c.Capability, StringComparer.Ordinal)
            .ThenBy(c => c.States, StringComparer.Ordinal)
            .ToList();

        var entries = await Innovated.GetByCorrelationAsync(correlationRoot);
        var innovated = new List<InnovatedRow>();
        var consumption = new List<ConsumptionRow>();
        var revisions = new List<string>();
        foreach (var e in entries.OrderBy(e => e.Hypothesis, StringComparer.Ordinal))
        {
            innovated.Add(new InnovatedRow(e.Provenance.ToString(), Round(e.Confidence.Score),
                e.Confidence.IsLow, e.SuccessCount, e.FailureCount));
            foreach (var c in (await Innovated.GetConsumptionsAsync(e.Id)).OrderBy(c => c.CorrelationRoot, StringComparer.Ordinal))
                consumption.Add(new ConsumptionRow(c.Outcome.ToString(),
                    RootIsCorrelation: c.CorrelationRoot == correlationRoot,
                    Resolved: c.ResolvedAt is not null));
            revisions.AddRange((await Innovated.GetRevisionsAsync(e.Id)).OrderBy(r => r.Seq).Select(r => r.Kind.ToString()));
        }

        var gaps = (await Gaps.GetByCorrelationAsync(correlationRoot))
            .GroupBy(g => g.Status, StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => $"{g.Key}={g.Count()}")
            .ToList();

        var proposals = (await Proposals.GetByCorrelationAsync(correlationRoot))
            .OrderBy(p => p.Kind.ToString(), StringComparer.Ordinal)
            .Select(p => $"{p.Kind}:{p.Status}:parked={p.ParkedPacketId is not null}")
            .ToList();

        var campaigns = (await Campaigns.GetByCorrelationAsync(correlationRoot))
            .OrderBy(c => c.Id, StringComparer.Ordinal)
            .Select(c => $"{c.Status}:{c.TargetStage}:{c.Domain}:prio={c.Priority}:preauth={c.PromotionPreauthorized}")
            .ToList();

        return new BehaviorSnapshot(chains, innovated, consumption, revisions, gaps, proposals, campaigns,
            OutcomeEmissions: EmittedOutcomeRoots.Count,
            OutcomeEmissionsUnderRoot: EmittedOutcomeRoots.Count(r => r == correlationRoot));
    }

    private static double Round(double d) => Math.Round(d, 4);

    // ────────────────────────────── fakes ──────────────────────────────

    /// <summary>Canned knowledge pipeline (the LLM boundary).</summary>
    internal sealed class FakePipeline : IKnowledgePipeline
    {
        private readonly KnowledgeResponse _response;
        public int CallCount;
        public FakePipeline(KnowledgeResponse response) => _response = response;
        public Task<KnowledgeResponse> RunAsync(KnowledgeRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_response);
        }
    }

    /// <summary>Canned innovation loop (the LLM boundary). The REAL InnovationNode wraps this, so the
    /// serve point — persist + RecordConsumptionAsync + FileProposalAsync — is exercised for real.</summary>
    internal sealed class FakeInnovationLoop : IInnovationLoop
    {
        private readonly InnovationProposal _proposal;
        public int CallCount;
        public FakeInnovationLoop(InnovationProposal proposal) => _proposal = proposal;
        public Task<InnovationProposal> RunAsync(InnovationRequest request, CancellationToken ct = default)
        {
            CallCount++;
            return Task.FromResult(_proposal);
        }
    }

    internal sealed class FakeAdequateProtocolCritic : IProtocolCritic
    {
        public Task<ProtocolCritique> FalsifyAsync(ValidationCampaign campaign, CancellationToken ct = default)
            => Task.FromResult(new ProtocolCritique(Array.Empty<string>(), true, "adequate"));
    }

    /// <summary>
    /// Stands in for CodingNode + CodingAgentLoop at the INode boundary. Reproduces the observable
    /// packet-level behavior we care about: (1) gated escalation — dispatch a BLOCKING FillKnowledgeGap
    /// child under the SAME correlation root; (2) terminal OutcomeFeedback emitted under that root
    /// (exactly what CodingAgentLoop.EmitOutcomeFeedbackAsync does); (3) validation-step measurements when
    /// invoked as a campaign step child.
    /// </summary>
    internal sealed class FakeCodingNode : INode
    {
        private readonly Lazy<INodeRouter> _router;
        private readonly IOutcomeFeedbackSink _sink;
        private readonly List<string> _emitted;
        private readonly bool _escalates;

        public FakeCodingNode(Lazy<INodeRouter> router, IOutcomeFeedbackSink sink, List<string> emitted, bool escalates)
        {
            _router = router;
            _sink = sink;
            _emitted = emitted;
            _escalates = escalates;
        }

        public NodeId Id => NodeId.Coding;

        public IReadOnlySet<Capability> Capabilities { get; } =
            new HashSet<Capability> { Capability.WriteCode, Capability.RunTests };

        public async Task<NodePacket> HandleAsync(NodePacket packet, CancellationToken ct = default)
        {
            if (packet.State == NodeState.Routed)
                packet = packet.Transition(NodeId.Coding, NodeState.Accepted, "Coding loop accepted task");
            if (packet.State == NodeState.Accepted)
                packet = packet.Transition(NodeId.Coding, NodeState.Working, "Coding loop started",
                    leaseFor: TimeSpan.FromMinutes(10));

            // A campaign validation step: report measurements so the mechanical verdict can be computed.
            var stepId = packet.Payload.Slot(PacketSlots.CampaignStepId);
            if (!string.IsNullOrWhiteSpace(stepId))
            {
                packet = packet.WithSlot(PacketSlots.StepMeasurements,
                    JsonSerializer.Serialize(new Dictionary<string, double> { ["pass_rate"] = 1.0 }, JsonOpts));
                return packet.Transition(NodeId.Coding, NodeState.Succeeded, "Validation step ran", success: true);
            }

            // Gated escalation: route a blocking knowledge gap under the SAME correlation root.
            if (_escalates)
            {
                var child = NodePacket.Create(
                    intent: packet.Payload.Intent,
                    capability: Capability.FillKnowledgeGap,
                    correlationId: packet.CorrelationId,
                    slots: new Dictionary<string, string>
                    {
                        [PacketSlots.Question] = "what is the TB-7 checksum rule (spec TB7-4.2)?",
                        [PacketSlots.Blocking] = "true",
                    });
                packet = packet.Transition(NodeId.Coding, NodeState.Working,
                    "Gated escalation: routed knowledge gap to knowledge node.");
                await _router.Value.DispatchAsync(child, ct);
            }

            var terminal = packet.Transition(NodeId.Coding, NodeState.Succeeded,
                "Coding loop finished with status 'completed'", success: true);

            // The outcome-feedback emit, keyed on the CORRELATION ROOT (never a per-invocation id).
            _emitted.Add(terminal.CorrelationId);
            await _sink.ApplyAsync(new OutcomeFeedback(terminal.CorrelationId, Success: true,
                Evidence: "coding run terminal status 'completed'", TerminalStatus: "completed"), ct);

            return terminal;
        }
    }
}

// ────────────────────────────── snapshot value types ──────────────────────────────

internal sealed record PacketChain(string Capability, string Address, string States, string Terminal, bool LeaseHeld);
internal sealed record InnovatedRow(string Provenance, double Score, bool IsLow, int Successes, int Failures);
internal sealed record ConsumptionRow(string Outcome, bool RootIsCorrelation, bool Resolved);

/// <summary>A normalized, deterministic capture of observed cross-subsystem behavior.</summary>
internal sealed record BehaviorSnapshot(
    IReadOnlyList<PacketChain> Chains,
    IReadOnlyList<InnovatedRow> Innovated,
    IReadOnlyList<ConsumptionRow> Consumption,
    IReadOnlyList<string> RevisionKinds,
    IReadOnlyList<string> GapStatuses,
    IReadOnlyList<string> Proposals,
    IReadOnlyList<string> Campaigns,
    int OutcomeEmissions,
    int OutcomeEmissionsUnderRoot)
{
    /// <summary>Stable text rendering — this is what gets asserted, so a drift shows as a readable diff.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine("PACKETS:");
        foreach (var c in Chains)
            sb.AppendLine($"  cap={c.Capability} addr={c.Address} states={c.States} terminal={c.Terminal} lease={c.LeaseHeld}");
        sb.AppendLine("INNOVATED:");
        foreach (var i in Innovated)
            sb.AppendLine($"  prov={i.Provenance} score={i.Score:0.####} isLow={i.IsLow} s={i.Successes} f={i.Failures}");
        sb.AppendLine("CONSUMPTION:");
        foreach (var c in Consumption)
            sb.AppendLine($"  outcome={c.Outcome} rootIsCorrelation={c.RootIsCorrelation} resolved={c.Resolved}");
        sb.AppendLine($"REVISIONS: {string.Join(",", RevisionKinds)}");
        sb.AppendLine($"GAPS: {string.Join(",", GapStatuses)}");
        sb.AppendLine($"PROPOSALS: {string.Join(",", Proposals)}");
        sb.AppendLine($"CAMPAIGNS: {string.Join(",", Campaigns)}");
        sb.AppendLine($"OUTCOME_EMISSIONS: total={OutcomeEmissions} underRoot={OutcomeEmissionsUnderRoot}");
        return sb.ToString();
    }
}
