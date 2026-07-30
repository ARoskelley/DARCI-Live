#nullable enable

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darci.Nodes;

/// <summary>Node kinds (doc §4).</summary>
public enum NodeKind { Capability = 0, Environment = 1, Adapter = 2 }

/// <summary>One routable capability a node advertises (doc §5.1 `capabilities[]`).</summary>
public sealed record NodeCapabilityDescriptor
{
    /// <summary>The routable verb, namespaced `domain.action`. Validated at registration.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = "";
    [JsonPropertyName("description")] public string Description { get; init; } = "";

    /// <summary>JSON Schema (or a `$ref`) for the payload. Carried in Phase 1; schema ENFORCEMENT is a later
    /// pass — declaring it now is what lets a coding agent write against the contract.</summary>
    [JsonPropertyName("input_schema")] public JsonElement? InputSchema { get; init; }
    [JsonPropertyName("output_schema")] public JsonElement? OutputSchema { get; init; }

    [JsonPropertyName("typical_latency_ms")] public int TypicalLatencyMs { get; init; } = 1000;

    /// <summary>Per-invocation budget. The dispatcher derives <see cref="NodeInvocation.DeadlineAt"/> from
    /// this — and it can never lengthen or shorten the work record's lease.</summary>
    [JsonPropertyName("deadline_ms")] public int DeadlineMs { get; init; } = 300_000;
}

/// <summary>
/// What a node needs (doc §5.1 `requires`). ADD-5b: populated as REAL INVENTORY in Phase 1 even though
/// nothing enforces it yet — this inventory is the design input for the memory/model brokers in Phase 2.
/// </summary>
public sealed record NodeRequires
{
    /// <summary>Model CLASSES (never model names — doc P4/§6.2), e.g. "chat.balanced", "code.generate".</summary>
    [JsonPropertyName("model_classes")] public IReadOnlyList<string> ModelClasses { get; init; } = Array.Empty<string>();

    /// <summary>Memory scopes, e.g. "read:documents", "write:innovated" (doc §6.1).</summary>
    [JsonPropertyName("memory_scopes")] public IReadOnlyList<string> MemoryScopes { get; init; } = Array.Empty<string>();

    [JsonPropertyName("permissions")] public IReadOnlyList<string> Permissions { get; init; } = Array.Empty<string>();

    /// <summary>True if any output can carry content the node did not author (doc §5.1/§7).</summary>
    [JsonPropertyName("emits_untrusted")] public bool EmitsUntrusted { get; init; }
}

/// <summary>
/// A node's `darci-node.json` (doc §5.1) — "the single source of truth about what a node is and needs".
///
/// <para><b>Phase E capability invariant (§14c):</b> extending the capability surface is an upward crossing,
/// so it must be a HUMAN-AUTHORED act. The human act is a REVIEWED MANIFEST MERGED INTO THE REPO — there is
/// no runtime self-registration (doc D2). Registration records <see cref="ComputeSha256"/> so a changed
/// capability surface is visible and auditable after the fact.</para>
/// </summary>
public sealed record NodeManifest
{
    [JsonPropertyName("contract_version")] public string ContractVersion { get; init; } = NodeContractVersion.Current;
    [JsonPropertyName("node_id")] public string NodeId { get; init; } = "";
    [JsonPropertyName("display_name")] public string DisplayName { get; init; } = "";
    [JsonPropertyName("node_version")] public string NodeVersion { get; init; } = "0.1.0";
    [JsonPropertyName("kind")] public NodeKind Kind { get; init; } = NodeKind.Capability;

    /// <summary>Null/empty for an IN-PROCESS node (Phase 1). An HTTP base URL once nodes go out-of-process.</summary>
    [JsonPropertyName("endpoint")] public string? Endpoint { get; init; }

    [JsonPropertyName("capabilities")] public IReadOnlyList<NodeCapabilityDescriptor> Capabilities { get; init; }
        = Array.Empty<NodeCapabilityDescriptor>();

    [JsonPropertyName("requires")] public NodeRequires Requires { get; init; } = new();

    [JsonPropertyName("health")] public string Health { get; init; } = "/health";
    [JsonPropertyName("author")] public string? Author { get; init; }
    [JsonPropertyName("repository")] public string? Repository { get; init; }

    // ── Environment extras (doc §8); unused by capability nodes ──
    [JsonPropertyName("workspace_root")] public string? WorkspaceRoot { get; init; }
    [JsonPropertyName("max_disk_mb")] public int? MaxDiskMb { get; init; }
    [JsonPropertyName("max_runtime_s")] public int? MaxRuntimeS { get; init; }
    [JsonPropertyName("network")] public string? Network { get; init; }

    public bool IsInProcess => string.IsNullOrWhiteSpace(Endpoint);

    /// <summary>Stable SHA-256 of the manifest's canonical JSON — the audit anchor for the §14c human act.</summary>
    public string ComputeSha256()
    {
        var json = JsonSerializer.Serialize(this, ManifestJson.Options);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }

    /// <summary>Validate the manifest (doc §5.5 step 2). Failure here is fatal AND NAMED — never silent.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!NodeContractVersion.IsSupported(ContractVersion))
            errors.Add($"contract_version '{ContractVersion}' is not supported by this core (supported: {string.Join(", ", NodeContractVersion.Supported)}).");

        if (string.IsNullOrWhiteSpace(NodeId))
            errors.Add("node_id is required.");
        else if (!NodeId.Contains('.', StringComparison.Ordinal))
            errors.Add($"node_id '{NodeId}' must be namespaced (e.g. 'darci.coding').");

        if (string.IsNullOrWhiteSpace(NodeVersion)) errors.Add("node_version is required.");

        if (Capabilities.Count == 0)
            errors.Add($"node '{NodeId}' declares no capabilities.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in Capabilities)
        {
            if (!CapabilityKey.IsValidName(c.Name))
                errors.Add($"capability name '{c.Name}' is not a valid namespaced 'domain.action' verb.");
            else if (!seen.Add(c.Name))
                errors.Add($"capability '{c.Name}' is declared more than once by node '{NodeId}'.");

            if (c.DeadlineMs <= 0) errors.Add($"capability '{c.Name}' has a non-positive deadline_ms.");
        }

        if (Kind == NodeKind.Environment && string.IsNullOrWhiteSpace(WorkspaceRoot))
            errors.Add($"environment node '{NodeId}' must declare a workspace_root (doc §8).");

        return errors;
    }
}

/// <summary>Shared JSON settings for manifests: snake_case names come from attributes; enums as strings.</summary>
public static class ManifestJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static readonly JsonSerializerOptions Pretty = new(Options) { WriteIndented = true };
}
