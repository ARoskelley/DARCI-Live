#nullable enable

using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes;

/// <summary>
/// SU 3.3 — the OUT-OF-PROCESS transport. Speaks doc §5.2's three endpoints to a node that is already
/// running somewhere else: <c>GET /health</c>, <c>GET /manifest</c>, <c>POST /invoke</c>. That is the entire
/// surface; the core calls nothing else.
///
/// <para>Per decision D3 the core does NOT launch node processes — it connects to ones that already exist.
/// So every failure here is somebody else's process being absent, slow, or wrong, and none of them may take
/// the core down (§5.5: "the core never dies because a node died"). This class therefore converts every
/// transport failure into a well-formed <see cref="NodeResult"/> rather than an exception.</para>
///
/// <para>The <c>PacketRef</c> side-channel that in-process adapters rely on is <c>[JsonIgnore]</c>d and
/// cannot cross a process boundary. An HTTP node is payload-only by construction, which is exactly the
/// discipline the contract was shaped to enforce.</para>
/// </summary>
public sealed class HttpNodeAdapter : INodeAdapter
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<HttpNodeAdapter> _logger;
    private readonly string? _sharedSecret;

    public HttpNodeAdapter(
        NodeManifest manifest,
        HttpClient http,
        ILogger<HttpNodeAdapter>? logger = null,
        string? sharedSecret = null)
    {
        Manifest = manifest;
        _http = http;
        _logger = logger ?? NullLogger<HttpNodeAdapter>.Instance;
        _sharedSecret = sharedSecret;
    }

    public NodeManifest Manifest { get; }

    /// <summary>The node's base address, e.g. <c>http://localhost:7801</c>.</summary>
    public string Endpoint => Manifest.Endpoint ?? "";

    // ── §5.2 GET /health ──

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var path = string.IsNullOrWhiteSpace(Manifest.Health) ? "/health" : Manifest.Health;
            using var response = await _http.GetAsync(Combine(Endpoint, path), ct);
            if (!response.IsSuccessStatusCode) return false;

            // §5.2: 200 with {"status":"ok"} when able to serve. A 200 carrying anything else is a node
            // saying it is up but NOT ready, which is not the same as healthy.
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return false;

            var health = JsonSerializer.Deserialize<HealthDto>(body, Json);
            return string.Equals(health?.Status, "ok", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Health check failed for node {NodeId} at {Endpoint}.", Manifest.NodeId, Endpoint);
            return false;
        }
    }

    // ── §5.2 GET /manifest ──

    /// <summary>
    /// Fetches the node's self-reported manifest for the §5.5 handshake. Returns null when unreachable or
    /// unparseable — the caller decides, because "cannot verify" and "verified as different" both mean
    /// do-not-register but deserve different log lines.
    /// </summary>
    public async Task<NodeManifest?> FetchManifestAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(Combine(Endpoint, "/manifest"), ct);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadAsStringAsync(ct);

            // Deserialize with the SAME options the on-disk loader uses, not this class's envelope options.
            // Two reasons, one of which cost a live boot: ManifestJson carries the string-enum converter, so
            // without it a perfectly valid `"kind": "capability"` fails to parse and the node is silently
            // skipped as "unparseable". And the handshake compares SHAs — a hash is only comparable if both
            // sides were parsed by identical rules.
            return string.IsNullOrWhiteSpace(body)
                ? null
                : JsonSerializer.Deserialize<NodeManifest>(body, ManifestJson.Options);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogDebug(ex, "Manifest fetch failed for node {NodeId} at {Endpoint}.", Manifest.NodeId, Endpoint);
            return null;
        }
    }

    // ── §5.2 POST /invoke ──

    public async Task<NodeResult> InvokeAsync(NodeInvocation invocation, CancellationToken ct = default)
    {
        // The deadline is BINDING (§5.3): "the core will abandon the call at the deadline regardless".
        // Enforced here rather than trusted to the node, because an unresponsive node is the case this
        // has to survive.
        var remaining = invocation.DeadlineAt - DateTime.UtcNow;
        if (remaining <= TimeSpan.Zero)
        {
            return NodeResult.Failed(invocation.TraceId, NodeErrorCode.DeadlineExceeded,
                "The invocation deadline had already passed before the node was called.");
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(remaining);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Combine(Endpoint, "/invoke"))
            {
                Content = JsonContent.Create(ToWire(invocation), options: Json),
            };

            // D4, minimal for loopback: a per-process shared secret. Full broker-token scoping and taint
            // enforcement stay deferred — this exists so the discipline is in place before anything runs
            // off-box, not because it is real auth.
            if (!string.IsNullOrWhiteSpace(_sharedSecret))
                request.Headers.TryAddWithoutValidation("X-Darci-Token", _sharedSecret);

            using var response = await _http.SendAsync(request, cts.Token);

            if (!response.IsSuccessStatusCode)
            {
                return NodeResult.Failed(invocation.TraceId, NodeErrorCode.DependencyUnavailable,
                    $"Node {Manifest.NodeId} returned HTTP {(int)response.StatusCode} from /invoke.");
            }

            var body = await response.Content.ReadAsStringAsync(cts.Token);
            var wire = string.IsNullOrWhiteSpace(body) ? null : JsonSerializer.Deserialize<WireResult>(body, Json);
            if (wire is null)
            {
                return NodeResult.Failed(invocation.TraceId, NodeErrorCode.Internal,
                    $"Node {Manifest.NodeId} returned an empty or unparseable /invoke response.");
            }

            return FromWire(wire, invocation);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Our deadline fired, not the caller's cancellation.
            _logger.LogWarning("Node {NodeId} exceeded its deadline at {Endpoint}.", Manifest.NodeId, Endpoint);
            return NodeResult.Failed(invocation.TraceId, NodeErrorCode.DeadlineExceeded,
                $"Node {Manifest.NodeId} did not respond before the invocation deadline.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Node {NodeId} was unreachable at {Endpoint}.", Manifest.NodeId, Endpoint);
            return NodeResult.Failed(invocation.TraceId, NodeErrorCode.DependencyUnavailable,
                $"Node {Manifest.NodeId} was unreachable: {ex.GetBaseException().Message}");
        }
    }

    // ── wire mapping (§5.3) ──

    internal static WireInvocation ToWire(NodeInvocation i) => new()
    {
        EnvelopeVersion = i.EnvelopeVersion,
        TraceId = i.TraceId,
        GoalId = i.GoalId,
        Capability = i.Capability,
        IssuedAt = i.IssuedAt,
        DeadlineAt = i.DeadlineAt,
        Principal = new WirePrincipal { Trust = i.Principal.Trust.ToString().ToLowerInvariant(), Id = i.Principal.Id },
        Taint = new WireTaint { Level = i.Taint.Level.ToString().ToLowerInvariant(), Sources = i.Taint.Sources },
        Broker = new WireBroker { Url = i.Broker.Url, Token = i.Broker.Token },
        Intent = i.Intent,
        SuccessCriteria = i.SuccessCriteria,
        SessionId = i.SessionId,
        Payload = i.Payload,
    };

    internal static NodeResult FromWire(WireResult w, NodeInvocation invocation)
    {
        var outcome = ParseOutcome(w.Outcome);

        // §5.3: trace_id MUST be echoed unchanged. A node that echoes the wrong one would scramble
        // telemetry correlation, so trust the invocation's, not the node's.
        var traceId = invocation.TraceId;

        return new NodeResult
        {
            EnvelopeVersion = string.IsNullOrWhiteSpace(w.EnvelopeVersion) ? NodeContractVersion.Current : w.EnvelopeVersion!,
            TraceId = traceId,
            Outcome = outcome,
            // Taint is monotonic (§5.3): a result may never be less tainted than its request.
            Taint = invocation.Taint.RaisedTo(ParseTaint(w.Taint)),
            Confidence = w.Confidence is { } c ? Confidence.Of(c) : Confidence.Unassessed,
            Payload = w.Payload ?? new Dictionary<string, string>(),
            Error = outcome == NodeOutcome.Error ? ParseError(w.Error) : null,
            Dependency = outcome == NodeOutcome.Blocked ? ParseDependency(w.Dependency) : null,
        };
    }

    private static NodeOutcome ParseOutcome(string? outcome) => outcome?.Trim().ToLowerInvariant() switch
    {
        "ok" => NodeOutcome.Ok,
        "blocked" => NodeOutcome.Blocked,
        // Anything unrecognized is an error, not an optimistic Ok. A node speaking a dialect we do not
        // understand has not demonstrated success.
        _ => NodeOutcome.Error,
    };

    private static TaintRef ParseTaint(WireTaint? t)
    {
        if (t is null) return TaintRef.Clean;
        var level = t.Level?.Trim().ToLowerInvariant() switch
        {
            "derived" => TaintLevel.Derived,
            "untrusted" => TaintLevel.Untrusted,
            _ => TaintLevel.Clean,
        };
        return new TaintRef(level, t.Sources ?? Array.Empty<string>());
    }

    private static NodeError ParseError(WireError? e)
    {
        if (e is null) return NodeError.Of(NodeErrorCode.Internal, "Node reported an error with no detail.");

        var code = e.Code?.Trim().ToUpperInvariant() switch
        {
            "INVALID_INPUT" => NodeErrorCode.InvalidInput,
            "PERMISSION_DENIED" => NodeErrorCode.PermissionDenied,
            "MODEL_UNAVAILABLE" => NodeErrorCode.ModelUnavailable,
            "DEPENDENCY_UNAVAILABLE" => NodeErrorCode.DependencyUnavailable,
            "DEADLINE_EXCEEDED" => NodeErrorCode.DeadlineExceeded,
            "NOT_IMPLEMENTED" => NodeErrorCode.NotImplemented,
            _ => NodeErrorCode.Internal,
        };

        return new NodeError(code, e.Message ?? "", e.Retryable ?? NodeError.DefaultRetryable(code));
    }

    private static NodeDependency ParseDependency(WireDependency? d)
    {
        if (d is null) return new NodeDependency(DependencyKind.PendingOutcome, "Node reported blocked with no detail.");

        var kind = d.Kind?.Trim().ToLowerInvariant() switch
        {
            "human-decision" => DependencyKind.HumanDecision,
            "missing-environment" => DependencyKind.MissingEnvironment,
            _ => DependencyKind.PendingOutcome,
        };

        return new NodeDependency(kind, d.Detail ?? "", d.ReferenceId);
    }

    internal static string Combine(string endpoint, string path) =>
        $"{endpoint.TrimEnd('/')}/{path.TrimStart('/')}";

    // ── wire DTOs: snake_case per the doc, kept separate from the domain records ──

    private sealed class HealthDto
    {
        public string? Status { get; set; }
    }

    internal sealed class WireInvocation
    {
        public string EnvelopeVersion { get; set; } = "";
        public string TraceId { get; set; } = "";
        public string GoalId { get; set; } = "";
        public string Capability { get; set; } = "";
        public DateTime IssuedAt { get; set; }
        public DateTime DeadlineAt { get; set; }
        public WirePrincipal? Principal { get; set; }
        public WireTaint? Taint { get; set; }
        public WireBroker? Broker { get; set; }
        public string Intent { get; set; } = "";
        public string? SuccessCriteria { get; set; }
        public string? SessionId { get; set; }
        public IReadOnlyDictionary<string, string> Payload { get; set; } = new Dictionary<string, string>();
    }

    internal sealed class WirePrincipal
    {
        public string? Trust { get; set; }
        public string? Id { get; set; }
    }

    internal sealed class WireTaint
    {
        public string? Level { get; set; }
        public IReadOnlyList<string>? Sources { get; set; }
    }

    internal sealed class WireBroker
    {
        public string? Url { get; set; }
        public string? Token { get; set; }
    }

    internal sealed class WireResult
    {
        public string? EnvelopeVersion { get; set; }
        public string? TraceId { get; set; }
        public string? Outcome { get; set; }
        public WireTaint? Taint { get; set; }
        public double? Confidence { get; set; }
        public IReadOnlyDictionary<string, string>? Payload { get; set; }
        public WireError? Error { get; set; }
        public WireDependency? Dependency { get; set; }
    }

    internal sealed class WireError
    {
        public string? Code { get; set; }
        public string? Message { get; set; }
        public bool? Retryable { get; set; }
    }

    internal sealed class WireDependency
    {
        public string? Kind { get; set; }
        public string? Detail { get; set; }
        public string? ReferenceId { get; set; }
    }
}
