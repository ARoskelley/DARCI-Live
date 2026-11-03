#nullable enable

using Darci.Memory.Graph;

namespace Darci.Research.Agents.Tests.Contract;

/// <summary>
/// The interlock that stops an automated wipe from emptying the Neo4j instance this host runs on.
///
/// <para>Written after the old interlock failed in the way disciplines fail. The parity suite was gated on
/// <c>DARCI_NEO4J_TEST_WIPE=1</c>; the flag was exported in a shell that then ran the full suite, and the
/// graph went with it. So these tests exist to assert the thing that actually protects the data: that no
/// combination of environment can aim a wipe at the configured instance.</para>
///
/// <para>Most of them use the pure overload, so the rule is tested without touching process state at all.
/// The few that must set variables live in the Neo4j collection so they cannot run beside the suite whose
/// behavior those same variables control.</para>
/// </summary>
public sealed class Neo4jWipeGuardTests
{
    private const string Prod = "bolt://localhost:7687";
    private const string SeparateInstance = "bolt://localhost:7688";

    // ── the refusal the whole guard exists for ──

    [Fact]
    public void Refuses_WhenTheDesignatedTestUri_IsTheConfiguredProductionUri()
    {
        var decision = Neo4jWipeGuard.Evaluate(wipeFlag: "1", designatedTestUri: Prod, configuredProductionUri: Prod);

        Assert.False(decision.Allowed);
        // The reason has to name both variables, or the operator cannot tell which one to change.
        Assert.Contains(Neo4jWipeGuard.TestUriEnv, decision.Reason);
        Assert.Contains(Neo4jWipeGuard.ProductionUriEnv, decision.Reason);
    }

    [Fact]
    public void TheWipeFlag_DoesNotOverrideTheRefusal()
    {
        // The flag was the old interlock. It is now necessary and NOT sufficient: this is the exact
        // situation that cost a graph, and it must now refuse.
        var decision = Neo4jWipeGuard.Evaluate("1", Prod, Prod);

        Assert.False(decision.Allowed);
        Assert.Contains(Neo4jWipeGuard.WipeFlagEnv, decision.Reason);
    }

    [Fact]
    public void Refuses_WhenTheProductionUriIsUnset_BecauseUnsetMeansTheDefaultInstance()
    {
        // An absent DARCI_NEO4J_URI does not mean "nothing to protect" — the app falls back to the default,
        // so the default is production.
        var decision = Neo4jWipeGuard.Evaluate("1", Neo4jWipeGuard.DefaultProductionUri, configuredProductionUri: null);

        Assert.False(decision.Allowed);
    }

    [Theory]
    [InlineData("bolt://127.0.0.1:7687")]           // same host, different spelling
    [InlineData("bolt://LOCALHOST:7687")]           // same host, different case
    [InlineData("bolt://[::1]:7687")]               // same host over IPv6
    [InlineData("neo4j://localhost:7687")]          // same server, different scheme
    [InlineData("neo4j+s://localhost:7687")]        // same server, encrypted scheme
    [InlineData("bolt://neo4j:secret@localhost:7687")] // same server, credentials inline
    [InlineData("bolt://localhost")]                // same server, port left implicit
    [InlineData("localhost:7687")]                  // same server, scheme left out
    public void Refuses_WhenTheTestUriIsOnlyASpellingVariant_OfTheProductionUri(string testUri)
    {
        // Every one of these is the same server. Anything the guard fails to recognise as production is a
        // wipe it would permit, so the comparison is on host and port only.
        var decision = Neo4jWipeGuard.Evaluate("1", testUri, Prod);

        Assert.False(decision.Allowed);
        Assert.True(Neo4jWipeGuard.SameInstance(testUri, Prod), $"'{testUri}' should be recognised as {Prod}");
    }

    [Fact]
    public void Refuses_WhenNoTestInstanceIsDesignated()
    {
        // Absent is not "use the normal one" — it is "nothing has been declared disposable".
        var decision = Neo4jWipeGuard.Evaluate("1", designatedTestUri: null, configuredProductionUri: Prod);

        Assert.False(decision.Allowed);
        Assert.Contains(Neo4jWipeGuard.TestUriEnv, decision.Reason);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("true")]
    [InlineData("yes")]
    public void Refuses_WhenTheWipeFlagIsNotExactlyOne(string? flag)
    {
        var decision = Neo4jWipeGuard.Evaluate(flag, SeparateInstance, Prod);

        Assert.False(decision.Allowed);
        Assert.Contains(Neo4jWipeGuard.WipeFlagEnv, decision.Reason);
    }

    [Theory]
    [InlineData("not a uri at all")]
    [InlineData("://")]
    public void Refuses_WhenTheTestUriCannotBeParsed(string testUri)
    {
        // The conservative answer to "is this the production graph?" is yes.
        var decision = Neo4jWipeGuard.Evaluate("1", testUri, Prod);

        Assert.False(decision.Allowed);
    }

    // ── the one allowed shape ──

    [Fact]
    public void Allows_OnlyWithTheFlagSet_AndAGenuinelySeparateInstance()
    {
        var decision = Neo4jWipeGuard.Evaluate("1", SeparateInstance, Prod);

        Assert.True(decision.Allowed, decision.Reason);
        Assert.Contains(SeparateInstance, decision.Reason);
    }

    [Fact]
    public void SameInstance_SeparatesInstancesThatDifferByPortOrHost()
    {
        Assert.False(Neo4jWipeGuard.SameInstance("bolt://localhost:7688", Prod));
        Assert.False(Neo4jWipeGuard.SameInstance("bolt://neo4j-test.internal:7687", Prod));

        // ...and treats an implicit port as bolt's default rather than as "no port".
        Assert.True(Neo4jWipeGuard.SameInstance("bolt://localhost", "bolt://localhost:7687"));
        Assert.False(Neo4jWipeGuard.SameInstance("bolt://localhost", "bolt://localhost:7688"));
    }

    [Fact]
    public void SameInstance_AnswersYesForMissingInput()
    {
        // Unknown target, production answer.
        Assert.True(Neo4jWipeGuard.SameInstance(null, Prod));
        Assert.True(Neo4jWipeGuard.SameInstance("", Prod));
        Assert.True(Neo4jWipeGuard.SameInstance(Prod, null));
    }
}

/// <summary>
/// The deletion-site check, which has to read the environment — so these are serialized with the suite
/// those variables govern.
/// </summary>
[Collection(Neo4jCollection.Name)]
public sealed class Neo4jWipeGuardEnvironmentTests
{
    private const string Prod = "bolt://localhost:7687";
    private const string SeparateInstance = "bolt://localhost:7688";
    private const string ThirdInstance = "bolt://localhost:7689";

    [Fact]
    public void EnsureWipeAllowed_Throws_WhenTheTargetIsTheConfiguredInstance()
    {
        using var _ = new EnvScope(
            (Neo4jWipeGuard.WipeFlagEnv, "1"),
            (Neo4jWipeGuard.TestUriEnv, SeparateInstance),
            (Neo4jWipeGuard.ProductionUriEnv, Prod));

        // The environment permits a wipe — of the separate instance. Pointing the call at production is
        // caught anyway, which is the point of checking at the delete rather than only at the gate.
        var ex = Assert.Throws<Neo4jWipeRefusedException>(() => Neo4jWipeGuard.EnsureWipeAllowed(Prod));
        Assert.Contains("REFUSED", ex.Message);
    }

    [Fact]
    public void EnsureWipeAllowed_Throws_WhenTheTargetIsSomeThirdInstance()
    {
        using var _ = new EnvScope(
            (Neo4jWipeGuard.WipeFlagEnv, "1"),
            (Neo4jWipeGuard.TestUriEnv, SeparateInstance),
            (Neo4jWipeGuard.ProductionUriEnv, Prod));

        // Not production either — but only the DESIGNATED instance may be emptied, so a stray target is
        // still refused.
        var ex = Assert.Throws<Neo4jWipeRefusedException>(() => Neo4jWipeGuard.EnsureWipeAllowed(ThirdInstance));
        Assert.Contains(Neo4jWipeGuard.TestUriEnv, ex.Message);
    }

    [Fact]
    public void EnsureWipeAllowed_Throws_WhenTheFlagIsAbsent_EvenForTheDesignatedInstance()
    {
        using var _ = new EnvScope(
            (Neo4jWipeGuard.WipeFlagEnv, null),
            (Neo4jWipeGuard.TestUriEnv, SeparateInstance),
            (Neo4jWipeGuard.ProductionUriEnv, Prod));

        Assert.Throws<Neo4jWipeRefusedException>(() => Neo4jWipeGuard.EnsureWipeAllowed(SeparateInstance));
    }

    [Fact]
    public void EnsureWipeAllowed_Permits_TheDesignatedSeparateInstance()
    {
        using var _ = new EnvScope(
            (Neo4jWipeGuard.WipeFlagEnv, "1"),
            (Neo4jWipeGuard.TestUriEnv, SeparateInstance),
            (Neo4jWipeGuard.ProductionUriEnv, Prod));

        Neo4jWipeGuard.EnsureWipeAllowed(SeparateInstance);   // does not throw
    }

    [Fact]
    public void TheTestHarness_TargetsTheDesignatedInstance_NeverTheConfiguredOne()
    {
        // Guards the harness itself: whatever else changes, the URI every Neo4j-touching test reads and
        // writes comes from DARCI_NEO4J_TEST_URI, and is never inherited from the app's own setting.
        var designated = Environment.GetEnvironmentVariable(Neo4jWipeGuard.TestUriEnv) ?? "";
        Assert.Equal(designated, Neo4jAvailability.Options.Uri);

        if (!string.IsNullOrWhiteSpace(designated))
        {
            var configured = Environment.GetEnvironmentVariable(Neo4jWipeGuard.ProductionUriEnv);
            Assert.False(Neo4jWipeGuard.SameInstance(Neo4jAvailability.Options.Uri, configured),
                "the test harness is pointed at the configured instance");
        }
    }
}

/// <summary>
/// Sets environment variables for one test and puts them back afterwards.
///
/// <para>Reads <see cref="Neo4jAvailability.IsAvailable"/> first, deliberately: that probe caches its
/// answer on first use, and if it happened to land inside one of these scopes it would cache a verdict
/// derived from the test's temporary values. Forcing it to settle beforehand keeps these tests from
/// silently switching the parity suite off on a host where it should run.</para>
/// </summary>
internal sealed class EnvScope : IDisposable
{
    private readonly (string Key, string? Previous)[] _restore;

    public EnvScope(params (string Key, string? Value)[] values)
    {
        _ = Neo4jAvailability.IsAvailable;

        _restore = values.Select(v => (v.Key, Environment.GetEnvironmentVariable(v.Key))).ToArray();
        foreach (var (key, value) in values) Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, previous) in _restore) Environment.SetEnvironmentVariable(key, previous);
    }
}
