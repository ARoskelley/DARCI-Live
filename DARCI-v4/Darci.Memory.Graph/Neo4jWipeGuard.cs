#nullable enable

namespace Darci.Memory.Graph;

/// <summary>Why a wipe was allowed or refused. The reason is the whole value of the type — a refusal that
/// does not say what to change just looks like a broken test run.</summary>
public sealed record Neo4jWipeDecision(bool Allowed, string Reason);

/// <summary>Thrown when something tries to empty a Neo4j instance the guard will not let it empty.</summary>
public sealed class Neo4jWipeRefusedException : Exception
{
    public Neo4jWipeRefusedException(string message) : base(message) { }
}

/// <summary>
/// Refuses to let an automated wipe target the Neo4j instance this host actually uses.
///
/// <para>This exists because the opt-in flag alone was not enough. The parity suite empties the graph
/// between tests and was gated on <c>DARCI_NEO4J_TEST_WIPE=1</c>; that gate is a DISCIPLINE, and the
/// failure mode of a discipline is that one day the flag is exported in the shell that then runs the full
/// suite. That happened, and it cost a graph. Neo4j Community allows one database per instance, so
/// isolating tests by database name is not available — the only real isolation is a different INSTANCE.</para>
///
/// <para>So the rule is now structural, and deliberately redundant with the flag:</para>
/// <list type="number">
///   <item><description><c>DARCI_NEO4J_TEST_WIPE</c> must be <c>1</c> — the existing opt-in, kept.</description></item>
///   <item><description><c>DARCI_NEO4J_TEST_URI</c> must name the instance to wipe. An absent value is not
///     interpreted as "use the normal one"; it means no instance has been declared disposable, so nothing
///     may be wiped.</description></item>
///   <item><description>That instance must not BE the configured one. Compared by host and port, so
///     <c>neo4j://127.0.0.1:7687</c> and <c>bolt://localhost:7687</c> are correctly recognised as the same
///     server rather than two different ones.</description></item>
/// </list>
///
/// <para>The comparison ignores the scheme and the credentials on purpose: those vary between clients
/// talking to one server, and anything that makes two URIs look DIFFERENT makes a wipe more likely to be
/// allowed. When a URI cannot be parsed at all the guard treats it as the same instance, for the same
/// reason — the conservative answer to "is this the production graph?" is yes.</para>
///
/// <para>Known gap, stated rather than hidden: two different hostnames that resolve to the same server
/// (<c>localhost</c> and the machine's own name, say) are not recognised as equal, because resolving DNS
/// inside a guard would make it slow and occasionally wrong. Give the disposable instance its own PORT and
/// the gap cannot be reached.</para>
/// </summary>
public static class Neo4jWipeGuard
{
    public const string WipeFlagEnv = "DARCI_NEO4J_TEST_WIPE";
    public const string TestUriEnv = "DARCI_NEO4J_TEST_URI";
    public const string ProductionUriEnv = "DARCI_NEO4J_URI";

    /// <summary>What <see cref="Neo4jOptions.FromEnvironment"/> falls back to. An unset
    /// <c>DARCI_NEO4J_URI</c> does not mean "no production instance" — it means the default one, and that
    /// default needs protecting just as much as an explicit value.</summary>
    public const string DefaultProductionUri = "bolt://localhost:7687";

    /// <summary>Pure form: no environment, no I/O, so the rule itself can be tested exhaustively.</summary>
    public static Neo4jWipeDecision Evaluate(
        string? wipeFlag,
        string? designatedTestUri,
        string? configuredProductionUri)
    {
        var production = string.IsNullOrWhiteSpace(configuredProductionUri)
            ? DefaultProductionUri
            : configuredProductionUri.Trim();

        if (wipeFlag?.Trim() != "1")
        {
            return new Neo4jWipeDecision(false,
                $"{WipeFlagEnv} is not 1 — refusing to wipe a graph that was not declared disposable.");
        }

        if (string.IsNullOrWhiteSpace(designatedTestUri))
        {
            return new Neo4jWipeDecision(false,
                $"{TestUriEnv} is not set — a wipe needs an instance explicitly designated as disposable, and "
                + $"no instance has been. Stand up a second Neo4j (its own port) and set {TestUriEnv} to it; "
                + $"the configured instance ({production}) is never a valid target.");
        }

        var test = designatedTestUri.Trim();

        if (SameInstance(test, production))
        {
            return new Neo4jWipeDecision(false,
                $"REFUSED: {TestUriEnv} ({test}) is the same Neo4j instance as {ProductionUriEnv} ({production}), "
                + $"so wiping it would empty the graph this host runs on. {WipeFlagEnv} does not override this. "
                + $"Point {TestUriEnv} at a separate instance on its own port.");
        }

        return new Neo4jWipeDecision(true, $"{test} is declared disposable and is not the configured instance ({production}).");
    }

    /// <summary>Reads the three variables the rule depends on and applies it.</summary>
    public static Neo4jWipeDecision EvaluateFromEnvironment() => Evaluate(
        Environment.GetEnvironmentVariable(WipeFlagEnv),
        Environment.GetEnvironmentVariable(TestUriEnv),
        Environment.GetEnvironmentVariable(ProductionUriEnv));

    /// <summary>
    /// The check at the point of deletion. Call it immediately before emitting anything that empties a
    /// graph, passing the URI the delete will actually run against — which is not necessarily the one in
    /// the environment, and that difference is exactly what this catches.
    /// </summary>
    public static void EnsureWipeAllowed(string? targetUri)
    {
        var decision = EvaluateFromEnvironment();
        if (!decision.Allowed) throw new Neo4jWipeRefusedException(decision.Reason);

        var production = Environment.GetEnvironmentVariable(ProductionUriEnv);
        if (SameInstance(targetUri, string.IsNullOrWhiteSpace(production) ? DefaultProductionUri : production))
        {
            throw new Neo4jWipeRefusedException(
                $"REFUSED: the wipe target ({targetUri ?? "(null)"}) is the configured Neo4j instance. The "
                + $"environment designated a different instance as disposable, so this call is pointed "
                + $"somewhere it must not be.");
        }

        var designated = Environment.GetEnvironmentVariable(TestUriEnv);
        if (!SameInstance(targetUri, designated))
        {
            throw new Neo4jWipeRefusedException(
                $"REFUSED: the wipe target ({targetUri ?? "(null)"}) is not the instance designated by "
                + $"{TestUriEnv} ({designated}). Only the designated instance may be emptied.");
        }
    }

    /// <summary>
    /// Whether two bolt URIs name the same Neo4j server, by host and port. Unparseable or empty input
    /// answers TRUE: an unknown target is treated as the production one, because that is the answer that
    /// refuses.
    /// </summary>
    public static bool SameInstance(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return true;

        var left = Endpoint(a);
        var right = Endpoint(b);
        if (left is null || right is null) return true;

        return string.Equals(left.Value.Host, right.Value.Host, StringComparison.OrdinalIgnoreCase)
            && left.Value.Port == right.Value.Port;
    }

    private const int DefaultBoltPort = 7687;

    private static (string Host, int Port)? Endpoint(string uri)
    {
        var text = uri.Trim();

        // Accept a bare host:port as well as a full URI — a value typed into an env file often lacks a scheme,
        // and failing to parse it would make the guard MORE permissive.
        if (!text.Contains("://", StringComparison.Ordinal)) text = "bolt://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var parsed)) return null;
        if (string.IsNullOrWhiteSpace(parsed.Host)) return null;

        var port = parsed.IsDefaultPort || parsed.Port <= 0 ? DefaultBoltPort : parsed.Port;
        return (NormalizeHost(parsed.Host), port);
    }

    private static string NormalizeHost(string host) =>
        host.Trim().Trim('[', ']').ToLowerInvariant() switch
        {
            "127.0.0.1" or "::1" or "0:0:0:0:0:0:0:1" or "localhost" => "localhost",
            var h => h,
        };
}
