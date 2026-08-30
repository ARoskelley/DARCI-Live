#nullable enable

using System.Diagnostics;
using Darci.Memory.Graph;
using Darci.Shared;

namespace Darci.Research.Agents.Tests.Contract;

/// <summary>
/// The two robustness rules a standalone/packaged core depends on.
///
/// <para><b>1. Configured is not reachable.</b> Credentials in .env.local say a host WANTS Neo4j, not that
/// Neo4j is running. Selecting the backing store on configuration alone made a host with creds and a
/// stopped Neo4j die on boot with an unhandled ServiceUnavailableException. The probe has to answer
/// quickly and never throw, so the composition root can fall back to SQLite instead of crashing.</para>
///
/// <para><b>2. A malformed message is a 400, not a 500.</b></para>
/// </summary>
public sealed class Neo4jFallbackTests
{
    // A port nothing listens on: the "configured but Neo4j is down" case, which is the whole point.
    private static Neo4jOptions Unreachable => new()
    {
        Uri = "bolt://localhost:7699",
        User = "neo4j",
        Password = "irrelevant-nothing-is-listening",
        Database = "neo4j",
    };

    [Fact]
    public async Task Probe_AgainstAStoppedNeo4j_ReportsUnreachableInsteadOfThrowing()
    {
        var (reachable, reason) = await Neo4jKnowledgeGraph.ProbeAsync(Unreachable, TimeSpan.FromSeconds(5));

        Assert.False(reachable);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task Probe_AgainstAStoppedNeo4j_ReturnsWithinItsTimeoutRatherThanTheDriverRetryWindow()
    {
        // The driver's own retry policy takes ~30s to give up, which is long enough to look like a hang and
        // is what made this fatal at startup. The bound is the fix; without it the fallback is unusable.
        var stopwatch = Stopwatch.StartNew();
        var (reachable, _) = await Neo4jKnowledgeGraph.ProbeAsync(Unreachable, TimeSpan.FromSeconds(3));
        stopwatch.Stop();

        Assert.False(reachable);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(15),
            $"probe took {stopwatch.Elapsed.TotalSeconds:0.#}s — it must bound the driver's retry window, not inherit it.");
    }

    [Fact]
    public async Task Probe_WithAGarbageUri_ReportsUnreachableInsteadOfThrowing()
    {
        var options = Unreachable with { Uri = "bolt://nonexistent-host-darci-test:7687" };

        var (reachable, reason) = await Neo4jKnowledgeGraph.ProbeAsync(options, TimeSpan.FromSeconds(5));

        Assert.False(reachable);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }

    [Fact]
    public async Task Probe_AgainstALiveNeo4j_ReportsReachable()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        var (reachable, reason) = await Neo4jKnowledgeGraph.ProbeAsync(
            Neo4jAvailability.Options, TimeSpan.FromSeconds(10));

        Assert.True(reachable, reason);
        Assert.Equal("connected", reason);
    }

    [Fact]
    public async Task Probe_WithBadCredentials_ReportsUnreachable()
    {
        if (!Neo4jAvailability.IsAvailable) return;

        // Auth failure is as fatal to a boot as a stopped server, so it must land on the same fallback path.
        var options = Neo4jAvailability.Options with { Password = "definitely-not-the-password" };

        var (reachable, _) = await Neo4jKnowledgeGraph.ProbeAsync(options, TimeSpan.FromSeconds(10));

        Assert.False(reachable);
    }

    // ── the /message payload rule ──

    [Theory]
    [InlineData(null)]      // field omitted entirely: {"userId":"Tinman"}
    [InlineData("")]        // present but empty
    [InlineData("   ")]     // whitespace only
    [InlineData("\t\n")]
    public void MessageContent_ThatCannotBeUsed_IsRejected(string? content)
    {
        Assert.False(IncomingMessageRules.IsValidContent(content));
    }

    [Theory]
    [InlineData("hello darci")]
    [InlineData("  padded but real  ")]
    [InlineData("0")]
    public void MessageContent_ThatIsUsable_IsAccepted(string content)
    {
        Assert.True(IncomingMessageRules.IsValidContent(content));
    }

    [Fact]
    public void MessageContent_RejectionCarriesAnActionableError()
    {
        // The caller has to be able to tell WHAT was wrong with the payload from the response alone.
        Assert.Contains("message", IncomingMessageRules.MissingContentError, StringComparison.OrdinalIgnoreCase);
        Assert.False(string.IsNullOrWhiteSpace(IncomingMessageRules.MissingContentError));
    }
}
