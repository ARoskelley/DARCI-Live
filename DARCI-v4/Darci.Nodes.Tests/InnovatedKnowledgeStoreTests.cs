using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests;

public sealed class InnovatedKnowledgeStoreTests : IDisposable
{
    private readonly string _dbPath;
    private readonly SqliteInnovatedKnowledgeStore _store;

    public InnovatedKnowledgeStoreTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), $"darci-innov-{Guid.NewGuid():N}.db");
        _store = new SqliteInnovatedKnowledgeStore($"Data Source={_dbPath}", NullLogger<SqliteInnovatedKnowledgeStore>.Instance);
        _store.InitializeAsync().GetAwaiter().GetResult();
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch { }
    }

    private static InnovatedKnowledgeRecord Sample(double conf = 0.2, string corr = "corr-1") => new()
    {
        CorrelationId = corr,
        OriginPacketId = "pkt-1",
        Hypothesis = "combine A and B via bridge C",
        Topic = "how to bridge A and B",
        Intent = "implement the prosthetic controller",
        CitedFactIds = new[] { "f1", "f2" },
        Provenance = Provenance.Innovated,
        Confidence = Confidence.Of(conf),
    };

    [Fact]
    public async Task AddAndGet_RoundTrips_WithContext()
    {
        var rec = Sample();
        await _store.AddAsync(rec);

        var loaded = await _store.GetAsync(rec.Id);
        Assert.NotNull(loaded);
        Assert.Equal("combine A and B via bridge C", loaded!.Hypothesis);
        Assert.Equal("implement the prosthetic controller", loaded.Intent);   // retained for future ideation node
        Assert.Equal(new[] { "f1", "f2" }, loaded.CitedFactIds);
        Assert.Equal(Provenance.Innovated, loaded.Provenance);
    }

    [Fact]
    public async Task Add_EnforcesConfidenceCap_OnWrite()
    {
        // Try to store an over-confident hypothesis — the store must clamp it.
        await _store.AddAsync(Sample(conf: 0.99));
        var loaded = (await _store.GetByProvenanceAsync(Provenance.Innovated))[0];
        Assert.True(loaded.Confidence.Score <= ProvenancePolicy.InnovatedCap);
        Assert.True(loaded.Confidence.IsLow);
    }

    [Fact]
    public async Task Add_LogsInitialRevision()
    {
        var rec = Sample();
        await _store.AddAsync(rec);
        var revs = await _store.GetRevisionsAsync(rec.Id);
        Assert.Single(revs);
        Assert.Equal(0, revs[0].Seq);
        Assert.Equal("created", revs[0].Change);
    }

    [Fact]
    public async Task Update_AppendsRevision_AndReclamps()
    {
        var rec = Sample();
        await _store.AddAsync(rec);
        // Same-provenance update with over-confidence → must re-clamp; append a structured evidence event.
        await _store.UpdateAsync(rec with { Confidence = Confidence.Of(0.99) },
            new LedgerEvent(LedgerEventKind.SuccessEvidence, "advanced"));

        var loaded = await _store.GetAsync(rec.Id);
        Assert.Equal(Provenance.Innovated, loaded!.Provenance);
        Assert.True(loaded.Confidence.Score <= ProvenancePolicy.InnovatedCap);   // re-clamped

        var revs = await _store.GetRevisionsAsync(rec.Id);
        Assert.Equal(2, revs.Count);
        Assert.Equal("advanced", revs[1].Change);
        Assert.Equal(LedgerEventKind.SuccessEvidence, revs[1].Kind);
    }

    [Fact]
    public async Task RevertToRevision_RestoresEarlierState()
    {
        var rec = Sample(conf: 0.2);
        await _store.AddAsync(rec);                                                    // rev 0: Innovated, 0.2
        await _store.UpdateAsync(rec with { Provenance = Provenance.Retracted, Confidence = Confidence.Unassessed },
            new LedgerEvent(LedgerEventKind.FailureEvidence, "retracted")); // rev 1 (downward, allowed)

        var reverted = await _store.RevertToRevisionAsync(rec.Id, 0);
        Assert.True(reverted);

        var loaded = await _store.GetAsync(rec.Id);
        Assert.Equal(Provenance.Innovated, loaded!.Provenance);     // restored
        Assert.Equal(0.2, loaded.Confidence.Score, 5);

        var revs = await _store.GetRevisionsAsync(rec.Id);
        Assert.Equal(3, revs.Count);                                // create, retract, revert
        Assert.Contains("reverted to rev 0", revs[2].Change);
    }

    [Fact]
    public async Task GetByProvenance_Filters()
    {
        await _store.AddAsync(Sample(corr: "a") with { Provenance = Provenance.Innovated });
        await _store.AddAsync(Sample(corr: "b") with { Provenance = Provenance.ProvisionallyValidated });

        Assert.Single(await _store.GetByProvenanceAsync(Provenance.Innovated));
        Assert.Single(await _store.GetByProvenanceAsync(Provenance.ProvisionallyValidated));
    }

    // ── GOVERNING INVARIANT (§0a): only a human-authored event may raise trust ──

    [Fact]
    public async Task RaisingTrust_WithAutomaticEvent_IsRejected()
    {
        var rec = Sample();                       // Innovated (rank 2)
        await _store.AddAsync(rec);

        // An automatic evidence event may NOT push it up to UnderTest (rank 3).
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            _store.UpdateAsync(rec with { Provenance = Provenance.UnderTest },
                new LedgerEvent(LedgerEventKind.SuccessEvidence, "sneaky auto-promote")));

        // ... and the entry is untouched.
        Assert.Equal(Provenance.Innovated, (await _store.GetAsync(rec.Id))!.Provenance);
    }

    [Fact]
    public async Task RaisingTrust_WithHumanEvent_IsAllowed()
    {
        var rec = Sample();
        await _store.AddAsync(rec);

        await _store.UpdateAsync(rec with { Provenance = Provenance.UnderTest },
            new LedgerEvent(LedgerEventKind.HumanAuthorizeCampaign, "campaign authorized"));

        Assert.Equal(Provenance.UnderTest, (await _store.GetAsync(rec.Id))!.Provenance);
    }

    [Fact]
    public async Task LoweringTrust_WithAutomaticEvent_IsAllowed()
    {
        var rec = Sample();
        await _store.AddAsync(rec);

        // Innovated → Retracted (downward) is fine with an automatic kind.
        await _store.UpdateAsync(rec with { Provenance = Provenance.Retracted, Confidence = Confidence.Unassessed },
            new LedgerEvent(LedgerEventKind.FailureEvidence, "failed"));

        Assert.Equal(Provenance.Retracted, (await _store.GetAsync(rec.Id))!.Provenance);
    }
}
