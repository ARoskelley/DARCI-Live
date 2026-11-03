using Darci.Nodes;

namespace Darci.Nodes.Tests;

/// <summary>The campaign verdict is a PURE FUNCTION over (pre-registered criteria × step evidence) — the
/// anti-validation-theater guard. Same inputs → same verdict; the outcome flag alone is never enough.</summary>
public class CampaignProtocolTests
{
    private static ValidationStep Step(string id, string metric, Comparator cmp, double threshold) =>
        new(id, ValidationStepKind.SandboxTest, Capability.RunTests, NodeId.Coding,
            new SuccessCriteria(metric, cmp, threshold));

    private static StepEvidence Ev(string id, ValidationStepOutcome outcome, params (string, double)[] measures) =>
        new(id, outcome, measures.ToDictionary(m => m.Item1, m => m.Item2));

    [Fact]
    public void AllStepsPass_AndCriteriaMet_Passes()
    {
        var protocol = new[] { Step("s1", "pass_rate", Comparator.GreaterOrEqual, 0.9), Step("s2", "latency_ms", Comparator.LessOrEqual, 50) };
        var evidence = new[] { Ev("s1", ValidationStepOutcome.Passed, ("pass_rate", 0.95)), Ev("s2", ValidationStepOutcome.Passed, ("latency_ms", 30)) };

        Assert.Equal(CampaignVerdict.Passed, CampaignProtocol.Evaluate(protocol, evidence));
    }

    [Fact]
    public void PassedOutcome_ButCriteriaNotMet_Fails()
    {
        // The load-bearing case: a step "passed" but the pre-registered metric bar was NOT met → Failed,
        // not Passed. This blocks post-hoc "it sort of worked".
        var protocol = new[] { Step("s1", "pass_rate", Comparator.GreaterOrEqual, 0.9) };
        var evidence = new[] { Ev("s1", ValidationStepOutcome.Passed, ("pass_rate", 0.5)) };

        Assert.Equal(CampaignVerdict.Failed, CampaignProtocol.Evaluate(protocol, evidence));
    }

    [Fact]
    public void PassedOutcome_ButMetricAbsent_IsInconclusive_NotFailed()
    {
        // The node "passed" but produced no measurement for the pre-registered metric — the protocol
        // couldn't test this way. That must NOT read as a failure (which would demote a good hypothesis).
        var protocol = new[] { Step("s1", "pass_rate", Comparator.GreaterOrEqual, 0.9) };
        var evidence = new[] { Ev("s1", ValidationStepOutcome.Passed, ("unrelated_metric", 1.0)) };

        Assert.Equal(CampaignVerdict.Inconclusive, CampaignProtocol.Evaluate(protocol, evidence));
    }

    [Fact]
    public void MeasuredBelowBar_Fails_ButAbsentMetric_DoesNot()
    {
        var protocol = new[] { Step("s1", "pass_rate", Comparator.GreaterOrEqual, 0.9) };
        // present-but-below → Failed; absent → Inconclusive.
        Assert.Equal(CampaignVerdict.Failed, CampaignProtocol.Evaluate(protocol, new[] { Ev("s1", ValidationStepOutcome.Passed, ("pass_rate", 0.5)) }));
        Assert.Equal(CampaignVerdict.Inconclusive, CampaignProtocol.Evaluate(protocol, new[] { Ev("s1", ValidationStepOutcome.Passed) }));
    }

    [Fact]
    public void AnyStepFailed_Fails()
    {
        var protocol = new[] { Step("s1", "m", Comparator.GreaterOrEqual, 1), Step("s2", "m", Comparator.GreaterOrEqual, 1) };
        var evidence = new[] { Ev("s1", ValidationStepOutcome.Passed, ("m", 2)), Ev("s2", ValidationStepOutcome.Failed, ("m", 2)) };

        Assert.Equal(CampaignVerdict.Failed, CampaignProtocol.Evaluate(protocol, evidence));
    }

    [Fact]
    public void BlockedStep_IsInconclusive()
    {
        // A step whose environment doesn't exist cannot conclude the campaign either way.
        var protocol = new[] { Step("s1", "m", Comparator.GreaterOrEqual, 1), Step("s2", "m", Comparator.GreaterOrEqual, 1) };
        var evidence = new[] { Ev("s1", ValidationStepOutcome.Passed, ("m", 2)), Ev("s2", ValidationStepOutcome.Blocked) };

        Assert.Equal(CampaignVerdict.Inconclusive, CampaignProtocol.Evaluate(protocol, evidence));
    }

    [Fact]
    public void MissingOrPendingEvidence_IsPending()
    {
        var protocol = new[] { Step("s1", "m", Comparator.GreaterOrEqual, 1), Step("s2", "m", Comparator.GreaterOrEqual, 1) };
        var onlyOne = new[] { Ev("s1", ValidationStepOutcome.Passed, ("m", 2)) };

        Assert.Equal(CampaignVerdict.Pending, CampaignProtocol.Evaluate(protocol, onlyOne));
        Assert.Equal(CampaignVerdict.Pending, CampaignProtocol.Evaluate(protocol, System.Array.Empty<StepEvidence>()));
    }

    [Fact]
    public void FailBeatsBlock_And_BlockBeatsPending()
    {
        var protocol = new[] { Step("f", "m", Comparator.GreaterOrEqual, 1), Step("b", "m", Comparator.GreaterOrEqual, 1), Step("p", "m", Comparator.GreaterOrEqual, 1) };
        // A hard failure dominates a block or a pending.
        var withFail = new[] { Ev("f", ValidationStepOutcome.Failed), Ev("b", ValidationStepOutcome.Blocked) };
        Assert.Equal(CampaignVerdict.Failed, CampaignProtocol.Evaluate(protocol, withFail));
    }

    [Fact]
    public void Deterministic_SameInputsSameVerdict()
    {
        var protocol = new[] { Step("s1", "m", Comparator.Equal, 1.0) };
        var evidence = new[] { Ev("s1", ValidationStepOutcome.Passed, ("m", 1.0)) };

        var a = CampaignProtocol.Evaluate(protocol, evidence);
        var b = CampaignProtocol.Evaluate(protocol, evidence);
        Assert.Equal(a, b);
        Assert.Equal(CampaignVerdict.Passed, a);
    }

    [Fact]
    public void EmptyProtocol_IsPending()
        => Assert.Equal(CampaignVerdict.Pending, CampaignProtocol.Evaluate(System.Array.Empty<ValidationStep>(), System.Array.Empty<StepEvidence>()));

    [Theory]
    [InlineData(Comparator.GreaterOrEqual, 0.9, 0.9, true)]
    [InlineData(Comparator.GreaterOrEqual, 0.9, 0.89, false)]
    [InlineData(Comparator.LessOrEqual, 50, 50, true)]
    [InlineData(Comparator.LessOrEqual, 50, 51, false)]
    [InlineData(Comparator.Equal, 1.0, 1.0, true)]
    [InlineData(Comparator.Equal, 1.0, 1.001, false)]
    public void SuccessCriteria_IsMetBy_Comparators(Comparator cmp, double threshold, double measured, bool expected)
    {
        var c = new SuccessCriteria("m", cmp, threshold);
        Assert.Equal(expected, c.IsMetBy(new Dictionary<string, double> { ["m"] = measured }));
    }

    [Fact]
    public void SuccessCriteria_MissingMetric_IsNotMet()
        => Assert.False(new SuccessCriteria("m", Comparator.GreaterOrEqual, 1).IsMetBy(new Dictionary<string, double>()));

    [Fact]
    public void SuccessCriteria_HasMetric_DetectsPresenceVsAbsence()
    {
        var c = new SuccessCriteria("m", Comparator.GreaterOrEqual, 1);
        Assert.True(c.HasMetric(new Dictionary<string, double> { ["m"] = 0.5 }));   // present (even if below bar)
        Assert.False(c.HasMetric(new Dictionary<string, double> { ["other"] = 9 }));
        Assert.False(c.HasMetric(new Dictionary<string, double>()));
    }
}
