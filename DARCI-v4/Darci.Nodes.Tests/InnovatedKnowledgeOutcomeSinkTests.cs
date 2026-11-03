using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class InnovatedKnowledgeOutcomeSinkTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteInnovatedKnowledgeStore _store;

    public InnovatedKnowledgeOutcomeSinkTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-innovsink-{Guid.NewGuid():N}.db");
        _store = new SqliteInnovatedKnowledgeStore($"Data Source={_dbPath}", NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private sealed class RecordingDemotionNotifier : IProvenanceDemotionNotifier
    {
        public int Count;
        public Task NotifyAsync(InnovatedKnowledgeRecord before, InnovatedKnowledgeRecord after, OutcomeFeedback cause, CancellationToken ct = default)
        {
            Count++;
            return Task.CompletedTask;
        }
    }

    private InnovatedKnowledgeOutcomeSink Sink(int severe = 3, IProvenanceDemotionNotifier? notifier = null) =>
        new(_store, new OutcomeFeedbackOptions { SevereFailureRetractThreshold = severe },
            NullLogger<InnovatedKnowledgeOutcomeSink>.Instance, notifier);

    /// <summary>Seed an innovated entry and (optionally) record that it was served into given correlation roots.</summary>
    private async Task<string> SeedAsync(Provenance prov = Provenance.Innovated, params string[] servedRoots)
    {
        var rec = new InnovatedKnowledgeRecord
        {
            Hypothesis = "h", Topic = "t", Intent = "i",
            Provenance = prov, Confidence = Confidence.Of(0.2),
        };
        await _store.AddAsync(rec);
        foreach (var root in servedRoots) await _store.RecordConsumptionAsync(rec.Id, root);
        return rec.Id;
    }

    // ── Success: evidence only, no promotion ──

    [Fact]
    public async Task Success_AppendsEvidence_ButNeverPromotes()
    {
        var id = await SeedAsync(Provenance.Innovated, "r1", "r2", "r3");
        var sink = Sink();

        await sink.ApplyAsync(new OutcomeFeedback("r1", Success: true));
        await sink.ApplyAsync(new OutcomeFeedback("r2", Success: true));
        await sink.ApplyAsync(new OutcomeFeedback("r3", Success: true));

        var rec = await _store.GetAsync(id);
        Assert.Equal(Provenance.Innovated, rec!.Provenance);          // trust NEVER rose automatically
        Assert.Equal(3, rec.SuccessCount);                            // distinct-root evidence
        Assert.True(rec.Confidence.IsLow);                            // within-cap ranking only
        Assert.True(rec.Confidence.Score <= ProvenancePolicy.InnovatedCap);
    }

    [Fact]
    public async Task Retries_OfSameRoot_CollapseToOne()
    {
        var id = await SeedAsync(Provenance.Innovated, "r1");
        var sink = Sink();

        await sink.ApplyAsync(new OutcomeFeedback("r1", Success: true));
        await sink.ApplyAsync(new OutcomeFeedback("r1", Success: true));   // retry, same root
        await sink.ApplyAsync(new OutcomeFeedback("r1", Success: true));   // retry, same root

        Assert.Equal(1, (await _store.GetAsync(id))!.SuccessCount);        // collapsed
    }

    [Fact]
    public async Task IndependentRoots_CountSeparately()
    {
        var id = await SeedAsync(Provenance.Innovated, "r1", "r2");
        var sink = Sink();

        await sink.ApplyAsync(new OutcomeFeedback("r1", Success: true));
        await sink.ApplyAsync(new OutcomeFeedback("r2", Success: true));

        Assert.Equal(2, (await _store.GetAsync(id))!.SuccessCount);
    }

    [Fact]
    public async Task NotServed_NoConsumptionLink_IsNoOp()
    {
        var id = await SeedAsync(Provenance.Innovated /* no served roots */);
        // Outcome arrives for a correlation the entry never consumed → nothing happens.
        await Sink().ApplyAsync(new OutcomeFeedback("random-root", Success: true));

        var rec = await _store.GetAsync(id);
        Assert.Equal(0, rec!.SuccessCount);
        Assert.Equal(Provenance.Innovated, rec.Provenance);
    }

    // ── Failure: soft demotion ──

    [Fact]
    public async Task Failure_FromBottomStage_Retracts()
    {
        var id = await SeedAsync(Provenance.Innovated, "r1");
        await Sink().ApplyAsync(new OutcomeFeedback("r1", Success: false, Evidence: "tests failed"));

        var rec = await _store.GetAsync(id);
        Assert.Equal(Provenance.Retracted, rec!.Provenance);          // bottom stage → Retract
        Assert.Equal(1, rec.FailureCount);
    }

    [Fact]
    public async Task Failure_FromHumanPromotedStage_DemotesOneStage_AndNotifies()
    {
        // A ProvisionallyValidated (human-promoted) entry should demote ONE stage, not retract, and notify.
        var id = await SeedAsync(Provenance.ProvisionallyValidated, "r1");
        var notifier = new RecordingDemotionNotifier();

        await Sink(notifier: notifier).ApplyAsync(new OutcomeFeedback("r1", Success: false));

        var rec = await _store.GetAsync(id);
        Assert.Equal(Provenance.UnderTest, rec!.Provenance);          // one stage down (not Retracted)
        Assert.Equal(1, notifier.Count);                             // UI notified
    }

    [Fact]
    public async Task Failure_Severe_Retracts()
    {
        var id = await SeedAsync(Provenance.UnderTest, "r1", "r2");
        var sink = Sink(severe: 2);

        await sink.ApplyAsync(new OutcomeFeedback("r1", Success: false));   // 1 failure → demote UnderTest→Innovated
        await sink.ApplyAsync(new OutcomeFeedback("r2", Success: false));   // 2 failures ≥ severe → Retracted

        Assert.Equal(Provenance.Retracted, (await _store.GetAsync(id))!.Provenance);
    }

    [Fact]
    public async Task Outcome_LeavesTrustedKnowledgeUnpromotable_ButDoesNotRaiseIt()
    {
        // A researched (trusted) entry that happens to be consumed: a success must not touch its trust.
        var rec = new InnovatedKnowledgeRecord
        {
            Hypothesis = "established", Topic = "t", Intent = "i",
            Provenance = Provenance.Researched, Confidence = Confidence.Of(0.8),
        };
        await _store.AddAsync(rec);
        await _store.RecordConsumptionAsync(rec.Id, "r1");

        await Sink().ApplyAsync(new OutcomeFeedback("r1", Success: true));

        var loaded = await _store.GetAsync(rec.Id);
        Assert.Equal(Provenance.Researched, loaded!.Provenance);      // unchanged (success never promotes)
    }

    [Fact]
    public async Task Success_LogsStructuredEvidenceRevision()
    {
        var id = await SeedAsync(Provenance.Innovated, "r1");
        await Sink().ApplyAsync(new OutcomeFeedback("r1", Success: true, Evidence: "green"));

        var revs = await _store.GetRevisionsAsync(id);
        Assert.Equal(2, revs.Count);                                 // created + success evidence
        Assert.Equal(LedgerEventKind.SuccessEvidence, revs[1].Kind);
        Assert.Equal("r1", revs[1].CorrelationRoot);                 // structured, not a prose blob
        Assert.False(Ledger.IsHumanAuthored(revs[1].Kind));          // automatic
    }
}
