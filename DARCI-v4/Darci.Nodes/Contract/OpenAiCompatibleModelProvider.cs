#nullable enable

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Nodes;

/// <summary>
/// An OpenAI-compatible chat-completions provider, for a LiteLLM proxy or anything else speaking
/// <c>POST {base}/chat/completions</c>.
///
/// <para><see cref="Kind"/> is the PROTOCOL (<c>openai</c>), not the deployment. A profile names the
/// deployment and declares the protocol, so one implementation serves any number of endpoints:
/// <c>"providers": { "litellm": { "kind": "openai", "base_url": "...", "api_key_env": "..." } }</c>.</para>
///
/// <para><b>The key never appears in a profile.</b> Profiles are committed; the provider reads the env var
/// the profile NAMES. <see cref="HostProfile.Validate"/> already refuses to start when that var is unset,
/// which is the §6.2 fail-fast-with-a-named-error rule doing its job.</para>
///
/// <para><b>Reasoning models need explicit handling</b>, learned the hard way against this very endpoint:
/// <c>coder-120b</c> returns its thinking in <c>message.reasoning_content</c> and its answer in
/// <c>message.content</c>. With too small a token budget the whole allowance goes to reasoning,
/// <c>finish_reason</c> comes back <c>length</c>, and <c>content</c> is an EMPTY STRING. A naive parser
/// reports a successful completion of "" — a silent, very confusing failure. So an empty answer that was
/// truncated is reported as a NAMED error instead, and the default budget here is generous.</para>
/// </summary>
public sealed class OpenAiCompatibleModelProvider : IModelProvider
{
    /// <summary>
    /// Deliberately larger than the Ollama provider's 1024. A reasoning model spends budget BEFORE it emits
    /// any answer, so a small default produces empty completions rather than short ones.
    /// </summary>
    public const int DefaultMaxTokens = 4096;

    private readonly HttpClient _http;
    private readonly ILogger<OpenAiCompatibleModelProvider> _logger;

    public OpenAiCompatibleModelProvider(HttpClient http, ILogger<OpenAiCompatibleModelProvider> logger)
    {
        _http = http;
        _logger = logger;
    }

    public string Kind => "openai";

    public async Task<ModelCompletion> CompleteAsync(
        ModelClassBinding binding, ModelProviderConfig config, ModelRequest request, CancellationToken ct = default)
    {
        if (ResolveKey(config) is not { } key)
        {
            return ModelCompletion.Failure(request.ModelClass, binding.Model,
                $"REMOTE_KEY_MISSING: provider needs env var '{config.ApiKeyEnv}', which is not set.");
        }

        var body = JsonSerializer.Serialize(new
        {
            model = binding.Model,
            messages = new[] { new { role = "user", content = request.Prompt } },
            temperature = request.Temperature ?? 0.7,
            max_tokens = request.MaxTokens ?? DefaultMaxTokens,
            stream = false,
        });

        using var cts = Linked(config, request.Timeout, ct);

        _logger.LogDebug("OpenAI-compatible complete class={Class} model={Model} endpoint={Url} promptLen={Len}",
            request.ModelClass, binding.Model, Url(config, "/chat/completions"), request.Prompt.Length);

        HttpResponseMessage response;
        string payload;
        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, Url(config, "/chat/completions")) { Content = content };
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            response = await _http.SendAsync(message, cts.Token);
            payload = await response.Content.ReadAsStringAsync(cts.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;   // genuine caller cancellation propagates
        }
        catch (OperationCanceledException)
        {
            // OUR bound fired, not the caller's. The Neo4j lesson: a remote that never answers must cost a
            // known amount and fail by name, never hang the call site.
            return ModelCompletion.Failure(request.ModelClass, binding.Model,
                $"REMOTE_TIMEOUT: {binding.Model} did not respond within the provider's bound.");
        }
        catch (Exception ex)
        {
            return ModelCompletion.Failure(request.ModelClass, binding.Model,
                $"REMOTE_UNREACHABLE: {ex.GetBaseException().Message}");
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return ModelCompletion.Failure(request.ModelClass, binding.Model, Classify(response.StatusCode, payload));

            return Parse(payload, binding, request);
        }
    }

    /// <summary>
    /// Embeddings over <c>POST {base}/embeddings</c>. Implemented because an OpenAI-compatible proxy
    /// commonly fronts an embedding model too, so a profile can legitimately bind
    /// <see cref="ModelClasses.EmbedText"/> here.
    /// </summary>
    public async Task<ModelEmbedding> EmbedAsync(
        ModelClassBinding binding, ModelProviderConfig config, EmbeddingRequest request, CancellationToken ct = default)
    {
        if (ResolveKey(config) is not { } key)
        {
            return ModelEmbedding.Failure(request.ModelClass, binding.Model,
                $"REMOTE_KEY_MISSING: provider needs env var '{config.ApiKeyEnv}', which is not set.");
        }

        var body = JsonSerializer.Serialize(new { model = binding.Model, input = request.Text });
        using var cts = Linked(config, request.Timeout, ct);

        try
        {
            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            using var message = new HttpRequestMessage(HttpMethod.Post, Url(config, "/embeddings")) { Content = content };
            message.Headers.TryAddWithoutValidation("Authorization", $"Bearer {key}");

            using var response = await _http.SendAsync(message, cts.Token);
            var payload = await response.Content.ReadAsStringAsync(cts.Token);

            if (!response.IsSuccessStatusCode)
                return ModelEmbedding.Failure(request.ModelClass, binding.Model, Classify(response.StatusCode, payload));

            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            var vector = Array.Empty<float>();
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0
                && data[0].TryGetProperty("embedding", out var emb) && emb.ValueKind == JsonValueKind.Array)
            {
                vector = new float[emb.GetArrayLength()];
                for (var i = 0; i < vector.Length; i++) vector[i] = emb[i].GetSingle();
            }

            return new ModelEmbedding
            {
                Succeeded = vector.Length > 0,
                Vector = vector,
                ModelClass = request.ModelClass,
                ResolvedModel = binding.Model,
                TokensIn = Usage(root, "prompt_tokens"),
                Error = vector.Length > 0 ? null : "REMOTE_EMPTY_EMBEDDING: response carried no embedding vector.",
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ModelEmbedding.Failure(request.ModelClass, binding.Model,
                $"REMOTE_TIMEOUT: {binding.Model} did not respond within the provider's bound.");
        }
        catch (Exception ex)
        {
            return ModelEmbedding.Failure(request.ModelClass, binding.Model,
                $"REMOTE_UNREACHABLE: {ex.GetBaseException().Message}");
        }
    }

    // ── parsing ──

    internal ModelCompletion Parse(string payload, ModelClassBinding binding, ModelRequest request)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(payload); }
        catch (JsonException ex)
        {
            return ModelCompletion.Failure(request.ModelClass, binding.Model,
                $"REMOTE_BAD_RESPONSE: not valid JSON ({ex.Message}).");
        }

        using (doc)
        {
            var root = doc.RootElement;

            if (!root.TryGetProperty("choices", out var choices)
                || choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() == 0)
            {
                return ModelCompletion.Failure(request.ModelClass, binding.Model,
                    "REMOTE_BAD_RESPONSE: response carried no choices.");
            }

            var choice = choices[0];
            var finish = choice.TryGetProperty("finish_reason", out var f) ? f.GetString() ?? "" : "";
            var text = "";
            var reasoningLength = 0;

            if (choice.TryGetProperty("message", out var msg))
            {
                text = msg.TryGetProperty("content", out var c) ? c.GetString()?.Trim() ?? "" : "";
                reasoningLength = msg.TryGetProperty("reasoning_content", out var rc) ? (rc.GetString() ?? "").Length : 0;
            }

            var tokensIn = Usage(root, "prompt_tokens");
            var tokensOut = Usage(root, "completion_tokens");

            // The reasoning-model trap: all budget spent thinking, nothing left to answer with. Reporting
            // this as a successful empty completion would send a caller hunting through its own prompt.
            if (text.Length == 0)
            {
                var why = finish == "length"
                    ? $"the token budget ({request.MaxTokens ?? DefaultMaxTokens}) was consumed before any answer was emitted"
                      + (reasoningLength > 0 ? $" — {tokensOut} completion token(s) went to reasoning" : "")
                    : $"finish_reason was '{finish}'";

                return ModelCompletion.Failure(request.ModelClass, binding.Model,
                    $"REMOTE_EMPTY_COMPLETION: {binding.Model} returned no content because {why}.");
            }

            return new ModelCompletion
            {
                Succeeded = true,
                Text = text,
                ModelClass = request.ModelClass,
                ResolvedModel = binding.Model,
                ProviderKind = Kind,
                TokensIn = tokensIn,
                TokensOut = tokensOut,
            };
        }
    }

    /// <summary>Turn an HTTP status into something an operator can act on, with the server's own detail.</summary>
    internal static string Classify(System.Net.HttpStatusCode status, string payload)
    {
        var detail = Detail(payload);
        return (int)status switch
        {
            401 or 403 => $"REMOTE_UNAUTHORIZED: the endpoint rejected the API key (HTTP {(int)status}). {detail}",
            404 => $"REMOTE_NOT_FOUND: no such route — check whether the base URL needs a /v1 suffix (HTTP 404). {detail}",
            400 => $"REMOTE_BAD_REQUEST: {detail} (HTTP 400) — often an unknown model alias.",
            429 => $"REMOTE_RATE_LIMITED: {detail} (HTTP 429).",
            >= 500 => $"REMOTE_SERVER_ERROR: HTTP {(int)status}. {detail}",
            _ => $"REMOTE_HTTP_{(int)status}: {detail}",
        };
    }

    /// <summary>Pull the OpenAI <c>error.message</c> out, falling back to a trimmed body.</summary>
    private static string Detail(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload)) return "(no body)";
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                if (err.ValueKind == JsonValueKind.String) return err.GetString() ?? "";
                if (err.TryGetProperty("message", out var m)) return m.GetString() ?? "";
            }
        }
        catch (JsonException) { /* fall through to the raw body */ }

        return payload.Length <= 300 ? payload.Trim() : payload[..300].Trim() + "…";
    }

    private static int Usage(JsonElement root, string name) =>
        root.TryGetProperty("usage", out var u) && u.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number
            ? v.GetInt32()
            : 0;

    private static string? ResolveKey(ModelProviderConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.ApiKeyEnv)) return null;
        var key = Environment.GetEnvironmentVariable(config.ApiKeyEnv);
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    internal static string Url(ModelProviderConfig config, string path) =>
        $"{(config.BaseUrl ?? "https://api.openai.com/v1").TrimEnd('/')}{path}";

    /// <summary>Per-request timeout, bounded by the provider ceiling, linked to the caller's token.</summary>
    private static CancellationTokenSource Linked(ModelProviderConfig config, TimeSpan? requested, CancellationToken ct)
    {
        var ceiling = TimeSpan.FromMinutes(Math.Max(1, config.TimeoutMinutes));
        var timeout = requested is { } r && r < ceiling ? r : ceiling;
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }
}
