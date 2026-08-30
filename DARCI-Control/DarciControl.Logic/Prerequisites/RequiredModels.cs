#nullable enable

using Darci.Nodes;

namespace DarciControl.Logic.Prerequisites;

/// <summary>
/// Which Ollama models this host actually needs, derived from <c>host-profile.json</c>.
///
/// <para>THE POINT IS THAT NOTHING KEEPS A SECOND LIST. The profile already decides what every model class
/// resolves to, and the broker fails at startup by name when a class cannot be satisfied. Every other
/// place that named models was a copy, and copies drift: <c>gemma4:e4b</c> is not a real tag and was never
/// installed, yet it outlived that fact in six files — including the preflight check that was supposed to
/// catch exactly this, and the tracked <c>.env.local.example</c> people are told to copy.</para>
///
/// <para>This is the C# twin of <c>Get-DarciRequiredModels.ps1</c>. Both read the same file so a packaged
/// zip can never ship a prerequisite list that disagrees with the profile sitting beside it.</para>
/// </summary>
public static class RequiredModels
{
    /// <summary>
    /// The core's own env-compat defaults, used when no profile is present. Kept honest on purpose: a
    /// WRONG list is worse than no list, because it reports confident nonsense.
    /// </summary>
    public static IReadOnlyList<string> EnvCompatDefaults { get; } =
        new[] { "gemma2:9b", "nomic-embed-text", "qwen2.5-coder:7b" };

    public const string OllamaProviderKind = "ollama";

    /// <summary>Distinct Ollama models bound by <paramref name="profile"/>, in first-seen order.</summary>
    public static IReadOnlyList<string> From(HostProfile profile)
    {
        var models = new List<string>();

        foreach (var (_, binding) in profile.Classes)
        {
            // Only Ollama classes are a local pull concern — a hosted provider needs an API key, not a
            // model file, and telling someone to `ollama pull` a Claude model would be nonsense.
            var provider = profile.Provider(binding.Provider);
            if (provider is not null &&
                !string.Equals(provider.Kind, OllamaProviderKind, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrWhiteSpace(binding.Model) &&
                !models.Contains(binding.Model, StringComparer.OrdinalIgnoreCase))
                models.Add(binding.Model);
        }

        return models.Count > 0 ? models : EnvCompatDefaults;
    }

    /// <summary>
    /// Load the profile at <paramref name="profilePath"/> and derive from it, falling back to
    /// <see cref="EnvCompatDefaults"/> when it is missing or unreadable — the same degradation the core
    /// performs, so the control centre never reports requirements the core would not apply.
    /// </summary>
    public static IReadOnlyList<string> FromFileOrDefaults(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath) || !File.Exists(profilePath))
            return EnvCompatDefaults;

        try
        {
            return From(HostProfileLoader.LoadFile(profilePath));
        }
        catch (Exception)
        {
            return EnvCompatDefaults;
        }
    }

    /// <summary>
    /// Whether <paramref name="required"/> is satisfied by what Ollama reports.
    ///
    /// <para>Tag matching, not equality: Ollama reports <c>nomic-embed-text:latest</c> for a model pulled
    /// as <c>nomic-embed-text</c>, so a naive comparison declares a present model missing and sends the
    /// user off to re-pull something they already have.</para>
    /// </summary>
    public static bool IsSatisfiedBy(string required, IEnumerable<string> installed)
    {
        foreach (var candidate in installed)
        {
            if (string.Equals(candidate, required, StringComparison.OrdinalIgnoreCase)) return true;

            if (!required.Contains(':') &&
                candidate.StartsWith(required + ":", StringComparison.OrdinalIgnoreCase)) return true;
        }

        return false;
    }
}
