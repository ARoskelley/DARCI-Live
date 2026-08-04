using System.Net;
using System.Text;
using System.Text.Json;
using Darci.Nodes;
using Microsoft.Extensions.Logging.Abstractions;

namespace Darci.Nodes.Tests.Contract;

/// <summary>P2a.2 — the broker resolves class→model, invokes the provider, and reports what actually ran.</summary>
public class ModelBrokerTests
{
    /// <summary>Captures the outbound request and returns a canned Ollama response.</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly string _json;
        private readonly HttpStatusCode _status;
        public string? LastUrl;
        public string? LastBody;
        public int Calls;

        public StubHandler(string json, HttpStatusCode status = HttpStatusCode.OK)
        {
            _json = json;
            _status = status;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastUrl = request.RequestUri?.ToString();
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(_status) { Content = new StringContent(_json, Encoding.UTF8, "application/json") };
        }
    }

    private const string GenerateJson = """
        {"model":"gemma2:9b","response":"  hello world  ","done":true,"prompt_eval_count":12,"eval_count":34}
        """;

    private const string EmbedJson = """
        {"embeddings":[[0.5,-0.25,0.75]],"prompt_eval_count":7}
        """;

    private static HostProfile Profile() => new()
    {
        ProfileId = "test-profile",
        Providers = new Dictionary<string, ModelProviderConfig>
        {
            ["ollama"] = new() { Kind = "ollama", BaseUrl = "http://localhost:11434", TimeoutMinutes = 12 },
        },
        Classes = new Dictionary<string, ModelClassBinding>
        {
            [ModelClasses.ChatFast] = new() { Provider = "ollama", Model = "fast-model" },
            [ModelClasses.ChatBalanced] = new() { Provider = "ollama", Model = "gemma2:9b" },
            [ModelClasses.ChatDeep] = new() { Provider = "ollama", Model = "deep-model" },
            [ModelClasses.ClassifyIntent] = new() { Provider = "ollama", Model = "gemma2:9b" },
            [ModelClasses.EmbedText] = new() { Provider = "ollama", Model = "nomic-embed-text" },
            [ModelClasses.CodeGenerate] = new() { Provider = "ollama", Model = "qwen2.5-coder:7b" },
            [ModelClasses.CodeFast] = new() { Provider = "ollama", Model = "qwen-fast" },
        },
    };

    private static (ModelBroker Broker, StubHandler Handler) Broker(string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        var handler = new StubHandler(json, status);
        var provider = new OllamaModelProvider(new HttpClient(handler), NullLogger<OllamaModelProvider>.Instance);
        var broker = new ModelBroker(Profile(), new IModelProvider[] { provider }, NullLogger<ModelBroker>.Instance);
        return (broker, handler);
    }

    // ── resolution ──

    [Fact]
    public void ResolveModelName_MapsClassToTheProfilesModel()
    {
        var (broker, _) = Broker(GenerateJson);
        Assert.Equal("gemma2:9b", broker.ResolveModelName(ModelClasses.ChatBalanced));
        Assert.Equal("qwen-fast", broker.ResolveModelName(ModelClasses.CodeFast));
        Assert.Null(broker.ResolveModelName("chat.nonexistent"));
        Assert.Equal("test-profile", broker.Profile.ProfileId);
    }

    [Fact]
    public async Task Complete_SendsTheResolvedModel_AndReportsWhatRan()
    {
        var (broker, handler) = Broker(GenerateJson);

        var result = await broker.CompleteAsync(new ModelRequest(ModelClasses.CodeGenerate, "write a parser"));

        Assert.True(result.Succeeded);
        Assert.Equal("hello world", result.Text);                       // trimmed
        Assert.Equal(ModelClasses.CodeGenerate, result.ModelClass);
        Assert.Equal("qwen2.5-coder:7b", result.ResolvedModel);         // ← the class was resolved
        Assert.Equal("ollama", result.ProviderKind);
        Assert.Equal(12, result.TokensIn);                              // ← captured for telemetry
        Assert.Equal(34, result.TokensOut);
        Assert.Contains("\"model\":\"qwen2.5-coder:7b\"", handler.LastBody);
        Assert.Equal("http://localhost:11434/api/generate", handler.LastUrl);
    }

    [Fact]
    public async Task Complete_PassesPerRequestSamplingOptions()
    {
        // These differ per caller today (coding 0.4/4096 vs general 0.7/1024); the broker must not flatten them.
        var (broker, handler) = Broker(GenerateJson);

        await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p") { Temperature = 0.4, MaxTokens = 4096 });
        Assert.Contains("\"temperature\":0.4", handler.LastBody);
        Assert.Contains("\"num_predict\":4096", handler.LastBody);

        await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));
        Assert.Contains("\"temperature\":0.7", handler.LastBody);       // documented defaults
        Assert.Contains("\"num_predict\":1024", handler.LastBody);
    }

    [Fact]
    public async Task Embed_ReturnsTheVector_AndTheResolvedEmbeddingModel()
    {
        var (broker, handler) = Broker(EmbedJson);

        var result = await broker.EmbedAsync(new EmbeddingRequest("some text"));

        Assert.True(result.Succeeded);
        Assert.Equal(new[] { 0.5f, -0.25f, 0.75f }, result.Vector);
        Assert.Equal("nomic-embed-text", result.ResolvedModel);
        Assert.Equal(7, result.TokensIn);
        Assert.Equal("http://localhost:11434/api/embed", handler.LastUrl);
    }

    // ── failure is reported, not thrown (callers degrade gracefully today) ──

    [Fact]
    public async Task Complete_OnTransportFailure_ReportsFailureRatherThanThrowing()
    {
        var (broker, _) = Broker("{}", HttpStatusCode.NotFound);

        var result = await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));

        Assert.False(result.Succeeded);
        Assert.NotNull(result.Error);
        Assert.Equal("gemma2:9b", result.ResolvedModel);   // still says what it TRIED — the 404 diagnostic
        Assert.Equal("", result.Text);
    }

    [Fact]
    public async Task Embed_OnFailure_ReportsFailureWithAnEmptyVector()
    {
        var (broker, _) = Broker("{}", HttpStatusCode.InternalServerError);
        var result = await broker.EmbedAsync(new EmbeddingRequest("x"));
        Assert.False(result.Succeeded);
        Assert.Empty(result.Vector);
    }

    [Fact]
    public async Task UnknownClass_ReportsFailure_WithoutCallingTheProvider()
    {
        var (broker, handler) = Broker(GenerateJson);
        var result = await broker.CompleteAsync(new ModelRequest("chat.nonexistent", "p"));

        Assert.False(result.Succeeded);
        Assert.Contains("not bound", result.Error);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task CallerCancellation_Propagates_RatherThanBecomingASoftFailure()
    {
        var (broker, _) = Broker(GenerateJson);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"), cts.Token));
    }

    // ── startup validation: an unusable profile must fail at construction, not mid-task ──

    [Fact]
    public void Constructing_WithAProviderKindThisHostCannotServe_ThrowsNamed()
    {
        var profile = Profile() with
        {
            Providers = new Dictionary<string, ModelProviderConfig>
            {
                ["ollama"] = new() { Kind = "anthropic", BaseUrl = "https://api.anthropic.com" },
            },
        };

        var ex = Assert.Throws<HostProfileException>(() => new ModelBroker(
            profile,
            new IModelProvider[] { new OllamaModelProvider(new HttpClient(), NullLogger<OllamaModelProvider>.Instance) },
            NullLogger<ModelBroker>.Instance));

        Assert.Contains("anthropic", ex.Message);
        Assert.Contains("no implementation", ex.Message);
    }

    [Fact]
    public void Constructing_WithAnInvalidProfile_ThrowsNamed()
    {
        var incomplete = Profile() with
        {
            Classes = new Dictionary<string, ModelClassBinding>
            {
                [ModelClasses.ChatBalanced] = new() { Provider = "ollama", Model = "gemma2:9b" },
            },
        };

        var ex = Assert.Throws<HostProfileException>(() => new ModelBroker(
            incomplete,
            new IModelProvider[] { new OllamaModelProvider(new HttpClient(), NullLogger<OllamaModelProvider>.Instance) },
            NullLogger<ModelBroker>.Instance));

        Assert.Contains("is invalid", ex.Message);
    }

    [Fact]
    public async Task DurationIsRecorded_ForTelemetry()
    {
        var (broker, _) = Broker(GenerateJson);
        var result = await broker.CompleteAsync(new ModelRequest(ModelClasses.ChatBalanced, "p"));
        Assert.True(result.DurationMs >= 0);
        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task EmbeddingClass_IsSeparateFromChat_SoVectorsNeverComeFromAChatModel()
    {
        var (broker, handler) = Broker(EmbedJson);
        await broker.EmbedAsync(new EmbeddingRequest("x"));
        Assert.Contains("nomic-embed-text", handler.LastBody);
        Assert.DoesNotContain("gemma2:9b", handler.LastBody);
    }

    [Fact]
    public void JsonRoundTrip_OfAProfile_PreservesBindings()
    {
        var json = JsonSerializer.Serialize(Profile(), HostProfile.Json);
        var back = JsonSerializer.Deserialize<HostProfile>(json, HostProfile.Json)!;
        Assert.Empty(back.Validate());
        Assert.Equal("qwen-fast", back.Resolve(ModelClasses.CodeFast)!.Model);
    }
}
