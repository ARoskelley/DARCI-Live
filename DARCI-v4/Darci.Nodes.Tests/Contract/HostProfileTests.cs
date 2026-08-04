using Darci.Nodes;

namespace Darci.Nodes.Tests.Contract;

/// <summary>
/// P2a.1 — the host profile is the ONE file a collaborator edits to run on different hardware, so its
/// validation has to be strict and its failures named. The motivating incident: a <c>gemma4:e4b</c> typo
/// (a model that does not exist) failed silently at runtime with a 404 on every generation instead of
/// loudly at startup.
/// </summary>
public sealed class HostProfileTests : IDisposable
{
    private readonly string _dir;

    public HostProfileTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"darci-profile-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Write(string json, string name = HostProfileLoader.FileName)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, json);
        return path;
    }

    private static HostProfile Valid() => new()
    {
        ProfileId = "test",
        Providers = new Dictionary<string, ModelProviderConfig>
        {
            ["ollama"] = new() { Kind = "ollama", BaseUrl = "http://localhost:11434", TimeoutMinutes = 12 },
        },
        Classes = ModelClasses.All.ToDictionary(
            c => c,
            _ => new ModelClassBinding { Provider = "ollama", Model = "gemma2:9b" }),
    };

    // ── class set ──

    [Fact]
    public void SevenClasses_IncludingTheAddedCodeFast()
    {
        Assert.Equal(7, ModelClasses.All.Length);
        Assert.Contains(ModelClasses.CodeFast, ModelClasses.All);   // fork F2: preserves the fast/full distinction
        Assert.All(ModelClasses.All, c => Assert.True(ModelClasses.IsKnown(c)));
        Assert.False(ModelClasses.IsKnown("chat.enormous"));
        Assert.True(ModelClasses.IsEmbedding(ModelClasses.EmbedText));
        Assert.False(ModelClasses.IsEmbedding(ModelClasses.ChatBalanced));
    }

    // ── validation ──

    [Fact]
    public void AProfileBindingEveryClass_IsValid() => Assert.Empty(Valid().Validate());

    [Fact]
    public void AnUnboundClass_IsANamedError()
    {
        var missing = Valid() with
        {
            Classes = Valid().Classes.Where(kv => kv.Key != ModelClasses.CodeFast)
                .ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        Assert.Contains(missing.Validate(), e => e.Contains(ModelClasses.CodeFast) && e.Contains("not bound"));
    }

    [Fact]
    public void AClassNamingAnUndeclaredProvider_IsANamedError()
    {
        var bad = Valid() with
        {
            Classes = Valid().Classes.ToDictionary(
                kv => kv.Key,
                kv => kv.Key == ModelClasses.ChatDeep
                    ? new ModelClassBinding { Provider = "anthropic", Model = "claude-sonnet-5" }
                    : kv.Value),
        };
        Assert.Contains(bad.Validate(), e => e.Contains("anthropic") && e.Contains("not declared"));
    }

    [Fact]
    public void AProviderRequiringAMissingEnvKey_IsANamedError()
    {
        // This is what stops a Claude profile from being adopted without its key — at STARTUP.
        var bad = Valid() with
        {
            Providers = new Dictionary<string, ModelProviderConfig>
            {
                ["ollama"] = new() { Kind = "ollama", BaseUrl = "http://localhost:11434" },
                ["anthropic"] = new() { Kind = "anthropic", ApiKeyEnv = "DEFINITELY_NOT_SET_ANTHROPIC_KEY_XYZ" },
            },
        };
        Assert.Contains(bad.Validate(), e => e.Contains("DEFINITELY_NOT_SET_ANTHROPIC_KEY_XYZ") && e.Contains("not set"));
    }

    [Fact]
    public void AnUnknownClassName_IsRejected()
    {
        var bad = Valid() with
        {
            Classes = Valid().Classes.Concat(new[]
            {
                new KeyValuePair<string, ModelClassBinding>("chat.enormous", new() { Provider = "ollama", Model = "x" }),
            }).ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        Assert.Contains(bad.Validate(), e => e.Contains("unknown class 'chat.enormous'"));
    }

    [Fact]
    public void AnOllamaProviderWithoutABaseUrl_IsRejected()
    {
        var bad = Valid() with
        {
            Providers = new Dictionary<string, ModelProviderConfig> { ["ollama"] = new() { Kind = "ollama" } },
        };
        Assert.Contains(bad.Validate(), e => e.Contains("needs a base_url"));
    }

    // ── loading ──

    [Fact]
    public void MalformedJson_ThrowsNamed()
    {
        var path = Write("{ not json ");
        var ex = Assert.Throws<HostProfileException>(() => HostProfileLoader.LoadFile(path));
        Assert.Contains("not valid JSON", ex.Message);
    }

    [Fact]
    public void AnInvalidProfile_ThrowsAtLoad_NotAtFirstUse()
    {
        var path = Write("""
            { "profile_id": "half", "providers": { "ollama": { "kind": "ollama", "base_url": "http://localhost:11434" } },
              "classes": { "chat.balanced": { "provider": "ollama", "model": "gemma2:9b" } } }
            """);
        var ex = Assert.Throws<HostProfileException>(() => HostProfileLoader.LoadFile(path));
        Assert.Contains("is invalid", ex.Message);
        Assert.Contains("not bound", ex.Message);   // names every missing class
    }

    [Fact]
    public void MissingFile_FallsBackToTheEnvironmentCompatProfile()
    {
        var (profile, fromFile) = HostProfileLoader.LoadOrDefault(Path.Combine(_dir, "nope.json"));
        Assert.False(fromFile);
        Assert.Equal("env-compat", profile.ProfileId);
        Assert.Empty(profile.Validate());
    }

    [Fact]
    public void TheEnvironmentCompatProfile_BindsEveryClass_SoAnInstallWithNoFileStillWorks()
    {
        var profile = HostProfileLoader.FromEnvironment();
        Assert.Empty(profile.Validate());
        Assert.All(ModelClasses.All, c => Assert.NotNull(profile.Resolve(c)));

        // Embeddings must NOT silently resolve to a chat model — that would produce garbage vectors.
        Assert.NotEqual(
            profile.Resolve(ModelClasses.ChatBalanced)!.Model,
            profile.Resolve(ModelClasses.EmbedText)!.Model);
    }

    // ── the profiles actually committed to the repo ──

    [Fact]
    public void TheRepoDefaultProfile_IsValid_AndAllLocal_AndReproducesTodaysRouting()
    {
        var path = FindRepoFile(HostProfileLoader.FileName);
        Assert.NotNull(path);

        var profile = HostProfileLoader.LoadFile(path!);
        Assert.Empty(profile.Validate());

        // All-local: no provider needs an API key (there is no Claude key on this host).
        Assert.All(profile.Providers.Values, p => Assert.True(string.IsNullOrEmpty(p.ApiKeyEnv)));
        Assert.All(profile.Classes.Values, b => Assert.Equal("ollama", profile.Provider(b.Provider)!.Kind));

        // Reproduces today's effective routing exactly — this is what makes the broker behavior-preserving.
        Assert.Equal("gemma2:9b", profile.Resolve(ModelClasses.ChatBalanced)!.Model);
        Assert.Equal("gemma2:9b", profile.Resolve(ModelClasses.ChatDeep)!.Model);
        Assert.Equal("nomic-embed-text", profile.Resolve(ModelClasses.EmbedText)!.Model);
        Assert.Equal("qwen2.5-coder:7b", profile.Resolve(ModelClasses.CodeGenerate)!.Model);
        Assert.Equal("qwen2.5-coder:7b", profile.Resolve(ModelClasses.CodeFast)!.Model);

        // The tiers that are pretending must SAY so, rather than implying a capability the host lacks.
        Assert.False(string.IsNullOrWhiteSpace(profile.Resolve(ModelClasses.ChatDeep)!.Note));
        Assert.False(string.IsNullOrWhiteSpace(profile.Resolve(ModelClasses.ChatFast)!.Note));
    }

    [Fact]
    public void TheClaudeExampleProfile_IsWellFormed_ButIsNotTheActiveProfile()
    {
        var path = FindRepoFile("host-profile.claude.example.json");
        Assert.NotNull(path);

        // It parses and binds every class...
        var raw = File.ReadAllText(path!);
        var profile = System.Text.Json.JsonSerializer.Deserialize<HostProfile>(raw, HostProfile.Json)!;
        Assert.All(ModelClasses.All, c => Assert.NotNull(profile.Resolve(c)));

        // ...but it is deliberately NOT loadable here: no ANTHROPIC_API_KEY on this host, and validation
        // catches that at load time rather than at the first chat.deep call.
        Assert.Contains(profile.Validate(), e => e.Contains("ANTHROPIC_API_KEY"));
        Assert.Contains("EXAMPLE", profile.ProfileId);
    }

    private static string? FindRepoFile(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, fileName);
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
