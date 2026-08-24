#nullable enable

namespace Darci.Shared;

/// <summary>
/// Serialises local model use across DARCI's subsystems.
///
/// Why this exists
/// ---------------
/// DARCI's living loop (Darci.Tools.Ollama.OllamaClient, general model) and the coding agent
/// loop (Darci.Coding.ModelRouter, coding model) both target the SAME Ollama instance —
/// http://localhost:11434 by default. They request DIFFERENT models. When Ollama does not have
/// enough VRAM to hold both resident, every alternation forces an unload/reload of a multi-GB
/// model. DARCI_CODING_ENVIRONMENT_LOG.md (sixth pass, 2026-06-11) records full coding runs
/// exceeding 90 minutes on the development machine as a direct result.
///
/// A second Ollama instance does NOT fix this on single-GPU hardware: two servers still compete
/// for the same VRAM and compute, and each tries to hold its own model resident. It only helps
/// with a second GPU, or VRAM large enough for both models at once — neither of which can be
/// assumed for AI Society team leads on unknown hardware.
///
/// So the fix is to bound concurrency in the application: exactly one subsystem drives the local
/// model at a time.
///
/// Semantics
/// ---------
/// - The coding loop takes a LONG exclusive lease via <see cref="AcquireAsync"/> and holds it for
///   the whole run. It is willing to wait.
/// - The living loop takes a SHORT opportunistic lease via <see cref="TryAcquireAsync"/> before
///   each model call. If focus is unavailable within its patience window it SOFT-SKIPS that call
///   rather than blocking the cycle — perception, messaging, and non-model actions keep running.
///
/// This is deliberately not a fair queue. Coding is the long-running foreground job; the living
/// loop yields to it. That is the "pause/yield core autonomy" behaviour the sixth-pass log
/// recommended.
/// </summary>
/// <summary>
/// Why a model call is being made. Determines whether it yields during a coding run.
/// </summary>
public enum ModelCallKind
{
    /// <summary>
    /// Autonomous / background work — Think cycles, memory consolidation, LLM-backed research,
    /// goal decomposition, CAD planning. Yields during a coding run. This is the default because
    /// most callers are background and the safe failure is to stand down.
    /// </summary>
    Background = 0,

    /// <summary>
    /// A direct user-facing reply. Under the default <see cref="FocusMode.Narrow"/> policy this
    /// passes through even while a coding run holds focus, so DARCI stays responsive to a person
    /// typing at her rather than going silent for the length of a run.
    /// </summary>
    Foreground = 1,
}

/// <summary>
/// How aggressively the living loop yields to a coding run.
/// Set with DARCI_FOCUS_MODE = narrow | broad | off.
/// </summary>
public enum FocusMode
{
    /// <summary>
    /// DEFAULT. Background model work yields; direct user replies pass through. The 90-minute
    /// contention in the sixth-pass log came from long-running background work competing with the
    /// coding loop, not from short foreground replies.
    /// </summary>
    Narrow = 0,

    /// <summary>
    /// Everything yields, including user replies. For constrained-VRAM hosts where even a single
    /// foreground reply mid-run forces a model swap costing tens of seconds — and where repeated
    /// messages would thrash. The cost is that DARCI goes quiet for the duration of a run.
    /// </summary>
    Broad = 1,

    /// <summary>
    /// Gate is transparent. For hosts with VRAM for both models resident, or a second GPU.
    /// </summary>
    Off = 2,
}

public interface IModelFocus
{
    /// <summary>True when some subsystem currently holds focus.</summary>
    bool IsHeld { get; }

    /// <summary>Name of the current holder, or null when free.</summary>
    string? Holder { get; }

    /// <summary>When the current lease was taken, or null when free.</summary>
    DateTime? HeldSinceUtc { get; }

    /// <summary>
    /// Wait as long as necessary for exclusive focus. Used by long-running foreground work
    /// (the coding agent loop). Dispose the returned lease to release.
    /// </summary>
    Task<IDisposable> AcquireAsync(string holder, CancellationToken ct = default);

    /// <summary>
    /// Try to take focus within <paramref name="maxWait"/>. Returns null if focus could not be
    /// obtained in time, in which case the caller should skip its model call. Used by the
    /// living loop so it degrades instead of stalling.
    /// </summary>
    Task<IDisposable?> TryAcquireAsync(string holder, TimeSpan maxWait, CancellationToken ct = default);

    /// <summary>
    /// Policy-aware acquisition. Applies the configured <see cref="FocusMode"/> to decide whether a
    /// call of this <paramref name="kind"/> should yield at all.
    ///
    /// Returns a lease to proceed with (which may be a no-op lease when the policy says this call
    /// is exempt), or null meaning "focus is busy — skip this call".
    ///
    /// Centralising the policy here keeps callers from each re-deriving it.
    /// </summary>
    Task<IDisposable?> TryAcquireForAsync(
        string holder,
        ModelCallKind kind,
        TimeSpan maxWait,
        CancellationToken ct = default);

    /// <summary>Snapshot for the /model-focus/status endpoint.</summary>
    ModelFocusStatus GetStatus();
}

public sealed record ModelFocusStatus(
    bool IsHeld,
    string? Holder,
    DateTime? HeldSinceUtc,
    double? HeldForSeconds,
    long TotalAcquisitions,
    long TotalSoftSkips,
    long TotalForegroundBypasses,
    FocusMode Mode);

/// <inheritdoc cref="IModelFocus"/>
public sealed class ModelFocus : IModelFocus
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _metaLock = new();

    private string? _holder;
    private DateTime? _heldSinceUtc;
    private long _totalAcquisitions;
    private long _totalSoftSkips;
    private long _totalForegroundBypasses;

    /// <summary>
    /// Default patience for the living loop. Short on purpose: if coding holds focus, we want the
    /// cycle to move on quickly rather than block. Override with DARCI_FOCUS_CORE_WAIT_SECONDS.
    /// </summary>
    public static TimeSpan DefaultCoreWait
    {
        get
        {
            var raw = Environment.GetEnvironmentVariable("DARCI_FOCUS_CORE_WAIT_SECONDS");
            if (double.TryParse(raw, out var seconds) && seconds >= 0 && seconds <= 600)
            {
                return TimeSpan.FromSeconds(seconds);
            }

            return TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>
    /// Configured policy. DARCI_FOCUS_MODE = narrow (default) | broad | off.
    ///
    /// Legacy DARCI_FOCUS_MODE_ENABLED=false is still honoured as an alias for "off" so existing
    /// .env files keep working.
    ///
    /// NOTE FOR THE BOOTSTRAPPER: the model tier recommendation should carry a default focus mode
    /// with it. A host sized for one model at a time wants `broad`; a host with headroom for the
    /// general and coding models resident simultaneously wants `narrow` or `off`. Tiering that
    /// does not also set this will produce cores that feel broken in one direction or thrash in
    /// the other.
    /// </summary>
    public static FocusMode CurrentMode
    {
        get
        {
            if (string.Equals(
                    Environment.GetEnvironmentVariable("DARCI_FOCUS_MODE_ENABLED"),
                    "false",
                    StringComparison.OrdinalIgnoreCase))
            {
                return FocusMode.Off;
            }

            var raw = Environment.GetEnvironmentVariable("DARCI_FOCUS_MODE");
            return raw?.Trim().ToLowerInvariant() switch
            {
                "broad" => FocusMode.Broad,
                "off" or "none" or "disabled" => FocusMode.Off,
                _ => FocusMode.Narrow,
            };
        }
    }

    /// <summary>True unless the gate is entirely transparent.</summary>
    public static bool Enabled => CurrentMode != FocusMode.Off;

    public bool IsHeld => _gate.CurrentCount == 0;

    public string? Holder
    {
        get { lock (_metaLock) { return _holder; } }
    }

    public DateTime? HeldSinceUtc
    {
        get { lock (_metaLock) { return _heldSinceUtc; } }
    }

    public async Task<IDisposable> AcquireAsync(string holder, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return NoOpLease.Instance;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        MarkHeld(holder);
        return new Lease(this);
    }

    public async Task<IDisposable?> TryAcquireAsync(string holder, TimeSpan maxWait, CancellationToken ct = default)
    {
        if (!Enabled)
        {
            return NoOpLease.Instance;
        }

        // Genuine cancellation propagates to the caller; a timeout simply means "busy".
        var entered = await _gate.WaitAsync(maxWait, ct).ConfigureAwait(false);

        if (!entered)
        {
            Interlocked.Increment(ref _totalSoftSkips);
            return null;
        }

        MarkHeld(holder);
        return new Lease(this);
    }

    public async Task<IDisposable?> TryAcquireForAsync(
        string holder,
        ModelCallKind kind,
        TimeSpan maxWait,
        CancellationToken ct = default)
    {
        var mode = CurrentMode;

        // Transparent — nothing is serialised.
        if (mode == FocusMode.Off)
        {
            return NoOpLease.Instance;
        }

        // Narrow (default): a person is waiting on this one. Let it through even mid-run.
        // On a VRAM-constrained host this may force a model swap; that is what `broad` is for.
        if (mode == FocusMode.Narrow && kind == ModelCallKind.Foreground)
        {
            Interlocked.Increment(ref _totalForegroundBypasses);
            return NoOpLease.Instance;
        }

        // Background work, or broad mode: take a turn or stand down.
        return await TryAcquireAsync(holder, maxWait, ct).ConfigureAwait(false);
    }

    public ModelFocusStatus GetStatus()
    {
        lock (_metaLock)
        {
            var heldFor = _heldSinceUtc is null
                ? (double?)null
                : (DateTime.UtcNow - _heldSinceUtc.Value).TotalSeconds;

            return new ModelFocusStatus(
                IsHeld: _gate.CurrentCount == 0,
                Holder: _holder,
                HeldSinceUtc: _heldSinceUtc,
                HeldForSeconds: heldFor,
                TotalAcquisitions: Interlocked.Read(ref _totalAcquisitions),
                TotalSoftSkips: Interlocked.Read(ref _totalSoftSkips),
                TotalForegroundBypasses: Interlocked.Read(ref _totalForegroundBypasses),
                Mode: CurrentMode);
        }
    }

    private void MarkHeld(string holder)
    {
        Interlocked.Increment(ref _totalAcquisitions);
        lock (_metaLock)
        {
            _holder = holder;
            _heldSinceUtc = DateTime.UtcNow;
        }
    }

    private void Release()
    {
        lock (_metaLock)
        {
            _holder = null;
            _heldSinceUtc = null;
        }

        _gate.Release();
    }

    private sealed class Lease : IDisposable
    {
        private ModelFocus? _owner;

        public Lease(ModelFocus owner) => _owner = owner;

        public void Dispose()
        {
            // Interlocked so a double-Dispose cannot over-release the semaphore.
            var owner = Interlocked.Exchange(ref _owner, null);
            owner?.Release();
        }
    }

    private sealed class NoOpLease : IDisposable
    {
        public static readonly NoOpLease Instance = new();
        public void Dispose() { }
    }
}
