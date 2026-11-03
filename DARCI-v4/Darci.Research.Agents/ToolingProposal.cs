#nullable enable

using System.Text;
using System.Text.Json;
using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>
/// A DATA-ONLY proposal for new node/tooling the system needs (§14c). It is a description, never an action:
/// it NEVER self-modifies and NEVER registers a node at runtime — node registration stays compile-time; a
/// human builds it in a normal dev session. Extending DARCI's capability surface is an upward crossing,
/// treated identically to the trust boundary (§0a), so this only ever PROPOSES. Must be demand-driven:
/// it cites the blocked campaign steps / open gaps that justify it.
/// </summary>
public sealed record ToolingProposal(
    string Purpose,
    Capability CapabilitySought,
    NodeId ProposedEnvironment,
    string ContractSketch,                       // sketched INode/Capability contract (prose/pseudocode) — for a human to build
    IReadOnlyList<string> BlockedCampaignIds,
    IReadOnlyList<string> BlockedStepIds,
    IReadOnlyList<string> OpenGapIds)
{
    /// <summary>Demand evidence: how many concrete blockers (steps + gaps) justify this tooling.</summary>
    public int DemandCount => BlockedStepIds.Count + OpenGapIds.Count;
}

/// <summary>The tooling critic's review — chiefly "what existing environment could run this instead?" — so
/// the capability surface isn't extended when something already covers the need.</summary>
public sealed record ToolingCritique(
    IReadOnlyList<string> ExistingAlternatives,
    bool NeedsNewCapability,
    string Summary);

public interface IToolingCritic
{
    Task<ToolingCritique> ReviewAsync(ToolingProposal proposal, CancellationToken ct = default);
}

public sealed class OllamaToolingCritic : IToolingCritic
{
    private readonly IResearchToolbox _toolbox;
    private readonly ILogger<OllamaToolingCritic> _logger;

    public OllamaToolingCritic(IResearchToolbox toolbox, ILogger<OllamaToolingCritic> logger)
    {
        _toolbox = toolbox;
        _logger = logger;
    }

    public async Task<ToolingCritique> ReviewAsync(ToolingProposal proposal, CancellationToken ct = default)
    {
        string raw;
        try { raw = await _toolbox.GenerateAsync(BuildPrompt(proposal), ct); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug(ex, "Tooling critic unavailable; defaulting to needs-new-capability=false (conservative — prefer reuse).");
            return new ToolingCritique(System.Array.Empty<string>(), NeedsNewCapability: false, "Critic unavailable; a human should check for an existing environment first.");
        }
        return Parse(raw) ?? new ToolingCritique(System.Array.Empty<string>(), false, "Unparseable critic output.");
    }

    private static string BuildPrompt(ToolingProposal p)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A system wants to add a NEW capability/environment. Your job is to PREVENT unnecessary");
        sb.AppendLine("capability growth: name any EXISTING environment or tool that could satisfy this need");
        sb.AppendLine("instead, and judge whether a genuinely new capability is required.");
        sb.AppendLine();
        sb.AppendLine($"PURPOSE: {p.Purpose}");
        sb.AppendLine($"CAPABILITY SOUGHT: {p.CapabilitySought} (proposed node: {p.ProposedEnvironment})");
        sb.AppendLine($"CONTRACT SKETCH: {p.ContractSketch}");
        sb.AppendLine("Respond with ONLY this JSON (no prose):");
        sb.AppendLine("""{"existingAlternatives": ["..."], "needsNewCapability": true|false, "summary": "one sentence"}""");
        sb.AppendLine("JSON:");
        return sb.ToString();
    }

    internal static ToolingCritique? Parse(string raw)
    {
        var json = JsonExtraction.FirstObject(raw);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var alts = new List<string>();
            if (root.TryGetProperty("existingAlternatives", out var a) && a.ValueKind == JsonValueKind.Array)
                foreach (var e in a.EnumerateArray())
                    if (e.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(e.GetString()))
                        alts.Add(e.GetString()!.Trim());
            var needs = root.TryGetProperty("needsNewCapability", out var n) && n.ValueKind == JsonValueKind.True;
            var summary = root.TryGetProperty("summary", out var s) ? s.GetString()?.Trim() ?? "" : "";
            return new ToolingCritique(alts, needs, summary);
        }
        catch { return null; }
    }
}

public sealed class ToolingProposalOptions
{
    /// <summary>Max concurrently-open tooling proposals per sought capability (rate limit / dedupe).</summary>
    public int MaxOpenPerCapability { get; init; } = 1;
}

/// <summary>Files a <see cref="ToolingProposal"/> into the ProposalStore as a data-only, human-decided
/// request. Demand-driven (refuses if nothing is cited), rate-limited (dedupes per capability), and
/// critic-reviewed. Approval is NOT registration — a human builds the node at compile time.</summary>
public interface IToolingProposalEmitter
{
    /// <summary>File the proposal, or return null if it was refused (no demand) or suppressed (rate limit).</summary>
    Task<HumanProposal?> EmitAsync(ToolingProposal proposal, CancellationToken ct = default);
}

public sealed class ToolingProposalEmitter : IToolingProposalEmitter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = false };

    private readonly IProposalStore _proposals;
    private readonly IToolingCritic _critic;
    private readonly ToolingProposalOptions _options;
    private readonly ILogger<ToolingProposalEmitter> _logger;

    public ToolingProposalEmitter(
        IProposalStore proposals,
        IToolingCritic critic,
        ToolingProposalOptions options,
        ILogger<ToolingProposalEmitter> logger)
    {
        _proposals = proposals;
        _critic = critic;
        _options = options;
        _logger = logger;
    }

    public async Task<HumanProposal?> EmitAsync(ToolingProposal proposal, CancellationToken ct = default)
    {
        // Demand-driven: must cite at least one concrete blocker.
        if (proposal.DemandCount < 1)
        {
            _logger.LogInformation("Tooling proposal for {Cap} refused — no demand cited.", proposal.CapabilitySought);
            return null;
        }

        // Rate-limit / dedupe: cap concurrently-open proposals for the same capability.
        var subject = proposal.CapabilitySought.ToString();
        var openForCap = (await _proposals.GetPendingAsync(ct: ct))
            .Count(p => p.Kind == HumanProposalKind.ProposeTooling && p.SubjectId == subject);
        if (openForCap >= _options.MaxOpenPerCapability)
        {
            _logger.LogInformation("Tooling proposal for {Cap} suppressed — {N} already open (limit {Lim}).",
                proposal.CapabilitySought, openForCap, _options.MaxOpenPerCapability);
            return null;
        }

        var critique = await _critic.ReviewAsync(proposal, ct);

        var hp = new HumanProposal
        {
            CorrelationId = proposal.BlockedCampaignIds.FirstOrDefault() ?? "",
            Kind = HumanProposalKind.ProposeTooling,
            SubjectId = subject,
            Title = $"Build tooling: {proposal.CapabilitySought} ({proposal.ProposedEnvironment})",
            Summary = proposal.Purpose,
            JustificationJson = JsonSerializer.Serialize(new { proposal, critique }, JsonOpts),
            // No parked packet: data-only; nothing is blocked ON this proposal (the campaign parks on its gap).
        };
        await _proposals.AddAsync(hp, ct);
        _logger.LogInformation("Filed tooling proposal {Id} for capability {Cap} (demand {D}).",
            hp.Id, proposal.CapabilitySought, proposal.DemandCount);
        return hp;
    }
}
