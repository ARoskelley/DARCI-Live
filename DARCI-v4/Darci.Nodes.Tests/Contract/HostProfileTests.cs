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
    public void TheRepoDefaultProfile_IsValid_AndRoutesTheHeavyTiersRemote_KeepingTheRestLocal()
    {
        // *** RE-BLESSED, NOT A REGRESSION ***
        // This asserted the default profile was ALL-LOCAL. The default is now deliberately HYBRID: the two
        // heavy tiers (code.generate, chat.deep) run on a remote OpenAI-compatible proxy, and everything
        // latency-sensitive or high-volume stays on Ollama. The all-local arrangement this test used to
        // pin now lives in host-profile.local-only.example.json and is asserted separately below, so the
        // keyless path is still covered rather than lost.
        var path = FindRepoFile(HostProfileLoader.FileName);
        Assert.NotNull(path);

        // The hybrid profile legitimately REFUSES to validate without its key — that is the §6.2 fail-fast
        // doing its job, and there is a separate test for it below. What this test is about is the routing
        // SHAPE, which is a property of the committed file rather than of whoever happens to be running it,
        // so a placeholder key stands in for the host's.
        const string keyEnv = "DARCI_LITELLM_API_KEY";
        var previous = Environment.GetEnvironmentVariable(keyEnv);
        if (string.IsNullOrWhiteSpace(previous)) Environment.SetEnvironmentVariable(keyEnv, "sk-placeholder-for-tests");

        try
        {
            var profile = HostProfileLoader.LoadFile(path!);
            Assert.Empty(profile.Validate());
            AssertHybridRouting(profile);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyEnv, previous);
        }
    }

    [Fact]
    public void TheRepoDefaultProfile_RefusesToLoadWithoutItsRemoteKey_ByName()
    {
        // The flip side, and the reason the local-only example exists: the committed default needs a
        // secret, so a host without one must be told exactly which variable is missing rather than
        // discovering it mid-task.
        const string keyEnv = "DARCI_LITELLM_API_KEY";
        var previous = Environment.GetEnvironmentVariable(keyEnv);
        Environment.SetEnvironmentVariable(keyEnv, null);

        try
        {
            var path = FindRepoFile(HostProfileLoader.FileName);
            var ex = Assert.Throws<HostProfileException>(() => HostProfileLoader.LoadFile(path!));

            Assert.Contains(keyEnv, ex.Message);
            Assert.Contains("not set", ex.Message);
        }
        finally
        {
            Environment.SetEnvironmentVariable(keyEnv, previous);
        }
    }

    private static void AssertHybridRouting(HostProfile profile)
    {
        // The heavy tiers are remote, and share one model.
        foreach (var heavy in new[] { ModelClasses.CodeGenerate, ModelClasses.ChatDeep })
        {
            var binding = profile.Resolve(heavy)!;
            Assert.Equal("openai", profile.Provider(binding.Provider)!.Kind);
            Assert.Equal("coder-120b", binding.Model);
        }

        // Everything else stays local, unchanged. code.fast staying local is the point: it preserves a
        // working local coder for when the remote is unavailable.
        Assert.Equal("gemma2:9b", profile.Resolve(ModelClasses.ChatBalanced)!.Model);
        Assert.Equal("gemma2:9b", profile.Resolve(ModelClasses.ChatFast)!.Model);
        Assert.Equal("gemma2:9b", profile.Resolve(ModelClasses.ClassifyIntent)!.Model);
        Assert.Equal("nomic-embed-text", profile.Resolve(ModelClasses.EmbedText)!.Model);
        Assert.Equal("qwen2.5-coder:7b", profile.Resolve(ModelClasses.CodeFast)!.Model);
        foreach (var local in new[]
                 {
                     ModelClasses.ChatBalanced, ModelClasses.ChatFast, ModelClasses.ClassifyIntent,
                     ModelClasses.EmbedText, ModelClasses.CodeFast,
                 })
            Assert.Equal("ollama", profile.Provider(profile.Resolve(local)!.Provider)!.Kind);

        // A remote provider NAMES its key env var and never carries the secret — profiles are committed.
        var remote = profile.Provider(profile.Resolve(ModelClasses.CodeGenerate)!.Provider)!;
        Assert.False(string.IsNullOrWhiteSpace(remote.ApiKeyEnv));
        Assert.DoesNotContain("sk-", System.Text.Json.JsonSerializer.Serialize(profile));

        // The OpenAI root must be explicit: /chat/completions is appended to this base.
        Assert.EndsWith("/v1", remote.BaseUrl);

        // A tier that is still pretending must SAY so. chat.deep no longer pretends (it is genuinely
        // deeper now), but chat.fast still maps to the 9B, so its note must survive.
        Assert.False(string.IsNullOrWhiteSpace(profile.Resolve(ModelClasses.ChatFast)!.Note));
    }

    [Fact]
    public void TheLocalOnlyExampleProfile_IsValid_AndNeedsNoApiKey()
    {
        // The escape hatch the hybrid default requires: a host with no remote key must still be able to
        // run every class. Copy this over host-profile.json and nothing needs a secret.
        var path = FindRepoFile("host-profile.local-only.example.json");
        Assert.NotNull(path);

        var profile = HostProfileLoader.LoadFile(path!);
        Assert.Empty(profile.Validate());

        Assert.All(profile.Providers.Values, p => Assert.True(string.IsNullOrEmpty(p.ApiKeyEnv)));
        Assert.All(profile.Classes.Values, b => Assert.Equal("ollama", profile.Provider(b.Provider)!.Kind));

        // Every class binds, so the broker's startup check passes without a remote implementation.
        foreach (var c in ModelClasses.All) Assert.NotNull(profile.Resolve(c));
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
