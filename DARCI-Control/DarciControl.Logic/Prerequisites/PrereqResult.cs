#nullable enable

namespace DarciControl.Logic.Prerequisites;

/// <summary>
/// How a prerequisite came out. The three-way split matters: most things DARCI needs are optional, and
/// collapsing "missing but survivable" into "failed" would tell the user their machine is broken when the
/// core would have started perfectly well on SQLite with degraded language features.
/// </summary>
public enum PrereqState
{
    /// <summary>Not checked yet — the UI's resting state.</summary>
    Unknown = 0,

    /// <summary>Present and usable.</summary>
    Ok = 1,

    /// <summary>Absent or degraded, but the core will still start. Say what is lost.</summary>
    Warning = 2,

    /// <summary>The core cannot start without this.</summary>
    Failed = 3,
}

/// <summary>
/// One prerequisite's verdict. <see cref="Remedy"/> is the whole point of the record existing: a check
/// that says "Ollama missing" and stops is barely more useful than the failure itself.
/// </summary>
/// <param name="Name">What was checked, as the user should see it.</param>
/// <param name="State">The verdict.</param>
/// <param name="Detail">What was actually found — a version, a URL, an error.</param>
/// <param name="Remedy">The concrete next action, when there is one (e.g. <c>ollama pull gemma2:9b</c>).</param>
public sealed record PrereqResult(string Name, PrereqState State, string Detail, string? Remedy = null)
{
    public static PrereqResult Ok(string name, string detail) => new(name, PrereqState.Ok, detail);

    public static PrereqResult Warning(string name, string detail, string? remedy = null) =>
        new(name, PrereqState.Warning, detail, remedy);

    public static PrereqResult Failed(string name, string detail, string? remedy = null) =>
        new(name, PrereqState.Failed, detail, remedy);
}

/// <summary>The whole preflight, and whether the core can be started from it.</summary>
public sealed record PrereqReport(IReadOnlyList<PrereqResult> Results)
{
    /// <summary>Nothing hard-blocks startup. Warnings are explicitly fine — that is what they are for.</summary>
    public bool CanStart => Results.All(r => r.State != PrereqState.Failed);

    public IReadOnlyList<PrereqResult> Blocking =>
        Results.Where(r => r.State == PrereqState.Failed).ToList();

    public IReadOnlyList<PrereqResult> Warnings =>
        Results.Where(r => r.State == PrereqState.Warning).ToList();
}
