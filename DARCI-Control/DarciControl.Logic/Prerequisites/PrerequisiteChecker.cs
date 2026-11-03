#nullable enable

using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace DarciControl.Logic.Prerequisites;

/// <summary>Where everything lives, so the checker works against a repo checkout or an unzipped install.</summary>
public sealed record DarciPaths
{
    /// <summary>Repo root, or the unzipped install root.</summary>
    public required string Root { get; init; }

    public string HostProfilePath => FirstExisting(
        Path.Combine(Root, "DARCI-v4", "host-profile.json"),
        Path.Combine(Root, "host-profile.json"));

    public string NodesPath => FirstExisting(
        Path.Combine(Root, "DARCI-v4", "nodes"),
        Path.Combine(Root, "nodes"));

    public string EnvLocalPath => FirstExisting(
        Path.Combine(Root, "DARCI-v4", "Darci.Api", ".env.local"),
        Path.Combine(Root, ".env.local"));

    private static string FirstExisting(params string[] candidates)
    {
        foreach (var c in candidates)
            if (File.Exists(c) || Directory.Exists(c)) return c;

        return candidates[^1];
    }
}

/// <summary>
/// The preflight behind the control centre's "Start DARCI" button, and behind the packaged zip's startup
/// script. Every check answers three questions: is it there, does it matter, and what do I do about it.
///
/// <para>DELIBERATELY READ-ONLY. Nothing here installs software or pulls a model on its own — those are
/// user decisions with real disk and bandwidth costs, so the checker reports a remedy and the UI offers to
/// run it. Silent installation is how a launcher becomes something people stop trusting.</para>
/// </summary>
public sealed class PrerequisiteChecker
{
    private readonly HttpClient _http;
    private readonly DarciPaths _paths;

    public PrerequisiteChecker(DarciPaths paths, HttpClient? http = null)
    {
        _paths = paths;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public async Task<PrereqReport> CheckAllAsync(
        string ollamaBaseUrl = "http://localhost:11434",
        string coreStatusUrl = "http://localhost:5081/status",
        CancellationToken ct = default)
    {
        var results = new List<PrereqResult> { await CheckDotnetAsync(ct) };

        var ollama = await CheckOllamaAsync(ollamaBaseUrl, ct);
        results.Add(ollama);

        // Only ask about models once Ollama is actually answering: "model missing" is a misleading verdict
        // when the truth is that nothing was there to ask.
        if (ollama.State == PrereqState.Ok)
            results.AddRange(await CheckModelsAsync(ollamaBaseUrl, ct));

        results.Add(await CheckNeo4jAsync(ct));
        results.Add(await CheckCoreAsync(coreStatusUrl, ct));

        return new PrereqReport(results);
    }

    // ── .NET ──

    public async Task<PrereqResult> CheckDotnetAsync(CancellationToken ct = default)
    {
        try
        {
            var (exitCode, output) = await RunAsync("dotnet", "--version", ct);
            if (exitCode != 0 || string.IsNullOrWhiteSpace(output))
                return PrereqResult.Failed(".NET SDK", "dotnet did not respond",
                    "Install .NET 8 from https://dotnet.microsoft.com/download");

            var version = output.Trim();
            return version.StartsWith("8.", StringComparison.Ordinal)
                ? PrereqResult.Ok(".NET SDK", version)
                : PrereqResult.Warning(".NET SDK", $"found {version}; .NET 8 is the tested target");
        }
        catch (Exception ex)
        {
            return PrereqResult.Failed(".NET SDK", ex.GetBaseException().Message,
                "Install .NET 8 from https://dotnet.microsoft.com/download");
        }
    }

    // ── Ollama ──

    public async Task<PrereqResult> CheckOllamaAsync(string baseUrl, CancellationToken ct = default)
    {
        try
        {
            var tags = await _http.GetFromJsonAsync<OllamaTags>($"{baseUrl.TrimEnd('/')}/api/tags", ct);
            return tags is null
                ? PrereqResult.Warning("Ollama", "responded but returned nothing readable", "Restart with: ollama serve")
                : PrereqResult.Ok("Ollama", $"{baseUrl} ({tags.Models?.Count ?? 0} model(s))");
        }
        catch (Exception ex)
        {
            // A WARNING, not a failure: the core starts without Ollama, it just cannot think.
            return PrereqResult.Warning("Ollama",
                $"not reachable at {baseUrl}: {ex.GetBaseException().Message}",
                "Install from https://ollama.com, then run: ollama serve");
        }
    }

    /// <summary>Models the host profile binds — never a hardcoded list. See <see cref="RequiredModels"/>.</summary>
    public async Task<IReadOnlyList<PrereqResult>> CheckModelsAsync(string baseUrl, CancellationToken ct = default)
    {
        var required = RequiredModels.FromFileOrDefaults(_paths.HostProfilePath);

        List<string> installed;
        try
        {
            var tags = await _http.GetFromJsonAsync<OllamaTags>($"{baseUrl.TrimEnd('/')}/api/tags", ct);
            installed = tags?.Models?.Select(m => m.Name).Where(n => !string.IsNullOrWhiteSpace(n)).ToList() ?? new();
        }
        catch (Exception ex)
        {
            return new[]
            {
                PrereqResult.Warning("Ollama models", $"could not be listed: {ex.GetBaseException().Message}"),
            };
        }

        return required
            .Select(model => RequiredModels.IsSatisfiedBy(model, installed)
                ? PrereqResult.Ok($"Model {model}", "available")
                : PrereqResult.Warning($"Model {model}", "not pulled", $"ollama pull {model}"))
            .ToList();
    }

    // ── Neo4j (optional by design) ──

    public async Task<PrereqResult> CheckNeo4jAsync(CancellationToken ct = default)
    {
        // Configuration decides whether Neo4j is wanted at all; the core stays on SQLite without it.
        var configured = File.Exists(_paths.EnvLocalPath) &&
            File.ReadLines(_paths.EnvLocalPath).Any(l =>
                l.TrimStart().StartsWith("DARCI_NEO4J_PASSWORD=", StringComparison.OrdinalIgnoreCase) &&
                l.Split('=', 2)[1].Trim().Length > 0);

        if (!configured)
            return PrereqResult.Ok("Knowledge graph", "SQLite (Neo4j not configured — this is a valid setup)");

        // ONE probe, shared with Neo4jController. Keeping a second copy here is how the same screen came
        // to report "Neo4j is not running" and "Neo4j is listening" simultaneously: the two had different
        // timeouts, and the shorter one gave up during IPv6 resolution.
        return await Runtime.Neo4jController.IsListeningAsync(ct)
            ? PrereqResult.Ok("Knowledge graph", "Neo4j is listening on bolt://localhost:7687")
            : PrereqResult.Warning("Knowledge graph",
                "Neo4j is configured but not running — the core will fall back to SQLite",
                "Start Neo4j, or leave it stopped to run on SQLite");
    }

    // ── the core itself ──

    public async Task<PrereqResult> CheckCoreAsync(string statusUrl, CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync(statusUrl, ct);
            return response.IsSuccessStatusCode
                ? PrereqResult.Ok("DARCI core", "already running")
                : PrereqResult.Warning("DARCI core", $"responded HTTP {(int)response.StatusCode}");
        }
        catch (Exception)
        {
            return PrereqResult.Warning("DARCI core", "not running", "Start it from here");
        }
    }

    /// <summary>Poll until the core answers — the readiness probe, ASSERTED rather than inferred from logs.</summary>
    public async Task<bool> WaitForCoreAsync(string statusUrl, TimeSpan timeout, CancellationToken ct = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var response = await _http.GetAsync(statusUrl, ct);
                if (response.IsSuccessStatusCode) return true;
            }
            catch (Exception) { /* not up yet */ }

            await Task.Delay(TimeSpan.FromSeconds(1), ct);
        }

        return false;
    }

    private static async Task<(int ExitCode, string Output)> RunAsync(string file, string args, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        var stdout = await process.StandardOutput.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        return (process.ExitCode, stdout);
    }

    private sealed record OllamaTags([property: JsonPropertyName("models")] List<OllamaTag>? Models);
    private sealed record OllamaTag([property: JsonPropertyName("name")] string Name);
}
