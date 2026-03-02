using Darci.Tools.Cad;

namespace Darci.Tools.Engineering.Providers;

public sealed class EngineeringProviderRequest
{
    public string Description { get; init; } = "";
    public string? PartType { get; init; }
    public Dictionary<string, double>? Parameters { get; init; }
    public CadDimensionSpec? Dimensions { get; init; }
}

public sealed class EngineeringProviderScriptResult
{
    public string ProviderName { get; init; } = "";
    public bool Success { get; init; }
    public string? Script { get; init; }
    public string? Error { get; init; }
}

public interface IEngineeringCadProvider
{
    string Name { get; }
    bool IsConfigured { get; }
    Task<EngineeringProviderScriptResult?> TryGenerateScript(EngineeringProviderRequest request, CancellationToken ct = default);
}
