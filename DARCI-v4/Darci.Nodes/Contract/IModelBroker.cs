#nullable enable

namespace Darci.Nodes;

/// <summary>
/// A text-generation request, addressed by CLASS (never a model name).
/// <para><see cref="Temperature"/>/<see cref="MaxTokens"/>/<see cref="Timeout"/> are per-request because the
/// existing callers genuinely differ (the coding path uses temperature 0.4 / 4096 tokens, the general path
/// 0.7 / 1024). Unifying those into one broker-wide setting would be a silent behavior change, so the broker
/// carries them instead.</para>
/// </summary>
public sealed record ModelRequest(string ModelClass, string Prompt)
{
    public double? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public TimeSpan? Timeout { get; init; }

    /// <summary>Optional label for telemetry (e.g. "innovation.synthesize") — never affects routing.</summary>
    public string? Purpose { get; init; }
}

/// <summary>The outcome of a generation. Failure is reported, NOT thrown: callers historically degrade
/// gracefully (returning a sentinel or empty string) and that behavior must survive.</summary>
public sealed record ModelCompletion
{
    public bool Succeeded { get; init; }
    public string Text { get; init; } = "";

    /// <summary>The class that was requested.</summary>
    public string ModelClass { get; init; } = "";
    /// <summary>The concrete model the profile resolved it to — the telemetry answer to "what actually ran".</summary>
    public string ResolvedModel { get; init; } = "";
    public string ProviderKind { get; init; } = "";

    public int TokensIn { get; init; }
    public int TokensOut { get; init; }
    public long DurationMs { get; init; }

    public string? Error { get; init; }

    public static ModelCompletion Failure(string modelClass, string resolvedModel, string error, long durationMs = 0) =>
        new() { Succeeded = false, ModelClass = modelClass, ResolvedModel = resolvedModel, Error = error, DurationMs = durationMs };
}

/// <summary>An embedding request, addressed by class (normally <see cref="ModelClasses.EmbedText"/>).</summary>
public sealed record EmbeddingRequest(string Text)
{
    public string ModelClass { get; init; } = ModelClasses.EmbedText;
    public TimeSpan? Timeout { get; init; }
    public string? Purpose { get; init; }
}

public sealed record ModelEmbedding
{
    public bool Succeeded { get; init; }
    public IReadOnlyList<float> Vector { get; init; } = Array.Empty<float>();
    public string ModelClass { get; init; } = ModelClasses.EmbedText;
    public string ResolvedModel { get; init; } = "";
    public int TokensIn { get; init; }
    public long DurationMs { get; init; }
    public string? Error { get; init; }

    public static ModelEmbedding Failure(string modelClass, string resolvedModel, string error) =>
        new() { Succeeded = false, ModelClass = modelClass, ResolvedModel = resolvedModel, Error = error };
}

/// <summary>
/// THE MODEL BROKER (doc §6.2). The single place inference is invoked, and the single place a class becomes
/// a concrete model.
///
/// <para>Why it exists: (1) collaborator portability — a node asks for <c>chat.balanced</c> and the host
/// profile decides what that means; (2) it is where token accounting and model-level telemetry happen, which
/// is impossible if callers hold their own provider clients; (3) it removes the hardcoded-model bypass.</para>
///
/// <para>Note: <see cref="IModelBroker"/> is the mechanism, not a mandate to rewrite every call site. The
/// existing <c>IModelRouter</c>/<c>IOllamaClient</c> interfaces remain as thin adapters over it (Phase 2 fork
/// F4) — the same reasoning as keeping the capability enums in Phase 1: mass mechanical conversion carries
/// real risk for zero behavioral gain.</para>
/// </summary>
public interface IModelBroker
{
    /// <summary>The active host profile — exposed for diagnostics and telemetry (<c>host_profile_id</c>).</summary>
    HostProfile Profile { get; }

    Task<ModelCompletion> CompleteAsync(ModelRequest request, CancellationToken ct = default);

    Task<ModelEmbedding> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default);

    /// <summary>The concrete model a class resolves to on this host, or null if unbound.</summary>
    string? ResolveModelName(string modelClass);
}
