namespace Darci.Tools.Cad;

/// <summary>
/// C# models matching the Python CAD engine's API contracts.
/// </summary>

// ── Requests (C# → Python) ──

public class CadGenerateRequest
{
    public string Script { get; set; } = "";
    public string Filename { get; set; } = "output.stl";
    public CadDimensionSpec? Dimensions { get; set; }
}

public class CadFeedbackRequest
{
    public string OriginalRequest { get; set; } = "";
    public CadGenerateResponse CadResult { get; set; } = new();
}

public class CadDimensionSpec
{
    public float? LengthMm { get; set; }
    public float? WidthMm { get; set; }
    public float? HeightMm { get; set; }
    public Dictionary<string, object>? Features { get; set; }
}

// ── Responses (Python → C#) ──

public class CadGenerateResponse
{
    public bool Success { get; set; }
    public string? StlPath { get; set; }
    public Dictionary<string, string?> RenderImages { get; set; } = new();
    public CadValidationResult? Validation { get; set; }
    public string ScriptUsed { get; set; } = "";
    public string? Error { get; set; }
    public int Iteration { get; set; }
}

public class CadValidationResult
{
    public bool IsWatertight { get; set; }
    public int TriangleCount { get; set; }
    public Dictionary<string, float> BoundingBoxMm { get; set; } = new();
    public List<CadDimensionError> DimensionErrors { get; set; } = new();
    public float VolumeCc { get; set; }
    public List<string> Warnings { get; set; } = new();
    public bool Passed { get; set; }
}

public class CadDimensionError
{
    public string Dimension { get; set; } = "";
    public float ExpectedMm { get; set; }
    public float ActualMm { get; set; }
    public float ErrorMm { get; set; }
}

public class CadFeedbackResponse
{
    public string FeedbackPrompt { get; set; } = "";
}

// ── Pipeline result (internal) ──

public class CadPipelineResult
{
    public bool Success { get; set; }
    public string OriginalRequest { get; set; } = "";
    public string? FinalStlPath { get; set; }
    public Dictionary<string, string?>? FinalRenders { get; set; }
    public CadValidationResult? FinalValidation { get; set; }
    public string? Error { get; set; }
    public int? ApprovedAtIteration { get; set; }
    public List<CadIterationLog> Iterations { get; set; } = new();
}

public class CadIterationLog
{
    public int Iteration { get; set; }
    public string Script { get; set; } = "";
    public CadGenerateResponse? Result { get; set; }
    public string? StoppedReason { get; set; }
}
