namespace Darci.Engineering;

/// <summary>
/// Universal interface for all engineering tools.
/// Each tool is a standalone service with a numerical interface:
/// state vector in, action + params out, quality metrics back.
///
/// Implementations:
///   - GeometryWorkbenchClient (HTTP client for Python workbench service)
///   - Future: BoardDesignerClient, SimulationBridgeClient, etc.
/// </summary>
public interface IEngineeringTool
{
    string ToolId { get; }
    string DisplayName { get; }
    int StateDimensions { get; }
    int ActionCount { get; }

    /// <summary>Check if the tool service is reachable.</summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);

    /// <summary>Reset the tool to a clean state. Returns initial state vector and action mask.</summary>
    Task<WorkbenchResetResponse> ResetAsync(WorkbenchResetRequest? request = null, CancellationToken ct = default);

    /// <summary>Get the current state vector.</summary>
    Task<float[]> GetStateAsync(CancellationToken ct = default);

    /// <summary>Get the current action validity mask.</summary>
    Task<bool[]> GetActionMaskAsync(CancellationToken ct = default);

    /// <summary>Execute an action with continuous parameters.</summary>
    Task<ToolStepResult> ExecuteAsync(int actionId, float[] parameters, CancellationToken ct = default);

    /// <summary>Run full validation suite.</summary>
    Task<ToolValidationResult> ValidateAsync(CancellationToken ct = default);

    /// <summary>Undo the last action.</summary>
    Task<bool> UndoAsync(CancellationToken ct = default);
}
