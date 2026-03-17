using System.Text.Json.Serialization;

namespace Darci.Engineering;

/// <summary>Result from executing one action on an engineering tool.</summary>
public record ToolStepResult
{
    [JsonPropertyName("state")]
    public float[] State { get; init; } = Array.Empty<float>();

    [JsonPropertyName("metrics")]
    public Dictionary<string, float> Metrics { get; init; } = new();

    [JsonPropertyName("success")]
    public bool Success { get; init; }

    [JsonPropertyName("error_message")]
    public string? ErrorMessage { get; init; }

    [JsonPropertyName("reward_components")]
    public Dictionary<string, float> RewardComponents { get; init; } = new();
}

/// <summary>Result from running full validation on a tool.</summary>
public record ToolValidationResult
{
    [JsonPropertyName("passed")]
    public bool Passed { get; init; }

    [JsonPropertyName("overall_score")]
    public float OverallScore { get; init; }

    [JsonPropertyName("category_scores")]
    public Dictionary<string, float> CategoryScores { get; init; } = new();

    [JsonPropertyName("violations")]
    public List<ToolViolation> Violations { get; init; } = new();
}

/// <summary>A specific validation failure or warning.</summary>
public record ToolViolation
{
    [JsonPropertyName("category")]
    public string Category { get; init; } = "";

    [JsonPropertyName("severity")]
    public string Severity { get; init; } = "warning";

    [JsonPropertyName("description")]
    public string Description { get; init; } = "";

    [JsonPropertyName("value")]
    public float? Value { get; init; }

    [JsonPropertyName("threshold")]
    public float? Threshold { get; init; }

    [JsonPropertyName("location")]
    public float[]? Location { get; init; }
}

/// <summary>Request to reset a workbench session.</summary>
public record WorkbenchResetRequest
{
    [JsonPropertyName("reference_path")]
    public string? ReferencePath { get; init; }

    [JsonPropertyName("constraints")]
    public Dictionary<string, object>? Constraints { get; init; }

    [JsonPropertyName("targets")]
    public Dictionary<string, float>? Targets { get; init; }
}

/// <summary>Request to execute an action.</summary>
public record WorkbenchExecuteRequest
{
    [JsonPropertyName("action_id")]
    public int ActionId { get; init; }

    [JsonPropertyName("parameters")]
    public float[] Parameters { get; init; } = new float[6];
}

/// <summary>Response from workbench reset.</summary>
public record WorkbenchResetResponse
{
    [JsonPropertyName("state")]
    public float[] State { get; init; } = Array.Empty<float>();

    [JsonPropertyName("action_mask")]
    public bool[] ActionMask { get; init; } = Array.Empty<bool>();
}

/// <summary>Response from workbench state endpoint.</summary>
public record WorkbenchStateResponse
{
    [JsonPropertyName("state")]
    public float[] State { get; init; } = Array.Empty<float>();

    [JsonPropertyName("step_count")]
    public int StepCount { get; init; }

    [JsonPropertyName("is_active")]
    public bool IsActive { get; init; }
}

/// <summary>Response from workbench action mask endpoint.</summary>
public record WorkbenchActionMaskResponse
{
    [JsonPropertyName("mask")]
    public bool[] Mask { get; init; } = Array.Empty<bool>();

    [JsonPropertyName("valid_action_names")]
    public List<string> ValidActionNames { get; init; } = new();
}

/// <summary>Response from workbench health endpoint.</summary>
public record WorkbenchHealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    [JsonPropertyName("has_geometry")]
    public bool HasGeometry { get; init; }

    [JsonPropertyName("step_count")]
    public int StepCount { get; init; }
}

/// <summary>Result of one engineering orchestration loop.</summary>
public record EngineeringResult
{
    public bool Success { get; init; }
    public float FinalScore { get; init; }
    public bool ValidationPassed { get; init; }
    public int StepsTaken { get; init; }
    public float TotalReward { get; init; }
    public string? ExportedStlPath { get; init; }
    public ToolValidationResult? FinalValidation { get; init; }
    public string? ErrorMessage { get; init; }
}
