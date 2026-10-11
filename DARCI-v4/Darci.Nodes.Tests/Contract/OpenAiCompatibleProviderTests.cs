#nullable enable

using System.Net;
using System.Text;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// The OpenAI-compatible (LiteLLM) provider. Mocked HTTP, because what needs proving is how this code
/// reacts to each server behaviour — and some of those behaviours are hard to provoke on demand against a
/// live endpoint.
///
/// <para>The empty-completion tests are the ones that matter most. They encode a trap found against the
/// real endpoint: <c>coder-120b</c> is a reasoning model, and with too small a token budget it spends the
/// whole allowance thinking, returns <c>finish_reason: length</c>, and leaves <c>content</c> an EMPTY
/// STRING. A parser that just reads <c>content</c> reports a SUCCESSFUL completion of "" — a silent failure
/// that would send a caller hunting through its own prompt for the bug.</para>
/// </summary>
public sealed class OpenAiCompatibleProviderTests
{
    private const string KeyEnv = "DARCI_TEST_OPENAI_KEY";

    private static ModelProviderConfig Config(string? apiKeyEnv = KeyEnv, int timeoutMinutes = 12) => new()
    {
        Kind = "openai",
        BaseUrl = "https://proxy.example.test/v1",
        ApiKeyEnv = apiKeyEnv,
        TimeoutMinutes = timeoutMinutes,
    };

    private static ModelClassBinding Binding => new() { Provider = "litellm", Model = "coder-120b" };

    private static ModelRequest Request(int? maxTokens = null) =>
        new(ModelClasses.CodeGenerate, "write a checksum") { MaxTokens = maxTokens };

    private static OpenAiCompatibleModelProvider Provider(StubHandler handler) =>
        new(new HttpClient(handler), NullLogger<OpenAiCompatibleModelProvider>.Instance);

    /// <summary>Sets the key env var for the duration of a test, then restores it.</summary>
    private sealed class KeyScope : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(KeyEnv);
        public KeyScope(string? value = "sk-test-not-a-real-key")
            => Environment.SetEnvironmentVariable(KeyEnv, value);
        public void Dispose() => Environment.SetEnvironmentVariable(KeyEnv, _previous);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly TimeSpan _delay;

        public StubHandler(HttpStatusCode status, string body, TimeSpan? delay = null)
        {
            _status = status;
            _body = body;
            _delay = delay ?? TimeSpan.Zero;
        }

        public string? LastAuthorization { get; private set; }
        public string? LastUri { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            LastAuthorization = request.Headers.TryGetValues("Authorization", out var v) ? string.Join("", v) : null;
            LastUri = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);

            if (_delay > TimeSpan.Zero) await Task.Delay(_delay, ct);

            return new HttpResponseMessage(_status) { Content = new StringContent(_body, Encoding.UTF8, "application/json") };
        }
    }

    // ── success + token parsing ──

    private const string SuccessBody = """
        {"id":"abc","model":"coder-120b","object":"chat.completion",
         "choices":[{"finish_reason":"stop","index":0,
                     "message":{"role":"assistant","content":"\n\nBROKER OK","reasoning_content":"thinking..."}}],
         "usage":{"prompt_tokens":17,"completion_tokens":36,"total_tokens":53,
                  "completion_tokens_details":{"reasoning_tokens":30,"text_tokens":6}}}
        """;

    [Fact]
    public async Task ASuccessfulCompletion_ReturnsTheContentAndTokenUsage()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal("BROKER OK", result.Text);          // trimmed
        Assert.Equal("coder-120b", result.ResolvedModel);
        Assert.Equal("openai", result.ProviderKind);
        Assert.Equal(17, result.TokensIn);
        Assert.Equal(36, result.TokensOut);
    }

    [Fact]
    public async Task ItPostsToChatCompletions_WithABearerKeyAndTheBoundModel()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);

        await Provider(handler).CompleteAsync(Binding, Config(), Request(maxTokens: 2048));

        Assert.Equal("https://proxy.example.test/v1/chat/completions", handler.LastUri);
        Assert.Equal("Bearer sk-test-not-a-real-key", handler.LastAuthorization);
        Assert.Contains("\"model\":\"coder-120b\"", handler.LastBody);
        Assert.Contains("\"max_tokens\":2048", handler.LastBody);
        Assert.Contains("\"role\":\"user\"", handler.LastBody);
    }

    [Fact]
    public async Task TheDefaultTokenBudget_IsGenerous_BecauseReasoningModelsSpendItBeforeAnswering()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);

        await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.Contains($"\"max_tokens\":{OpenAiCompatibleModelProvider.DefaultMaxTokens}", handler.LastBody);
        Assert.True(OpenAiCompatibleModelProvider.DefaultMaxTokens >= 4096);
    }

    // ── THE REASONING-MODEL TRAP ──

    [Fact]
    public async Task AnEmptyCompletionTruncatedByReasoning_IsANamedFailure_NotASilentEmptySuccess()
    {
        // Observed verbatim against the live endpoint with max_tokens=20: all 20 completion tokens went to
        // reasoning, content came back "", finish_reason "length".
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, """
            {"choices":[{"finish_reason":"length","index":0,
                         "message":{"role":"assistant","content":"","reasoning_content":"The user is asking me to"}}],
             "usage":{"prompt_tokens":17,"completion_tokens":20,
                      "completion_tokens_details":{"reasoning_tokens":20,"text_tokens":0}}}
            """);

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request(maxTokens: 20));

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_EMPTY_COMPLETION", result.Error);
        Assert.Contains("20", result.Error);          // names the budget that was too small
        Assert.Contains("reasoning", result.Error);   // and why it went
    }

    [Fact]
    public async Task AnEmptyCompletionForAnyOtherReason_AlsoFailsByName()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"choices":[{"finish_reason":"content_filter","message":{"content":""}}],"usage":{}}""");

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_EMPTY_COMPLETION", result.Error);
        Assert.Contains("content_filter", result.Error);
    }

    // ── failure modes, all named, none thrown ──

    [Fact]
    public async Task AnAuthFailure_IsANamedUnauthorizedError()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.Unauthorized,
            """{"error":{"message":"Invalid API key"}}""");

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_UNAUTHORIZED", result.Error);
        Assert.Contains("Invalid API key", result.Error);   // the server's own detail survives
    }

    [Fact]
    public async Task AnUnknownModelAlias_SurfacesTheServersExplanation()
    {
        // The real failure hit while wiring this: the alias given was "Coder120b", which the proxy rejects.
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.BadRequest,
            """{"error":{"message":"Invalid model name passed in model=Coder120b"}}""");

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_BAD_REQUEST", result.Error);
        Assert.Contains("Coder120b", result.Error);
        Assert.Contains("model alias", result.Error);
    }

    [Fact]
    public async Task A404_HintsAtTheMissingV1Suffix()
    {
        // The most likely base-URL mistake for an OpenAI-compatible proxy.
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.NotFound, "");

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.Contains("REMOTE_NOT_FOUND", result.Error);
        Assert.Contains("/v1", result.Error);
    }

    [Fact]
    public async Task ATimeout_FailsCleanlyWithinItsBound_RatherThanHanging()
    {
        // The Neo4j lesson applied to inference: an unresponsive remote must cost a KNOWN amount and fail
        // by name. TimeoutMinutes 1 is the floor the provider enforces.
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, SuccessBody, delay: TimeSpan.FromSeconds(30));

        var started = DateTime.UtcNow;
        var result = await Provider(handler).CompleteAsync(
            Binding, Config(timeoutMinutes: 1), Request() with { Timeout = TimeSpan.FromSeconds(1) });
        var elapsed = DateTime.UtcNow - started;

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_TIMEOUT", result.Error);
        Assert.True(elapsed < TimeSpan.FromSeconds(15), $"took {elapsed.TotalSeconds:0.#}s — the bound was not enforced.");
    }

    [Fact]
    public async Task AnUnreachableEndpoint_FailsCleanly()
    {
        using var _ = new KeyScope();
        var provider = new OpenAiCompatibleModelProvider(
            new HttpClient(new ThrowingHandler()), NullLogger<OpenAiCompatibleModelProvider>.Instance);

        var result = await provider.CompleteAsync(Binding, Config(), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_UNREACHABLE", result.Error);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("No such host is known.");
    }

    [Fact]
    public async Task GarbageJson_FailsByName()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, "not json at all");

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_BAD_RESPONSE", result.Error);
    }

    [Fact]
    public async Task AResponseWithNoChoices_FailsByName()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, """{"usage":{"prompt_tokens":1}}""");

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_BAD_RESPONSE", result.Error);
    }

    // ── the key itself ──

    [Fact]
    public async Task AMissingKey_FailsByName_WithoutCallingTheEndpoint()
    {
        using var _ = new KeyScope(value: null);
        var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);

        var result = await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_KEY_MISSING", result.Error);
        Assert.Contains(KeyEnv, result.Error);     // names the var to set
        Assert.Null(handler.LastUri);              // never dialled
    }

    [Fact]
    public async Task TheKeyIsReadFromTheEnvironment_NeverFromTheProfile()
    {
        // The security property: a profile is committed, so it names the var and never holds the secret.
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, SuccessBody);

        await Provider(handler).CompleteAsync(Binding, Config(), Request());

        Assert.Equal("Bearer sk-test-not-a-real-key", handler.LastAuthorization);
        Assert.DoesNotContain("sk-", System.Text.Json.JsonSerializer.Serialize(Config()));
    }

    // ── embeddings ──

    [Fact]
    public async Task AnEmbedding_ParsesTheVectorAndPromptTokens()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK,
            """{"data":[{"embedding":[0.1,0.2,0.3],"index":0}],"usage":{"prompt_tokens":5}}""");

        var result = await Provider(handler).EmbedAsync(
            new ModelClassBinding { Provider = "litellm", Model = "nomic-embed-text" },
            Config(), new EmbeddingRequest("hello"));

        Assert.True(result.Succeeded, result.Error);
        Assert.Equal(3, result.Vector.Count);
        Assert.Equal(0.2f, result.Vector[1], 3);
        Assert.Equal(5, result.TokensIn);
        Assert.Equal("https://proxy.example.test/v1/embeddings", handler.LastUri);
    }

    [Fact]
    public async Task AnEmbeddingWithNoVector_FailsByName()
    {
        using var _ = new KeyScope();
        var handler = new StubHandler(HttpStatusCode.OK, """{"data":[],"usage":{}}""");

        var result = await Provider(handler).EmbedAsync(
            new ModelClassBinding { Provider = "litellm", Model = "nomic-embed-text" },
            Config(), new EmbeddingRequest("hello"));

        Assert.False(result.Succeeded);
        Assert.Contains("REMOTE_EMPTY_EMBEDDING", result.Error);
    }

    // ── URL joining ──

    [Theory]
    [InlineData("https://proxy.example.test/v1", "https://proxy.example.test/v1/chat/completions")]
    [InlineData("https://proxy.example.test/v1/", "https://proxy.example.test/v1/chat/completions")]
    public void TheBaseUrlIsJoinedWithoutDoubledSlashes(string baseUrl, string expected)
    {
        Assert.Equal(expected, OpenAiCompatibleModelProvider.Url(
            new ModelProviderConfig { BaseUrl = baseUrl }, "/chat/completions"));
    }
}
