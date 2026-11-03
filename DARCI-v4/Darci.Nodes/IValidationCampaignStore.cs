#nullable enable

namespace Darci.Nodes;

/// <summary>
/// Durable store of validation campaigns + their per-step evidence (SQLite, consistent with the packet /
/// proposal / innovated stores). The protocol is written once (pre-registration) and the immutable snapshot
/// fields are never rewritten; step evidence accumulates as child packets resolve. The verdict is NOT
/// stored as a source of truth — it is recomputed by <see cref="CampaignProtocol.Evaluate"/> from the
/// pinned protocol × the recorded evidence.
/// </summary>
public interface IValidationCampaignStore
{
    Task InitializeAsync(CancellationToken ct = default);

    Task AddAsync(ValidationCampaign campaign, CancellationToken ct = default);
    Task UpdateAsync(ValidationCampaign campaign, CancellationToken ct = default);

    Task<ValidationCampaign?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationCampaign>> GetByEntryAsync(string entryId, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationCampaign>> GetByStatusAsync(CampaignStatus status, int limit = 100, CancellationToken ct = default);
    Task<IReadOnlyList<ValidationCampaign>> GetByCorrelationAsync(string correlationId, CancellationToken ct = default);

    /// <summary>Record (or replace) the evidence for one step of a campaign. Idempotent per (campaign, step).</summary>
    Task RecordStepEvidenceAsync(string campaignId, StepEvidence evidence, CancellationToken ct = default);
    Task<IReadOnlyList<StepEvidence>> GetStepEvidenceAsync(string campaignId, CancellationToken ct = default);

    /// <summary>Recompute the mechanical verdict for a campaign from its pinned protocol × recorded evidence.</summary>
    Task<CampaignVerdict> ComputeVerdictAsync(string campaignId, CancellationToken ct = default);
}
