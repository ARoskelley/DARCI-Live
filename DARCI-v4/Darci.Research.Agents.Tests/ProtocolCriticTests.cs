#nullable enable

using Darci.Nodes;
using Darci.Research.Agents;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

/// <summary>The protocol critic falsifies the DESIGN before authorization (anti-validation-theater).</summary>
public class ProtocolCriticTests
{
    private static ValidationCampaign Campaign() => new()
    {
        HypothesisSnapshot = "combine EMG threshold detection with a PID grip loop",
        TargetStage = Provenance.ProvisionallyValidated,
        Protocol = new[]
        {
            new ValidationStep("s1", ValidationStepKind.SandboxTest, Capability.RunTests, NodeId.Coding,
                new SuccessCriteria("pass_rate", Comparator.GreaterOrEqual, 0.9)),
        },
    };

    [Fact]
    public void Parse_ExtractsFailureModesAndAdequacy()
    {
        var raw = """{"unexercisedFailureModes": ["no sensor-noise case", "no cold-start"], "adequate": false, "summary": "misses noise"}""";
        var c = OllamaProtocolCritic.Parse(raw);
        Assert.NotNull(c);
        Assert.Equal(2, c!.UnexercisedFailureModes.Count);
        Assert.False(c.Adequate);
        Assert.Equal("misses noise", c.Summary);
    }

    [Fact]
    public async Task Unavailable_FailsClosed_NotAdequate()
    {
        // If the critic can't run, it must NOT imply the protocol is sound.
        var critic = new OllamaProtocolCritic(new FakeToolbox(throwOnGenerate: true), NullLogger<OllamaProtocolCritic>.Instance);
        var critique = await critic.FalsifyAsync(Campaign());
        Assert.False(critique.Adequate);
        Assert.NotEmpty(critique.UnexercisedFailureModes);
    }

    [Fact]
    public async Task Adequate_ProtocolParsesThroughToolbox()
    {
        var raw = """{"unexercisedFailureModes": [], "adequate": true, "summary": "covers the main modes"}""";
        var critic = new OllamaProtocolCritic(new FakeToolbox(generation: raw), NullLogger<OllamaProtocolCritic>.Instance);
        var critique = await critic.FalsifyAsync(Campaign());
        Assert.True(critique.Adequate);
    }
}
