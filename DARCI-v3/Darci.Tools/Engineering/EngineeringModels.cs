using Darci.Tools.Cad;

namespace Darci.Tools.Engineering;

public class EngineeringWorkRequest
{
    public string Description { get; set; } = "";
    public string? PartType { get; set; }
    public Dictionary<string, double>? Parameters { get; set; }
    public bool ProviderOnly { get; set; }
    public CadDimensionSpec? Dimensions { get; set; }
    public int MaxIterations { get; set; } = 3;
}

public class EngineeringWorkbenchResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public string? FinalScript { get; set; }
    public CadGenerateResponse? CadResult { get; set; }
    public string? GenerationSource { get; set; }
    public List<EngineeringProviderAttempt> ProviderAttempts { get; set; } = new();
}

public class EngineeringProviderAttempt
{
    public string Provider { get; set; } = "";
    public bool Success { get; set; }
    public string? Error { get; set; }
}
