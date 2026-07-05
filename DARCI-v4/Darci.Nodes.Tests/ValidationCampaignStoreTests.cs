using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class ValidationCampaignStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteValidationCampaignStore _store;

    public ValidationCampaignStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-campaign-{Guid.NewGuid():N}.db");
        _store = new SqliteValidationCampaignStore($"Data Source={_dbPath}", NullLogger<SqliteValidationCampaignStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static ValidationCampaign SampleCampaign(string entryId = "entry-1", CampaignStatus status = CampaignStatus.Draft) => new()
    {
        EntryId = entryId,
        HypothesisRevisionSeq = 3,
        HypothesisSnapshot = "combine EMG threshold detection with a PID grip loop",
        TargetStage = Provenance.ProvisionallyValidated,
        Domain = KnowledgeDomain.Sensitive,
        CorrelationId = "corr-1",
        Status = status,
        Protocol = new[]
        {
            new ValidationStep("s1", ValidationStepKind.SandboxTest, Capability.RunTests, NodeId.Coding,
                new SuccessCriteria("pass_rate", Comparator.GreaterOrEqual, 0.9), "sandbox build+test"),
            new ValidationStep("s2", ValidationStepKind.ExternalResearchCheck, Capability.AnswerKnowledge, NodeId.Knowledge,
                new SuccessCriteria("corroborations", Comparator.GreaterOrEqual, 2), "literature corroboration"),
        },
    };

    [Fact]
    public async Task Add_Get_RoundTripsProtocolAndSnapshot()
    {
        var c = SampleCampaign();
        await _store.AddAsync(c);

        var got = await _store.GetAsync(c.Id);
        Assert.NotNull(got);
        Assert.Equal(c.EntryId, got!.EntryId);
        Assert.Equal(3, got.HypothesisRevisionSeq);
        Assert.Equal(KnowledgeDomain.Sensitive, got.Domain);
        Assert.Equal(2, got.Protocol.Count);
        Assert.Equal("s1", got.Protocol[0].Id);
        Assert.Equal(Comparator.GreaterOrEqual, got.Protocol[0].Criteria.Comparator);
        Assert.Equal(0.9, got.Protocol[0].Criteria.Threshold, 5);
        Assert.Equal(NodeId.Knowledge, got.Protocol[1].Environment);
        Assert.Null(got.Authorization);
    }

    [Fact]
    public async Task Update_PersistsAuthorizationAndStatus()
    {
        var c = SampleCampaign();
        await _store.AddAsync(c);

        var authorized = c with
        {
            Status = CampaignStatus.Authorized,
            Authorization = new CampaignAuthorization("tinman", ApprovedBudget: 10, DateTime.UtcNow, PromotionPreauthorized: false),
        };
        await _store.UpdateAsync(authorized);

        var got = await _store.GetAsync(c.Id);
        Assert.Equal(CampaignStatus.Authorized, got!.Status);
        Assert.NotNull(got.Authorization);
        Assert.Equal("tinman", got.Authorization!.ApprovedBy);
        Assert.Equal(10, got.Authorization.ApprovedBudget);
        Assert.False(got.Authorization.PromotionPreauthorized);
    }

    [Fact]
    public async Task StepEvidence_RecordsAndReplacesPerStep()
    {
        var c = SampleCampaign();
        await _store.AddAsync(c);

        await _store.RecordStepEvidenceAsync(c.Id, new StepEvidence("s1", ValidationStepOutcome.Failed,
            new Dictionary<string, double> { ["pass_rate"] = 0.4 }));
        // A re-run replaces the evidence for the same step (idempotent per campaign+step).
        await _store.RecordStepEvidenceAsync(c.Id, new StepEvidence("s1", ValidationStepOutcome.Passed,
            new Dictionary<string, double> { ["pass_rate"] = 0.95 }, ChildPacketId: "pkt-1"));

        var evidence = await _store.GetStepEvidenceAsync(c.Id);
        Assert.Single(evidence);
        Assert.Equal(ValidationStepOutcome.Passed, evidence[0].Outcome);
        Assert.Equal(0.95, evidence[0].Measurements["pass_rate"], 5);
        Assert.Equal("pkt-1", evidence[0].ChildPacketId);
    }

    [Fact]
    public async Task ComputeVerdict_RecomputesFromEvidence()
    {
        var c = SampleCampaign();
        await _store.AddAsync(c);

        Assert.Equal(CampaignVerdict.Pending, await _store.ComputeVerdictAsync(c.Id));   // no evidence yet

        await _store.RecordStepEvidenceAsync(c.Id, new StepEvidence("s1", ValidationStepOutcome.Passed,
            new Dictionary<string, double> { ["pass_rate"] = 0.95 }));
        await _store.RecordStepEvidenceAsync(c.Id, new StepEvidence("s2", ValidationStepOutcome.Passed,
            new Dictionary<string, double> { ["corroborations"] = 3 }));

        Assert.Equal(CampaignVerdict.Passed, await _store.ComputeVerdictAsync(c.Id));

        // Flip one step below its pre-registered bar → the recomputed verdict fails.
        await _store.RecordStepEvidenceAsync(c.Id, new StepEvidence("s2", ValidationStepOutcome.Passed,
            new Dictionary<string, double> { ["corroborations"] = 1 }));
        Assert.Equal(CampaignVerdict.Failed, await _store.ComputeVerdictAsync(c.Id));
    }

    [Fact]
    public async Task Queries_ByEntry_Status_Correlation()
    {
        await _store.AddAsync(SampleCampaign("entry-A", CampaignStatus.Draft));
        await _store.AddAsync(SampleCampaign("entry-A", CampaignStatus.Authorized));
        await _store.AddAsync(SampleCampaign("entry-B", CampaignStatus.Draft));

        Assert.Equal(2, (await _store.GetByEntryAsync("entry-A")).Count);
        Assert.Equal(2, (await _store.GetByStatusAsync(CampaignStatus.Draft)).Count);
        Assert.Equal(3, (await _store.GetByCorrelationAsync("corr-1")).Count);
    }
}
