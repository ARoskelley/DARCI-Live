#nullable enable

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>A concrete inference backend. One per <see cref="ModelProviderConfig.Kind"/>.</summary>
public interface IModelProvider
{
    string Kind { get; }
    Task<ModelCompletion> CompleteAsync(ModelClassBinding binding, ModelProviderConfig config, ModelRequest request, CancellationToken ct = default);
    Task<ModelEmbedding> EmbedAsync(ModelClassBinding binding, ModelProviderConfig config, EmbeddingRequest request, CancellationToken ct = default);
}

/// <summary>
/// The model broker (doc §6.2): resolves a CLASS to a concrete provider+model via the host profile, invokes
/// it, and reports what actually ran (resolved model, tokens, duration) so telemetry can answer "which model
/// did this work, and what did it cost".
///
/// <para>Failures are returned, never thrown (except genuine caller cancellation): every existing call site
/// degrades gracefully today, and that behavior must survive the broker's introduction.</para>
/// </summary>
public sealed class ModelBroker : IModelBroker
{
    private readonly IReadOnlyDictionary<string, IModelProvider> _providers;
    private readonly ILogger<ModelBroker> _logger;

    public ModelBroker(HostProfile profile, IEnumerable<IModelProvider> providers, ILogger<ModelBroker> logger)
    {
        Profile = profile;
        _providers = providers.ToDictionary(p => p.Kind, StringComparer.OrdinalIgnoreCase);
        _logger = logger;

        var errors = profile.Validate();
        if (errors.Count > 0)
            throw new HostProfileException($"Host profile '{profile.ProfileId}' is invalid: {string.Join(" | ", errors)}");

        // Every class must resolve to a provider kind we can actually serve — fail at construction, not
        // mid-task (doc §6.2).
        foreach (var c in ModelClasses.All)
        {
            var binding = profile.Resolve(c)!;
            var kind = profile.Provider(binding.Provider)!.Kind;
            if (!_providers.ContainsKey(kind))
                throw new HostProfileException(
                    $"Class '{c}' needs provider kind '{kind}' (via '{binding.Provider}'), which this host has no " +
                    $"implementation for. Available: {string.Join(", ", _providers.Keys)}.");
        }

        _logger.LogInformation("Model broker ready on profile '{Profile}': {Bindings}",
            profile.ProfileId,
            string.Join(", ", ModelClasses.All.Select(c => $"{c}→{profile.Resolve(c)!.Model}")));
    }

    public HostProfile Profile { get; }

    public string? ResolveModelName(string modelClass) => Profile.Resolve(modelClass)?.Model;

    public async Task<ModelCompletion> CompleteAsync(ModelRequest request, CancellationToken ct = default)
    {
        if (!TryResolve(request.ModelClass, out var binding, out var config, out var provider, out var error))
            return ModelCompletion.Failure(request.ModelClass, "", error!);

        var sw = Stopwatch.StartNew();
        try
        {
            var completion = await provider!.CompleteAsync(binding!, config!, request, ct);
            sw.Stop();
            return completion with { DurationMs = sw.ElapsedMilliseconds, ModelClass = request.ModelClass };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // genuine caller cancellation propagates
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Model completion failed for class {Class} (model {Model}).",
                request.ModelClass, binding!.Model);
            return ModelCompletion.Failure(request.ModelClass, binding.Model, $"{ex.GetType().Name}: {ex.Message}", sw.ElapsedMilliseconds);
        }
    }

    public async Task<ModelEmbedding> EmbedAsync(EmbeddingRequest request, CancellationToken ct = default)
    {
        if (!TryResolve(request.ModelClass, out var binding, out var config, out var provider, out var error))
            return ModelEmbedding.Failure(request.ModelClass, "", error!);

        var sw = Stopwatch.StartNew();
        try
        {
            var embedding = await provider!.EmbedAsync(binding!, config!, request, ct);
            sw.Stop();
            return embedding with { DurationMs = sw.ElapsedMilliseconds, ModelClass = request.ModelClass };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogDebug(ex, "Model embedding failed for class {Class} (model {Model}).",
                request.ModelClass, binding!.Model);
            return ModelEmbedding.Failure(request.ModelClass, binding.Model, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private bool TryResolve(
        string modelClass,
        out ModelClassBinding? binding,
        out ModelProviderConfig? config,
        out IModelProvider? provider,
        out string? error)
    {
        binding = null; config = null; provider = null; error = null;

        binding = Profile.Resolve(modelClass);
        if (binding is null) { error = $"class '{modelClass}' is not bound by profile '{Profile.ProfileId}'."; return false; }

        config = Profile.Provider(binding.Provider);
        if (config is null) { error = $"provider '{binding.Provider}' is not declared."; return false; }

        if (!_providers.TryGetValue(config.Kind, out provider))
        {
            error = $"no implementation for provider kind '{config.Kind}'.";
            return false;
        }
        return true;
    }
}

/// <summary>
/// Ollama-backed provider. Speaks <c>/api/generate</c> and <c>/api/embed</c>, and captures Ollama's
/// <c>prompt_eval_count</c>/<c>eval_count</c> as token counts — the numbers the doc's §6.3 telemetry wants
/// and which no caller could report while holding its own HTTP client.
/// </summary>
public sealed class OllamaModelProvider : IModelProvider
{
    private readonly HttpClient _http;
    private readonly ILogger<OllamaModelProvider> _logger;

    public OllamaModelProvider(HttpClient http, ILogger<OllamaModelProvider> logger)
    {
        _http = http;
        _logger = logger;
        // No BaseAddress here: the profile supplies an absolute URL per provider, so one HttpClient can serve
        // several Ollama endpoints if a profile ever declares them.
    }

    public string Kind => "ollama";

    public async Task<ModelCompletion> CompleteAsync(
        ModelClassBinding binding, ModelProviderConfig config, ModelRequest request, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            model = binding.Model,
            prompt = request.Prompt,
            stream = false,
            options = new
            {
                temperature = request.Temperature ?? 0.7,
                num_predict = request.MaxTokens ?? 1024,
            },
        });

        using var cts = Linked(config, request.Timeout, ct);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        _logger.LogDebug("Ollama complete class={Class} model={Model} promptLen={Len}",
            request.ModelClass, binding.Model, request.Prompt.Length);

        var response = await _http.PostAsync(Url(config, "/api/generate"), content, cts.Token);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        var root = doc.RootElement;

        return new ModelCompletion
        {
            Succeeded = true,
            Text = root.TryGetProperty("response", out var r) ? r.GetString()?.Trim() ?? "" : "",
            ModelClass = request.ModelClass,
            ResolvedModel = binding.Model,
            ProviderKind = Kind,
            TokensIn = Int(root, "prompt_eval_count"),
            TokensOut = Int(root, "eval_count"),
        };
    }

    public async Task<ModelEmbedding> EmbedAsync(
        ModelClassBinding binding, ModelProviderConfig config, EmbeddingRequest request, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new { model = binding.Model, input = request.Text });

        using var cts = Linked(config, request.Timeout, ct);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(Url(config, "/api/embed"), content, cts.Token);
        response.EnsureSuccessStatusCode();

        using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(cts.Token), cancellationToken: cts.Token);
        var root = doc.RootElement;

        var vector = Array.Empty<float>();
        if (root.TryGetProperty("embeddings", out var embs) && embs.ValueKind == JsonValueKind.Array && embs.GetArrayLength() > 0)
        {
            var first = embs[0];
            vector = new float[first.GetArrayLength()];
            for (var i = 0; i < vector.Length; i++) vector[i] = first[i].GetSingle();
        }

        return new ModelEmbedding
        {
            Succeeded = true,
            Vector = vector,
            ModelClass = request.ModelClass,
            ResolvedModel = binding.Model,
            TokensIn = Int(root, "prompt_eval_count"),
        };
    }

    private static string Url(ModelProviderConfig config, string path) =>
        $"{(config.BaseUrl ?? "http://localhost:11434").TrimEnd('/')}{path}";

    /// <summary>Per-request timeout, bounded by the provider ceiling, linked to the caller's token.</summary>
    private static CancellationTokenSource Linked(ModelProviderConfig config, TimeSpan? requested, CancellationToken ct)
    {
        var ceiling = TimeSpan.FromMinutes(Math.Max(1, config.TimeoutMinutes));
        var timeout = requested is { } r && r < ceiling ? r : ceiling;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private static int Int(JsonElement root, string name) =>
        root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
