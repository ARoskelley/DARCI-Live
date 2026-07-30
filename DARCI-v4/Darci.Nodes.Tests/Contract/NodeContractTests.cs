using System.Text.Json;
using Darci.Nodes;

namespace Darci.Nodes.Tests.Contract;

/// <summary>SU1 — the contract types. Nothing consumes them yet; these pin the shape and the invariants
/// the later sub-units depend on.</summary>
public class NodeContractTests
{
    // ── capability naming (doc §5.1) ──

    [Theory]
    [InlineData("coding.write", true)]
    [InlineData("knowledge.gapfill", true)]
    [InlineData("a.b.c", true)]
    [InlineData("summarize", false)]           // not namespaced
    [InlineData("Coding.Write", false)]        // not lowercase
    [InlineData("coding.", false)]             // empty segment
    [InlineData(".write", false)]
    [InlineData("coding.9write", false)]       // segment must start with a letter
    [InlineData("coding.write-fast", false)]   // hyphen not allowed
    [InlineData("", false)]
    [InlineData(null, false)]
    public void CapabilityName_Validation(string? name, bool valid)
        => Assert.Equal(valid, CapabilityKey.IsValidName(name));

    [Fact]
    public void AllBuiltInCapabilityNames_AreValid()
        => Assert.All(Capabilities.BuiltIn, n => Assert.True(CapabilityKey.IsValidName(n), n));

    // ── legacy bridge totality (so SU2–SU5 can straddle both worlds safely) ──

    [Fact]
    public void EveryLegacyCapability_MapsToACanonicalString_AndBack()
    {
        foreach (Capability c in Enum.GetValues<Capability>())
        {
            var name = CapabilityKey.From(c);
            Assert.True(CapabilityKey.IsValidName(name), $"{c} → '{name}' is not a valid name");
            Assert.Equal(c, CapabilityKey.ToLegacy(name));
            Assert.Contains(name, Capabilities.BuiltIn);
        }
    }

    [Fact]
    public void EveryLegacyNodeId_MapsToACanonicalString_AndBack()
    {
        foreach (NodeId n in Enum.GetValues<NodeId>())
        {
            var key = CapabilityKey.From(n);
            Assert.Equal(n, CapabilityKey.ToLegacyNode(key));
        }
    }

    [Fact]
    public void ExternalCapability_HasNoLegacyEquivalent_ButIsStillValid()
    {
        // The C1 payoff: a collaborator's capability the core has never heard of is expressible.
        Assert.True(CapabilityKey.IsValidName("acme.simulate_thermal"));
        Assert.Null(CapabilityKey.ToLegacy("acme.simulate_thermal"));
    }

    // ── error taxonomy (doc §5.4) ──

    [Theory]
    [InlineData(NodeErrorCode.InvalidInput, false, "INVALID_INPUT")]
    [InlineData(NodeErrorCode.PermissionDenied, false, "PERMISSION_DENIED")]
    [InlineData(NodeErrorCode.NotImplemented, false, "NOT_IMPLEMENTED")]
    [InlineData(NodeErrorCode.ModelUnavailable, true, "MODEL_UNAVAILABLE")]
    [InlineData(NodeErrorCode.DependencyUnavailable, true, "DEPENDENCY_UNAVAILABLE")]
    [InlineData(NodeErrorCode.DeadlineExceeded, true, "DEADLINE_EXCEEDED")]
    [InlineData(NodeErrorCode.Internal, true, "INTERNAL")]
    public void ErrorCodes_HaveDocumentedRetryStanceAndWireSpelling(NodeErrorCode code, bool retryable, string wire)
    {
        var err = NodeError.Of(code, "boom");
        Assert.Equal(retryable, err.Retryable);
        Assert.Equal(wire, err.WireCode);
    }

    // ── ADD-3: the `blocked` outcome ──

    [Fact]
    public void BlockedOutcome_CarriesAStructuredDependency_AndIsNotAnError()
    {
        var r = NodeResult.BlockedOn("t1", new NodeDependency(
            DependencyKind.HumanDecision, "awaiting campaign authorization", "proposal_123"));

        Assert.Equal(NodeOutcome.Blocked, r.Outcome);
        Assert.Null(r.Error);                       // blocked is NOT an error — nothing to retry
        Assert.Equal(DependencyKind.HumanDecision, r.Dependency!.Kind);
        Assert.Equal("proposal_123", r.Dependency.ReferenceId);
    }

    [Fact]
    public void ThreeOutcomes_Exist_SoBoundedWorkCanReportAGoalLevelWait()
        => Assert.Equal(3, Enum.GetValues<NodeOutcome>().Length);

    [Fact]
    public void DependencyKinds_CoverTheThreeRealWaits()
    {
        Assert.Equal(
            new[] { DependencyKind.HumanDecision, DependencyKind.MissingEnvironment, DependencyKind.PendingOutcome },
            Enum.GetValues<DependencyKind>());
    }

    // ── ADD-2: correlation identity is structural, not incidental ──

    [Fact]
    public void GoalId_AndTraceId_AreDistinctFields_TraceIdDefaultsFresh()
    {
        var a = new NodeInvocation { GoalId = "root-1", Capability = Capabilities.CodingWrite };
        var b = new NodeInvocation { GoalId = "root-1", Capability = Capabilities.CodingWrite };

        Assert.Equal(a.GoalId, b.GoalId);            // same correlation root…
        Assert.NotEqual(a.TraceId, b.TraceId);       // …different per-invocation trace ids
        Assert.NotEqual(a.GoalId, a.TraceId);        // and a trace id is never the correlation key
    }

    // ── ADD-3: payload must survive a process hop ──

    [Fact]
    public void Invocation_RoundTripsThroughJson_AndDropsTheTransitionalPacketRef()
    {
        var packet = NodePacket.Create("do the thing", capability: Capability.WriteCode);
        var inv = new NodeInvocation
        {
            GoalId = packet.CorrelationId,
            Capability = Capabilities.CodingWrite,
            Intent = "do the thing",
            SuccessCriteria = "tests pass",
            Payload = new Dictionary<string, string> { [PacketSlots.Question] = "why?", ["k"] = "v" },
            PacketRef = packet,   // in-process side-channel
        };

        var json = JsonSerializer.Serialize(inv);
        var back = JsonSerializer.Deserialize<NodeInvocation>(json)!;

        Assert.Equal(inv.GoalId, back.GoalId);
        Assert.Equal(inv.TraceId, back.TraceId);
        Assert.Equal(inv.Capability, back.Capability);
        Assert.Equal(inv.Intent, back.Intent);
        Assert.Equal(inv.SuccessCriteria, back.SuccessCriteria);
        Assert.Equal(inv.Payload, back.Payload);                 // payload survives intact
        Assert.DoesNotContain("PacketRef", json, StringComparison.OrdinalIgnoreCase);
        Assert.Null(back.PacketRef);   // CANNOT cross a process boundary — by construction
    }

    [Fact]
    public void Result_RoundTripsThroughJson()
    {
        var r = NodeResult.Ok("t1", new Dictionary<string, string> { ["out"] = "1" }, Confidence.Of(0.8, "sure"));
        var back = JsonSerializer.Deserialize<NodeResult>(JsonSerializer.Serialize(r))!;

        Assert.Equal("t1", back.TraceId);
        Assert.Equal(NodeOutcome.Ok, back.Outcome);
        Assert.Equal(0.8, back.Confidence.Score, 4);
        Assert.Equal("sure", back.Confidence.Note);
        Assert.Equal(r.Payload, back.Payload);
    }

    [Fact]
    public void OmittedConfidence_IsUnassessed_NotZero()
    {
        // doc §5.3: "Omit rather than fabricate" — and Unassessed is itself a gap, not high confidence.
        var r = NodeResult.Ok("t1");
        Assert.False(r.Confidence.IsAssessed);
        Assert.True(r.Confidence.IsGap);
    }

    // ── deadline / taint / broker plumbing ──

    [Fact]
    public void Invocation_Deadline_IsPerInvocation()
    {
        var now = DateTime.UtcNow;
        var inv = new NodeInvocation { DeadlineAt = now.AddSeconds(30) };
        Assert.False(inv.IsExpired(now));
        Assert.True(inv.IsExpired(now.AddSeconds(31)));
    }

    [Fact]
    public void Taint_IsMonotonic_AndDefaultsCleanAndPermissive()
    {
        Assert.Equal(TaintLevel.Clean, TaintRef.Clean.Level);
        Assert.Empty(TaintRef.Clean.Sources);

        var untrusted = new TaintRef(TaintLevel.Untrusted, new[] { "web" });
        Assert.Equal(TaintLevel.Untrusted, TaintRef.Clean.RaisedTo(untrusted).Level);
        // never lowers
        Assert.Equal(TaintLevel.Untrusted, untrusted.RaisedTo(TaintRef.Clean).Level);
    }

    [Fact]
    public void Broker_IsReservedNoOp_InPhase1()
    {
        Assert.Null(BrokerRef.None.Url);
        Assert.Null(BrokerRef.None.Token);
        Assert.Equal(BrokerRef.None, new NodeInvocation().Broker);
    }

    [Fact]
    public void ContractVersion_SupportsRev0_1_And_0_1_1()
    {
        Assert.True(NodeContractVersion.IsSupported("0.1"));
        Assert.True(NodeContractVersion.IsSupported("0.1.1"));
        Assert.False(NodeContractVersion.IsSupported("0.2"));
        Assert.False(NodeContractVersion.IsSupported(null));
        Assert.Equal("0.1.1", NodeContractVersion.Current);
    }
}
