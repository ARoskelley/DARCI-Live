#nullable enable

using Darci.Nodes;
using DarciControl.Logic.Prerequisites;

namespace DarciControl.Logic.Tests;

/// <summary>
/// The rule that stops the model list drifting again: required models are DERIVED from host-profile.json,
/// never held as a second list. `gemma4:e4b` outlived the tag itself in six files, including the preflight
/// check meant to catch exactly that.
/// </summary>
public sealed class RequiredModelsTests
{
    private static HostProfile Profile(params (string Class, string Provider, string Model)[] classes) => new()
    {
        ProfileId = "test",
        Providers = new Dictionary<string, ModelProviderConfig>
        {
            ["ollama"] = new() { Kind = "ollama", BaseUrl = "http://localhost:11434" },
            ["anthropic"] = new() { Kind = "anthropic", ApiKeyEnv = "ANTHROPIC_API_KEY" },
        },
        Classes = classes.ToDictionary(
            c => c.Class,
            c => new ModelClassBinding { Provider = c.Provider, Model = c.Model }),
    };

    [Fact]
    public void Derives_TheDistinctOllamaModels_InFirstSeenOrder()
    {
        var models = RequiredModels.From(Profile(
            ("chat.fast", "ollama", "gemma2:9b"),
            ("chat.balanced", "ollama", "gemma2:9b"),
            ("embed.text", "ollama", "nomic-embed-text"),
            ("code.generate", "ollama", "qwen2.5-coder:7b")));

        // Deduped: four classes, three actual pulls.
        Assert.Equal(new[] { "gemma2:9b", "nomic-embed-text", "qwen2.5-coder:7b" }, models);
    }

    [Fact]
    public void Ignores_NonOllamaProviders()
    {
        // A hosted class needs an API key, not a model file. Telling someone to `ollama pull` a Claude
        // model would be nonsense, and would fail forever.
        var models = RequiredModels.From(Profile(
            ("chat.deep", "anthropic", "claude-opus-5"),
            ("embed.text", "ollama", "nomic-embed-text")));

        Assert.Equal(new[] { "nomic-embed-text" }, models);
    }

    [Fact]
    public void FallsBackToEnvCompatDefaults_WhenTheProfileBindsNothing()
    {
        Assert.Equal(RequiredModels.EnvCompatDefaults, RequiredModels.From(Profile()));
    }

    [Fact]
    public void FallsBackToEnvCompatDefaults_WhenTheProfileIsMissing()
    {
        // Matches what the core does with no profile, so the control centre never reports requirements the
        // core itself would not apply.
        Assert.Equal(RequiredModels.EnvCompatDefaults, RequiredModels.FromFileOrDefaults(null));
        Assert.Equal(RequiredModels.EnvCompatDefaults,
            RequiredModels.FromFileOrDefaults(Path.Combine(Path.GetTempPath(), $"nope-{Guid.NewGuid():N}.json")));
    }

    [Fact]
    public void FallsBackToDefaults_RatherThanThrowing_OnAnUnreadableProfile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad-profile-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ not json");
        try
        {
            Assert.Equal(RequiredModels.EnvCompatDefaults, RequiredModels.FromFileOrDefaults(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void TheRealRepoProfile_ResolvesToTheModelsThatAreActuallyInstalled()
    {
        // Pins the D0 reconciliation. If someone reintroduces a phantom tag in host-profile.json, this
        // fails here rather than as a 404 on every generation at runtime.
        var repoRoot = FindRepoRoot();
        var profilePath = Path.Combine(repoRoot, "DARCI-v4", "host-profile.json");
        Assert.True(File.Exists(profilePath), $"expected a host profile at {profilePath}");

        var models = RequiredModels.FromFileOrDefaults(profilePath);

        Assert.Equal(new[] { "gemma2:9b", "nomic-embed-text", "qwen2.5-coder:7b" }, models);
        Assert.DoesNotContain("gemma4:e4b", models);
    }

    // ── tag matching ──

    [Theory]
    [InlineData("nomic-embed-text", "nomic-embed-text:latest")]
    [InlineData("nomic-embed-text", "nomic-embed-text")]
    [InlineData("gemma2:9b", "gemma2:9b")]
    public void TagMatching_TreatsAnImplicitLatestAsSatisfied(string required, string installed)
    {
        // Ollama reports `name:latest` for a model pulled as `name`. Naive equality declares a model that
        // is right there missing, and sends the user off to re-pull it.
        Assert.True(RequiredModels.IsSatisfiedBy(required, new[] { installed }));
    }

    [Theory]
    [InlineData("gemma2:9b", "gemma2:2b")]
    [InlineData("qwen2.5-coder:7b", "qwen2.5-coder:32b")]
    public void TagMatching_DoesNotAcceptADifferentExplicitTag(string required, string installed)
    {
        // An explicit tag is a specific claim about size and behaviour; a different one is not a substitute.
        Assert.False(RequiredModels.IsSatisfiedBy(required, new[] { installed }));
    }

    [Fact]
    public void TagMatching_IsNotSatisfiedByNothing()
    {
        Assert.False(RequiredModels.IsSatisfiedBy("gemma2:9b", Array.Empty<string>()));
    }

    internal static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "DARCI-v4")))
            dir = dir.Parent;

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate the repo root.");
    }
}
