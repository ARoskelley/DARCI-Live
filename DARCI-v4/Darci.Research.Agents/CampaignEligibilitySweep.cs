#nullable enable

using Darci.Nodes;
using Microsoft.Extensions.Logging;

namespace Darci.Research.Agents;

/// <summary>Tuning for the auto-draft eligibility sweep. Off by default — auto-drafting is opt-in.</summary>
public sealed class CampaignSweepOptions
{
    /// <summary>Master switch. When false the sweep is a no-op (nothing auto-drafts).</summary>
    public bool Enabled { get; init; }
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(15);
    /// <summary>How many innovated entries to scan per tick.</summary>
    public int MaxEntriesPerSweep { get; init; } = 25;
    /// <summary>Cap on how many campaigns one tick may auto-draft (throttle).</summary>
    public int MaxDraftsPerSweep { get; init; } = 3;
    /// <summary>Stage an auto-drafted campaign seeks (the mid tier; never the trusted top tier).</summary>
    public Provenance TargetStage { get; init; } = Provenance.ProvisionallyValidated;
}

/// <summary>
/// The auto-draft eligibility sweep (the testable core; the hosted service just ticks it). On each run it
/// scans Innovated entries, and for those the pure <see cref="CampaignEligibilityEvaluator"/> deems eligible
/// — and which don't already have a live campaign — it calls <see cref="ICampaignCoordinator"/> to DRAFT a
/// campaign at <see cref="CampaignPriority.AutoDrafted"/>. It automates ONLY the draft: the campaign still
/// parks for human authorization → protocol critic → human authorizes → steps → verdict → promotion. It
/// never authorizes, runs, or promotes anything. Modelled on NodeWatchdog (logic) + its hosted service.
/// </summary>
public sealed class CampaignEligibilitySweep
{
    private readonly ICampaignCoordinator _coordinator;
    private readonly IInnovatedKnowledgeStore _innovated;
    private readonly IValidationCampaignStore _campaigns;
    private readonly CampaignEligibilityOptions _eligibility;
    private readonly CampaignSweepOptions _options;
    private readonly ILogger<CampaignEligibilitySweep> _logger;

    public CampaignEligibilitySweep(
        ICampaignCoordinator coordinator,
        IInnovatedKnowledgeStore innovated,
        IValidationCampaignStore campaigns,
        CampaignEligibilityOptions eligibility,
        CampaignSweepOptions options,
        ILogger<CampaignEligibilitySweep> logger)
    {
        _coordinator = coordinator;
        _innovated = innovated;
        _campaigns = campaigns;
        _eligibility = eligibility;
        _options = options;
        _logger = logger;
    }

    /// <summary>Run one sweep. Returns the number of campaigns auto-drafted.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct = default)
    {
        if (!_options.Enabled) return 0;

        var entries = await _innovated.GetByProvenanceAsync(Provenance.Innovated, _options.MaxEntriesPerSweep, ct);
        var drafted = 0;

        foreach (var entry in entries)
        {
            if (drafted >= _options.MaxDraftsPerSweep) break;
            ct.ThrowIfCancellationRequested();

            var outcomes = await _innovated.CountDistinctOutcomesAsync(entry.Id, ct);
            if (!CampaignEligibilityEvaluator.Evaluate(entry, outcomes, _eligibility).Eligible) continue;

            // De-dupe: never auto-draft a second campaign for an entry that already has one in flight.
            var existing = await _campaigns.GetByEntryAsync(entry.Id, ct);
            if (existing.Any(c => c.Status.IsActive())) continue;

            var domain = DomainClassifier.Classify(null, entry.Hypothesis, entry.Topic, entry.Intent);
            var protocol = CampaignProtocols.Default(domain);

            // parentPacket=null → coordinator mints one and PARKS it for human authorization.
            // preauthorizePromotion=false → an auto-drafted campaign never pre-authorizes the 2nd touch.
            await _coordinator.DraftAndRequestAuthorizationAsync(
                entry, protocol, _options.TargetStage, domain,
                parentPacket: null, preauthorizePromotion: false, priority: CampaignPriority.AutoDrafted, ct);
            drafted++;
            _logger.LogInformation("Auto-drafted campaign for entry {Id} ({Domain}); parked for human authorization.", entry.Id, domain);
        }

        if (drafted > 0) _logger.LogInformation("Eligibility sweep auto-drafted {N} campaign(s).", drafted);
        return drafted;
    }
}
