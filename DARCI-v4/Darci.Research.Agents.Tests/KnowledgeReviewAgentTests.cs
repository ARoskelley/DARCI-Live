#nullable enable

using Darci.Research.Agents;
using Darci.Research.Agents.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Research.Agents.Tests;

public class KnowledgeReviewAgentTests
{
    private static OllamaKnowledgeReviewAgent Agent(string generation, bool throwOnGenerate = false) =>
        new(new FakeToolbox(generation, throwOnGenerate), NullLogger<OllamaKnowledgeReviewAgent>.Instance);

    private static KnowledgeRequest Req() => new("What is the Damm table?");

    [Fact]
    public async Task ParsesFulfillsTrue()
    {
        var resp = await Agent("""{"fulfills":true,"confidence":0.8,"missing":[],"reasoning":"complete"}""")
            .ReviewAsync(Req(), "a full answer", "knowledge-graph");

        Assert.True(resp.Fulfills);
        Assert.Equal(0.8, resp.Confidence.Score, 5);
        Assert.Empty(resp.MissingAspects);
    }

    [Fact]
    public async Task ParsesFulfillsFalseWithMissing()
    {
        var resp = await Agent("""{"fulfills":false,"confidence":0.2,"missing":["the actual table"],"reasoning":"vague"}""")
            .ReviewAsync(Req(), "vague answer", "knowledge-graph");

        Assert.False(resp.Fulfills);
        Assert.Contains("the actual table", resp.MissingAspects);
    }

    [Fact]
    public async Task ToolboxThrows_DefaultsToNotFulfilled()
    {
        var resp = await Agent("", throwOnGenerate: true).ReviewAsync(Req(), "answer", "knowledge-graph");
        Assert.False(resp.Fulfills);                            // fail-safe: escalate rather than wrongly accept
        Assert.False(resp.Confidence.IsAssessed);
    }

    [Fact]
    public async Task EmptyCandidate_NotFulfilled_WithoutCallingModel()
    {
        // throwOnGenerate would blow up if the model were called; it must NOT be for an empty candidate.
        var resp = await Agent("", throwOnGenerate: true).ReviewAsync(Req(), "   ", "knowledge-graph");
        Assert.False(resp.Fulfills);
    }

    [Fact]
    public async Task UnparseableResponse_NotFulfilled()
    {
        var resp = await Agent("yeah looks fine to me").ReviewAsync(Req(), "answer", "knowledge-graph");
        Assert.False(resp.Fulfills);
    }
}
