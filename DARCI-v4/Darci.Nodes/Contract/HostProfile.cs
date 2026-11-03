#nullable enable

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Darci.Nodes;

/// <summary>A model provider a profile can bind classes to.</summary>
public sealed record ModelProviderConfig
{
    /// <summary>Provider kind: <c>"ollama"</c> today; <c>"anthropic"</c> etc. later.</summary>
    [JsonPropertyName("kind")] public string Kind { get; init; } = "ollama";

    [JsonPropertyName("base_url")] public string? BaseUrl { get; init; }

    /// <summary>Per-provider request ceiling. A request may specify a shorter one.</summary>
    [JsonPropertyName("timeout_minutes")] public int TimeoutMinutes { get; init; } = 12;

    /// <summary>Name of the ENV VAR holding this provider's key. Never the key itself — profiles are
    /// committed to the repo, secrets are not.</summary>
    [JsonPropertyName("api_key_env")] public string? ApiKeyEnv { get; init; }
}

/// <summary>What a class resolves to on this host.</summary>
public sealed record ModelClassBinding
{
    [JsonPropertyName("provider")] public string Provider { get; init; } = "";
    [JsonPropertyName("model")] public string Model { get; init; } = "";

    /// <summary>Optional honesty note — e.g. "aspirational: same model as balanced on this hardware".</summary>
    [JsonPropertyName("note")] public string? Note { get; init; }
}

/// <summary>
/// <c>host-profile.json</c> (doc §6.2): the ONE file a collaborator edits to run DARCI on different hardware.
/// It maps every <see cref="ModelClasses"/> entry to a concrete provider + model.
///
/// <para>Validation is deliberately strict and startup-time: <i>"A host that cannot satisfy a class a node
/// requires fails at registration with a named error, not mid-task."</i> That is the lesson from the
/// gemma4:e4b typo, which failed silently at runtime with 404s on every generation instead of loudly at boot.</para>
/// </summary>
public sealed record HostProfile
{
    [JsonPropertyName("profile_id")] public string ProfileId { get; init; } = "default";
    [JsonPropertyName("description")] public string? Description { get; init; }

    [JsonPropertyName("providers")]
    public IReadOnlyDictionary<string, ModelProviderConfig> Providers { get; init; }
        = new Dictionary<string, ModelProviderConfig>();

    [JsonPropertyName("classes")]
    public IReadOnlyDictionary<string, ModelClassBinding> Classes { get; init; }
        = new Dictionary<string, ModelClassBinding>();

    /// <summary>The binding for a class, or null if the profile does not bind it.</summary>
    public ModelClassBinding? Resolve(string modelClass) =>
        Classes.TryGetValue(modelClass, out var b) ? b : null;

    public ModelProviderConfig? Provider(string name) =>
        Providers.TryGetValue(name, out var p) ? p : null;

    /// <summary>Named, exhaustive validation errors. Empty list = usable profile.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(ProfileId)) errors.Add("profile_id is required.");
        if (Providers.Count == 0) errors.Add("no providers declared.");

        foreach (var (name, p) in Providers)
        {
            if (string.IsNullOrWhiteSpace(p.Kind))
                errors.Add($"provider '{name}' has no kind.");
            if (p.TimeoutMinutes <= 0)
                errors.Add($"provider '{name}' has a non-positive timeout_minutes.");
            if (string.Equals(p.Kind, "ollama", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(p.BaseUrl))
                errors.Add($"ollama provider '{name}' needs a base_url.");
            if (!string.IsNullOrWhiteSpace(p.ApiKeyEnv) && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(p.ApiKeyEnv)))
                errors.Add($"provider '{name}' requires env var '{p.ApiKeyEnv}', which is not set.");
        }

        // EVERY class must bind — an unsatisfiable class must fail at startup, not mid-task.
        foreach (var c in ModelClasses.All)
        {
            if (!Classes.TryGetValue(c, out var binding))
            {
                errors.Add($"class '{c}' is not bound by profile '{ProfileId}'.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(binding.Model))
                errors.Add($"class '{c}' binds no model.");
            if (string.IsNullOrWhiteSpace(binding.Provider) || Provider(binding.Provider) is null)
                errors.Add($"class '{c}' names provider '{binding.Provider}', which is not declared.");
        }

        foreach (var declared in Classes.Keys.Where(k => !ModelClasses.IsKnown(k)))
            errors.Add($"profile '{ProfileId}' binds unknown class '{declared}'.");

        return errors;
    }

    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };
}

/// <summary>Thrown when the host profile cannot be loaded or is unusable. Fatal and named, by design.</summary>
public sealed class HostProfileException : Exception
{
    public HostProfileException(string message) : base(message) { }
}

/// <summary>
/// Loads <c>host-profile.json</c>, or synthesizes one from the legacy <c>DARCI_OLLAMA_*</c> environment
/// variables so an existing install keeps working unchanged when no profile file is present.
/// </summary>
public static class HostProfileLoader
{
    public const string FileName = "host-profile.json";

    /// <summary>Load from an explicit path; throws <see cref="HostProfileException"/> if missing or invalid.</summary>
    public static HostProfile LoadFile(string path)
    {
        string raw;
        try { raw = File.ReadAllText(path); }
        catch (Exception ex) { throw new HostProfileException($"Could not read host profile '{path}': {ex.Message}"); }

        HostProfile? profile;
        try { profile = JsonSerializer.Deserialize<HostProfile>(raw, HostProfile.Json); }
        catch (JsonException ex) { throw new HostProfileException($"Host profile '{path}' is not valid JSON: {ex.Message}"); }

        if (profile is null) throw new HostProfileException($"Host profile '{path}' deserialized to null.");

        var errors = profile.Validate();
        if (errors.Count > 0)
            throw new HostProfileException($"Host profile '{path}' is invalid: {string.Join(" | ", errors)}");

        return profile;
    }

    /// <summary>Load <paramref name="path"/> if it exists, otherwise fall back to
    /// <see cref="FromEnvironment"/>. Returns the profile and whether the file was used.</summary>
    public static (HostProfile Profile, bool FromFile) LoadOrDefault(string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            return (LoadFile(path), true);
        return (FromEnvironment(), false);
    }

    /// <summary>
    /// The compatibility profile: built from the same environment variables the pre-broker code read, so
    /// routing is BIT-IDENTICAL to today when no profile file exists. This is what makes introducing the
    /// broker a non-event behaviorally.
    /// </summary>
    public static HostProfile FromEnvironment()
    {
        var general = Env("DARCI_OLLAMA_MODEL") ?? "gemma2:9b";
        var coding = Env("DARCI_OLLAMA_CODING_MODEL") ?? "qwen2.5-coder:7b";
        var fastCoding = Env("DARCI_OLLAMA_FAST_CODING_MODEL") ?? coding;
        var embed = Env("DARCI_OLLAMA_EMBED_MODEL") ?? Env("DARCI_OLLAMA_EMBEDDING_MODEL") ?? "nomic-embed-text";
        var baseUrl = NormalizeBaseUrl(Env("DARCI_OLLAMA_BASE_URL") ?? Env("OLLAMA_HOST") ?? "http://localhost:11434");

        return new HostProfile
        {
            ProfileId = "env-compat",
            Description = "Synthesized from DARCI_OLLAMA_* environment variables (no host-profile.json found). " +
                          "Reproduces the pre-broker routing exactly.",
            Providers = new Dictionary<string, ModelProviderConfig>
            {
                ["ollama"] = new() { Kind = "ollama", BaseUrl = baseUrl, TimeoutMinutes = 12 },
            },
            Classes = new Dictionary<string, ModelClassBinding>
            {
                [ModelClasses.ChatFast] = new() { Provider = "ollama", Model = general },
                [ModelClasses.ChatBalanced] = new() { Provider = "ollama", Model = general },
                [ModelClasses.ChatDeep] = new() { Provider = "ollama", Model = general },
                [ModelClasses.ClassifyIntent] = new() { Provider = "ollama", Model = general },
                [ModelClasses.EmbedText] = new() { Provider = "ollama", Model = embed },
                [ModelClasses.CodeGenerate] = new() { Provider = "ollama", Model = coding },
                [ModelClasses.CodeFast] = new() { Provider = "ollama", Model = fastCoding },
            },
        };
    }

    private static string? Env(string name)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
    }

    private static string NormalizeBaseUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            url = $"http://{url}";
        return url.TrimEnd('/');
    }
}
