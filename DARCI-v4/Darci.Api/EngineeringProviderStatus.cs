using System.Diagnostics;
using System.Net.Http;
using System.Text.RegularExpressions;

namespace Darci.Api;

public sealed class EngineeringProviderStatus
{
    public string Name { get; init; } = "";
    public string Category { get; init; } = "tool";
    public bool Configured { get; init; }
    public bool Detected { get; init; }
    public string Mode { get; init; } = "disabled";
    public string? Endpoint { get; init; }
    public string? Notes { get; init; }
    public string? Probe { get; init; }
    public List<string> RequiredEnv { get; init; } = new();
}

public static class EngineeringProviderStatusService
{
    public static async Task<IReadOnlyList<EngineeringProviderStatus>> GetStatus(bool probe = false, CancellationToken ct = default)
    {
        var cadCoderUrl = Environment.GetEnvironmentVariable("DARCI_CADCODER_URL");
        var cadCoderKey = Environment.GetEnvironmentVariable("DARCI_CADCODER_API_KEY");

        var kittyBase = Environment.GetEnvironmentVariable("DARCI_KITTYCAD_BASE_URL");
        var kittyPath = Environment.GetEnvironmentVariable("DARCI_KITTYCAD_PATH") ?? "/v1/text-to-cad";
        var kittyKey = Environment.GetEnvironmentVariable("DARCI_KITTYCAD_API_KEY");

        var mujocoEnabled = string.Equals(
            Environment.GetEnvironmentVariable("DARCI_MUJOCO_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);
        var pythonPath = Environment.GetEnvironmentVariable("DARCI_PYTHON_PATH") ?? "python";

        var build123dEnabled = string.Equals(
            Environment.GetEnvironmentVariable("DARCI_BUILD123D_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var calculixPath = Environment.GetEnvironmentVariable("DARCI_CALCULIX_PATH");
        var kicadCli = Environment.GetEnvironmentVariable("DARCI_KICAD_CLI_PATH");
        var kicadMcpUrl = Environment.GetEnvironmentVariable("DARCI_KICAD_MCP_URL");
        var roscribeUrl = Environment.GetEnvironmentVariable("DARCI_ROSCRIBE_URL");
        var ros2Cli = Environment.GetEnvironmentVariable("DARCI_ROS2_CLI_PATH");

        var libEmgEnabled = string.Equals(
            Environment.GetEnvironmentVariable("DARCI_LIBEMG_ENABLED"),
            "true",
            StringComparison.OrdinalIgnoreCase);

        var openBciPort = Environment.GetEnvironmentVariable("DARCI_OPENBCI_PORT");

        var statuses = new List<EngineeringProviderStatus>();

        var cadCoderConfigured = !string.IsNullOrWhiteSpace(cadCoderUrl);
        (bool Ok, string Message)? cadCoderProbe = probe && cadCoderConfigured
            ? await ProbeHttpEndpoint(cadCoderUrl!, ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "cadcoder",
            Category = "cad-model",
            Configured = cadCoderConfigured,
            Detected = !probe ? cadCoderConfigured : cadCoderProbe?.Ok == true,
            Mode = cadCoderConfigured ? "external-api" : "not-configured",
            Endpoint = cadCoderUrl,
            Probe = cadCoderProbe?.Message,
            Notes = "Dedicated CAD code model endpoint.",
            RequiredEnv = new List<string> { "DARCI_CADCODER_URL", "DARCI_CADCODER_API_KEY(optional)" }
        });

        var kittyConfigured = !string.IsNullOrWhiteSpace(kittyBase) && !string.IsNullOrWhiteSpace(kittyKey);
        (bool Ok, string Message)? kittyProbe = probe && kittyConfigured
            ? await ProbeHttpEndpoint(CombineUrl(kittyBase!, kittyPath), ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "kittycad",
            Category = "cad-model",
            Configured = kittyConfigured,
            Detected = !probe ? kittyConfigured : kittyProbe?.Ok == true,
            Mode = kittyConfigured ? "external-api" : "not-configured",
            Endpoint = kittyBase,
            Probe = kittyProbe?.Message,
            Notes = "Zoo text-to-CAD provider.",
            RequiredEnv = new List<string> { "DARCI_KITTYCAD_BASE_URL", "DARCI_KITTYCAD_API_KEY", "DARCI_KITTYCAD_PATH(optional)" }
        });

        var build123dConfigured = build123dEnabled;
        (bool Ok, string Message)? build123dProbe = probe && build123dConfigured
            ? await ProbePythonModule(pythonPath, "build123d", ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "build123d",
            Category = "cad-kernel",
            Configured = build123dConfigured,
            Detected = !probe ? build123dConfigured : build123dProbe?.Ok == true,
            Mode = build123dConfigured ? "python-module" : "disabled",
            Endpoint = pythonPath,
            Probe = build123dProbe?.Message,
            Notes = "Alternative parametric CAD kernel.",
            RequiredEnv = new List<string> { "DARCI_BUILD123D_ENABLED=true", "DARCI_PYTHON_PATH(optional)" }
        });

        var mujocoConfigured = mujocoEnabled;
        (bool Ok, string Message)? mujocoProbe = probe && mujocoConfigured
            ? await ProbePythonModule(pythonPath, "mujoco", ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "mujoco",
            Category = "simulation",
            Configured = mujocoConfigured,
            Detected = !probe ? mujocoConfigured : mujocoProbe?.Ok == true,
            Mode = mujocoConfigured ? "python-module" : "disabled",
            Endpoint = pythonPath,
            Probe = mujocoProbe?.Message,
            Notes = "Dynamics and range-of-motion sandbox.",
            RequiredEnv = new List<string> { "DARCI_MUJOCO_ENABLED=true", "DARCI_PYTHON_PATH(optional)" }
        });

        var calculixConfigured = !string.IsNullOrWhiteSpace(calculixPath);
        (bool Ok, string Message)? calculixProbe = probe && calculixConfigured
            ? await ProbeExecutable(calculixPath!, new[] { "-v" }, ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "calculix",
            Category = "validation",
            Configured = calculixConfigured,
            Detected = !probe ? calculixConfigured : calculixProbe?.Ok == true,
            Mode = calculixConfigured ? "local-cli" : "not-configured",
            Endpoint = calculixPath,
            Probe = calculixProbe?.Message,
            Notes = "Structural FEA solver.",
            RequiredEnv = new List<string> { "DARCI_CALCULIX_PATH" }
        });

        var kicadConfigured = !string.IsNullOrWhiteSpace(kicadCli);
        (bool Ok, string Message)? kicadProbe = probe && kicadConfigured
            ? await ProbeExecutable(kicadCli!, new[] { "version" }, ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "kicad",
            Category = "electronics",
            Configured = kicadConfigured,
            Detected = !probe ? kicadConfigured : kicadProbe?.Ok == true,
            Mode = kicadConfigured ? "local-cli" : "not-configured",
            Endpoint = kicadCli,
            Probe = kicadProbe?.Message,
            Notes = "Schematic and PCB generation.",
            RequiredEnv = new List<string> { "DARCI_KICAD_CLI_PATH" }
        });

        var kicadMcpConfigured = !string.IsNullOrWhiteSpace(kicadMcpUrl);
        (bool Ok, string Message)? kicadMcpProbe = probe && kicadMcpConfigured
            ? await ProbeHttpEndpoint(kicadMcpUrl!, ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "kicad-mcp",
            Category = "electronics",
            Configured = kicadMcpConfigured,
            Detected = !probe ? kicadMcpConfigured : kicadMcpProbe?.Ok == true,
            Mode = kicadMcpConfigured ? "external-api" : "not-configured",
            Endpoint = kicadMcpUrl,
            Probe = kicadMcpProbe?.Message,
            Notes = "Optional KiCad MCP gateway.",
            RequiredEnv = new List<string> { "DARCI_KICAD_MCP_URL" }
        });

        var libEmgConfigured = libEmgEnabled;
        (bool Ok, string Message)? libEmgProbe = probe && libEmgConfigured
            ? await ProbePythonModule(pythonPath, "libemg", ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "libemg",
            Category = "biosignal",
            Configured = libEmgConfigured,
            Detected = !probe ? libEmgConfigured : libEmgProbe?.Ok == true,
            Mode = libEmgConfigured ? "python-module" : "disabled",
            Endpoint = pythonPath,
            Probe = libEmgProbe?.Message,
            Notes = "EMG processing and classification.",
            RequiredEnv = new List<string> { "DARCI_LIBEMG_ENABLED=true", "DARCI_PYTHON_PATH(optional)" }
        });

        var openBciConfigured = !string.IsNullOrWhiteSpace(openBciPort);
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "openbci",
            Category = "biosignal",
            Configured = openBciConfigured,
            Detected = openBciConfigured,
            Mode = openBciConfigured ? "hardware-port" : "not-configured",
            Endpoint = openBciPort,
            Probe = openBciConfigured ? "Port configured." : null,
            Notes = "Optional OpenBCI serial/COM port binding.",
            RequiredEnv = new List<string> { "DARCI_OPENBCI_PORT" }
        });

        var ros2Configured = !string.IsNullOrWhiteSpace(ros2Cli);
        (bool Ok, string Message)? ros2Probe = probe && ros2Configured
            ? await ProbeExecutable(ros2Cli!, new[] { "--help" }, ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "ros2",
            Category = "robotics",
            Configured = ros2Configured,
            Detected = !probe ? ros2Configured : ros2Probe?.Ok == true,
            Mode = ros2Configured ? "local-cli" : "not-configured",
            Endpoint = ros2Cli,
            Probe = ros2Probe?.Message,
            Notes = "ROS2/ros2_control toolchain integration point.",
            RequiredEnv = new List<string> { "DARCI_ROS2_CLI_PATH" }
        });

        var roscribeConfigured = !string.IsNullOrWhiteSpace(roscribeUrl);
        (bool Ok, string Message)? roscribeProbe = probe && roscribeConfigured
            ? await ProbeHttpEndpoint(roscribeUrl!, ct)
            : null;
        statuses.Add(new EngineeringProviderStatus
        {
            Name = "roscribe",
            Category = "robotics",
            Configured = roscribeConfigured,
            Detected = !probe ? roscribeConfigured : roscribeProbe?.Ok == true,
            Mode = roscribeConfigured ? "external-api" : "not-configured",
            Endpoint = roscribeUrl,
            Probe = roscribeProbe?.Message,
            Notes = "Optional ROS code scaffolding API.",
            RequiredEnv = new List<string> { "DARCI_ROSCRIBE_URL" }
        });

        return statuses;
    }

    public static object GetSetupGuide()
    {
        return new
        {
            recommendedOrder = new[]
            {
                "1. cadcoder or kittycad",
                "2. mujoco + calculix",
                "3. kicad (+ optional kicad-mcp)",
                "4. libemg + openbci",
                "5. ros2 (+ optional roscribe)"
            },
            environmentTemplate = new Dictionary<string, string>
            {
                ["DARCI_CADCODER_URL"] = "",
                ["DARCI_CADCODER_API_KEY"] = "",
                ["CADCODER_PROVIDER"] = "ollama",
                ["CADCODER_OLLAMA_URL"] = "http://127.0.0.1:11434/api/generate",
                ["CADCODER_OLLAMA_MODEL"] = "gemma4:e4b",
                ["CADCODER_ADAPTER_API_KEY"] = "",
                ["DARCI_KITTYCAD_BASE_URL"] = "https://api.zoo.dev",
                ["DARCI_KITTYCAD_API_KEY"] = "",
                ["DARCI_KITTYCAD_PATH"] = "/v1/text-to-cad",
                ["DARCI_BUILD123D_ENABLED"] = "false",
                ["DARCI_MUJOCO_ENABLED"] = "false",
                ["DARCI_PYTHON_PATH"] = "python",
                ["DARCI_CALCULIX_PATH"] = "",
                ["DARCI_KICAD_CLI_PATH"] = "",
                ["DARCI_KICAD_MCP_URL"] = "",
                ["DARCI_KICAD_MCP_API_KEY"] = "",
                ["DARCI_LIBEMG_ENABLED"] = "false",
                ["DARCI_OPENBCI_PORT"] = "",
                ["DARCI_ROS2_CLI_PATH"] = "",
                ["DARCI_ROSCRIBE_URL"] = "",
                ["DARCI_ROSCRIBE_API_KEY"] = ""
            },
            notes = new[]
            {
                "Use GET /engineering/providers/status?probe=true after configuring env vars.",
                "Set bool flags to true only after dependency installation.",
                "Keep provider URLs reachable from Darci.Api runtime host."
            }
        };
    }

    private static async Task<(bool Ok, string Message)> ProbeHttpEndpoint(string url, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return (false, "Invalid URL.");
            }

            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var req = new HttpRequestMessage(HttpMethod.Get, uri);
            using var response = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            return ((int)response.StatusCode < 500, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex)
        {
            return (false, Trim(ex.Message));
        }
    }

    private static async Task<(bool Ok, string Message)> ProbePythonModule(
        string pythonExecutable,
        string moduleName,
        CancellationToken ct)
    {
        var code = $"import {moduleName}; print(getattr({moduleName}, '__version__', 'ok'))";
        return await ProbeExecutable(pythonExecutable, new[] { "-c", code }, ct);
    }

    private static async Task<(bool Ok, string Message)> ProbeExecutable(
        string executable,
        IReadOnlyList<string> args,
        CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(executable))
            {
                return (false, "Not configured.");
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var arg in args)
            {
                startInfo.ArgumentList.Add(arg);
            }

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return (false, "Failed to start process.");
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return (false, "Probe timeout.");
            }

            var stdout = (await process.StandardOutput.ReadToEndAsync()).Trim();
            var stderr = (await process.StandardError.ReadToEndAsync()).Trim();
            var output = string.IsNullOrWhiteSpace(stdout) ? stderr : stdout;
            var firstLine = FirstLine(output);
            if (process.ExitCode == 0)
            {
                return (true, string.IsNullOrWhiteSpace(firstLine) ? "ok" : firstLine);
            }

            return (false, string.IsNullOrWhiteSpace(firstLine) ? $"exit {process.ExitCode}" : firstLine);
        }
        catch (Exception ex)
        {
            return (false, Trim(ex.Message));
        }
    }

    private static string CombineUrl(string baseUrl, string path)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return path;
        }

        var normalizedBase = baseUrl.TrimEnd('/');
        var normalizedPath = string.IsNullOrWhiteSpace(path) ? "" : "/" + path.TrimStart('/');
        return normalizedBase + normalizedPath;
    }

    private static string FirstLine(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "";
        }

        var line = value
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? "";
        return Trim(line);
    }

    private static string Trim(string value)
    {
        var v = Regex.Replace(value ?? "", @"\s+", " ").Trim();
        return v.Length <= 220 ? v : v[..220];
    }
}
