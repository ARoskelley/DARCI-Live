using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace Darci.Api;

public interface IEngineeringAssemblySimulationClient
{
    Task<EngineeringAssemblySimulationReport> Simulate(
        EngineeringAssemblySimulationRequest request,
        CancellationToken ct = default);
}

public sealed class EngineeringAssemblySimulationClient : IEngineeringAssemblySimulationClient
{
    private readonly HttpClient _http;
    private readonly ILogger<EngineeringAssemblySimulationClient> _logger;

    public EngineeringAssemblySimulationClient(
        HttpClient http,
        ILogger<EngineeringAssemblySimulationClient> logger)
    {
        _http = http;
        _logger = logger;

        var pythonBaseUrl = Environment.GetEnvironmentVariable("DARCI_PYTHON_SERVICE_BASE_URL");
        if (string.IsNullOrWhiteSpace(pythonBaseUrl))
        {
            pythonBaseUrl = "http://localhost:8000";
        }

        _http.BaseAddress = new Uri(pythonBaseUrl, UriKind.Absolute);
        _http.Timeout = TimeSpan.FromSeconds(ReadTimeoutSeconds());
        _logger.LogInformation(
            "Engineering simulation client configured: baseUrl={BaseUrl}, timeoutSeconds={TimeoutSeconds}",
            _http.BaseAddress,
            _http.Timeout.TotalSeconds);
    }

    public async Task<EngineeringAssemblySimulationReport> Simulate(
        EngineeringAssemblySimulationRequest request,
        CancellationToken ct = default)
    {
        try
        {
            var response = await _http.PostAsJsonAsync("/simulation/assembly", request, cancellationToken: ct);
            response.EnsureSuccessStatusCode();

            var report = await response.Content.ReadFromJsonAsync<EngineeringAssemblySimulationReport>(cancellationToken: ct);
            if (report != null)
            {
                return report;
            }

            return BuildFailure("Simulation service returned an empty response payload.");
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Engineering assembly simulation timed out after {TimeoutSeconds}s", _http.Timeout.TotalSeconds);
            return BuildFailure(
                $"Simulation service timeout after {_http.Timeout.TotalSeconds:0} seconds. " +
                "Increase DARCI_SIMULATION_TIMEOUT_SECONDS or reduce simulation sample count.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Engineering assembly simulation call failed");
            return BuildFailure($"Simulation service call failed: {ex.Message}");
        }
    }

    private static EngineeringAssemblySimulationReport BuildFailure(string message)
    {
        return new EngineeringAssemblySimulationReport
        {
            Passed = false,
            Issues =
            {
                new EngineeringAssemblySimulationIssue
                {
                    Severity = "error",
                    Code = "simulation_service_error",
                    Message = message
                }
            }
        };
    }

    private static int ReadTimeoutSeconds()
    {
        var raw = Environment.GetEnvironmentVariable("DARCI_SIMULATION_TIMEOUT_SECONDS");
        if (int.TryParse(raw, out var value))
        {
            return Math.Clamp(value, 30, 3600);
        }

        return 900;
    }
}

public sealed class EngineeringAssemblySimulationRequest
{
    public List<EngineeringAssemblySimulationPart> Parts { get; init; } = new();
    public List<EngineeringAssemblySimulationConnection> Connections { get; init; } = new();
    public double CollisionToleranceMm { get; init; } = 0.1;
    public double ClearanceTargetMm { get; init; } = 0.2;
    public int SamplePointsPerMesh { get; init; } = 256;
}

public sealed class EngineeringAssemblySimulationPart
{
    public string Name { get; init; } = "";
    public string? PartType { get; init; }
    public string? StlPath { get; init; }
    public double X { get; init; }
    public double Y { get; init; }
    public double Z { get; init; }
    public double RxDeg { get; init; }
    public double RyDeg { get; init; }
    public double RzDeg { get; init; }
}

public sealed class EngineeringAssemblySimulationConnection
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Relation { get; init; } = "";
    public EngineeringAssemblyMotionSpec? Motion { get; init; }
}

public sealed class EngineeringAssemblyMotionSpec
{
    public string? Type { get; init; }
    public List<double>? Axis { get; init; }
    public double? RangeDeg { get; init; }
    public double? RangeMm { get; init; }
    public int? Steps { get; init; }
    public List<double>? PivotMm { get; init; }
    public string? MovingPart { get; init; }
}

public sealed class EngineeringAssemblySimulationReport
{
    public bool Passed { get; init; }
    public int StaticPairsChecked { get; init; }
    public int StaticCollisionCount { get; init; }
    public double? GlobalMinClearanceMm { get; init; }
    public List<EngineeringAssemblySimulationMotionCheck> MotionChecks { get; init; } = new();
    public List<EngineeringAssemblySimulationIssue> Issues { get; init; } = new();
}

public sealed class EngineeringAssemblySimulationMotionCheck
{
    public string From { get; init; } = "";
    public string To { get; init; } = "";
    public string Relation { get; init; } = "";
    public string MotionType { get; init; } = "";
    public bool Passed { get; init; }
    public double? MinClearanceMm { get; init; }
    public List<int> CollisionSteps { get; init; } = new();
}

public sealed class EngineeringAssemblySimulationIssue
{
    public string Severity { get; init; } = "warning";
    public string Code { get; init; } = "";
    public string Message { get; init; } = "";
    public string? PartA { get; init; }
    public string? PartB { get; init; }
    public string? Connection { get; init; }
}
