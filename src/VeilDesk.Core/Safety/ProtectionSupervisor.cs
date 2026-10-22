using VeilDesk.Core.Settings;

namespace VeilDesk.Core.Safety;

/// <summary>
/// The states the protection layer can be in. The state decides whether the
/// output may be shown with censor masks, or whether the whole output has to
/// be covered.
/// </summary>
public enum ProtectionState
{
    /// <summary>Protection was explicitly stopped by the user. Nothing is drawn.</summary>
    Stopped,

    /// <summary>Protection is running and the last evidence is trustworthy.</summary>
    Protecting,

    /// <summary>
    /// Protection cannot be maintained. The complete output has to be covered;
    /// a partial warning overlay is not an acceptable substitute.
    /// </summary>
    FailClosed
}

/// <summary>
/// Supervises whether the local pipeline can still vouch for what is on screen.
///
/// The supervisor is deliberately free of WPF, capture and ONNX dependencies so
/// that the exact production decision can be replayed deterministically in
/// tests. Every entry point takes an explicit timestamp, so the behaviour does
/// not depend on wall-clock timing.
///
/// Design rules enforced here:
/// <list type="bullet">
/// <item>A new inference attempt never clears the fail-closed state. Only
/// evidence that the pipeline works again does.</item>
/// <item>Leaving fail-closed requires a minimum dwell time and a number of
/// consecutive successful analyses, so the state cannot oscillate.</item>
/// <item>The state only depends on evidence freshness and pipeline health, not
/// on the number of failed attempts, so a single transient error cannot make
/// the cover blink.</item>
/// </list>
/// </summary>
public sealed class ProtectionSupervisor
{
    private readonly object _gate = new();

    private ProtectionState _state = ProtectionState.Stopped;
    private string? _reason;
    private long _failClosedSinceMs = -1;
    private long _lastSuccessMs = -1;
    private long _lastFrameMs = -1;
    private long _lastAttemptMs = -1;
    private long _lastFailureMs = -1;
    private int _consecutiveFailures;
    private int _consecutiveSuccesses;
    private string? _lastError;
    private bool _captureFailed;
    private string? _captureError;

    private int _failClosedAfterMs = 1500;
    private int _recoveryPasses = 2;
    private int _minPausedMs = 1000;

    /// <summary>Raised on every state transition: (previous, current, reason).</summary>
    public event Action<ProtectionState, ProtectionState, string?>? StateChanged;

    public ProtectionState State
    {
        get { lock (_gate) return _state; }
    }

    /// <summary>True when the whole output must be covered.</summary>
    public bool FailClosed
    {
        get { lock (_gate) return _state == ProtectionState.FailClosed; }
    }

    /// <summary>Human readable reason for the current state.</summary>
    public string? Reason
    {
        get { lock (_gate) return _reason; }
    }

    /// <summary>Timestamp of the last analysis that completed without an error.</summary>
    public long LastSuccessMs
    {
        get { lock (_gate) return _lastSuccessMs; }
    }

    /// <summary>Timestamp of the last captured frame seen by the analysis loop.</summary>
    public long LastFrameMs
    {
        get { lock (_gate) return _lastFrameMs; }
    }

    /// <summary>Timestamp of the last inference attempt, successful or not.</summary>
    public long LastAttemptMs
    {
        get { lock (_gate) return _lastAttemptMs; }
    }

    public int ConsecutiveFailures
    {
        get { lock (_gate) return _consecutiveFailures; }
    }

    public int ConsecutiveSuccesses
    {
        get { lock (_gate) return _consecutiveSuccesses; }
    }

    public string? LastError
    {
        get { lock (_gate) return _lastError; }
    }

    public string? CaptureError
    {
        get { lock (_gate) return _captureError; }
    }

    /// <summary>Timestamp the fail-closed state was entered.</summary>
    public long FailClosedSinceMs
    {
        get { lock (_gate) return _failClosedSinceMs; }
    }

    /// <summary>Copies the relevant tuning values out of the user settings.</summary>
    public void ApplySettings(AppSettings settings)
    {
        var censoring = settings.Censoring;
        lock (_gate)
        {
            _failClosedAfterMs = Math.Max(0, censoring.FailClosedAfterMs);
            _recoveryPasses = Math.Max(1, censoring.FailClosedRecoveryPasses);
            _minPausedMs = Math.Max(0, censoring.FailClosedRecoveryMs);
        }
    }

    /// <summary>
    /// Begins a protection session. Until the first successful analysis the
    /// state is fail-closed, because nothing is known about the screen yet.
    /// </summary>
    public void Start(long nowMs)
    {
        ProtectionState previous;
        lock (_gate)
        {
            previous = _state;
            _state = ProtectionState.FailClosed;
            _reason = "protection started: no analysis has completed yet";
            _failClosedSinceMs = nowMs;
            _lastSuccessMs = -1;
            _lastFrameMs = nowMs;
            _lastAttemptMs = -1;
            _lastFailureMs = -1;
            _consecutiveFailures = 0;
            _consecutiveSuccesses = 0;
            _lastError = null;
            _captureFailed = false;
            _captureError = null;
        }
        Raise(previous, ProtectionState.FailClosed, "protection started");
    }

    /// <summary>
    /// An explicit user stop. Protection is terminated and no overlay may be
    /// drawn afterwards; this is different from an automatic safety pause.
    /// </summary>
    public void Stop()
    {
        ProtectionState previous;
        lock (_gate)
        {
            previous = _state;
            _state = ProtectionState.Stopped;
            _reason = "stopped by the user";
            _failClosedSinceMs = -1;
            _consecutiveSuccesses = 0;
        }
        Raise(previous, ProtectionState.Stopped, "stopped by the user");
    }

    /// <summary>A new frame was captured and handed to the analysis loop.</summary>
    public void ReportFrame(long nowMs)
    {
        lock (_gate)
        {
            if (_state == ProtectionState.Stopped || nowMs <= _lastFrameMs) return;
            _lastFrameMs = nowMs;
        }
    }

    /// <summary>
    /// An inference attempt started. This never clears the fail-closed state:
    /// a new attempt is not evidence that protection works again.
    /// </summary>
    public void ReportAttempt(long nowMs)
    {
        lock (_gate) _lastAttemptMs = nowMs;
    }

    /// <summary>The inference attempt completed without an error.</summary>
    public void ReportSuccess(long nowMs)
    {
        lock (_gate)
        {
            if (_state == ProtectionState.Stopped || nowMs <= _lastSuccessMs || nowMs <= _lastFailureMs) return;
            if (_lastSuccessMs >= 0 && nowMs - _lastSuccessMs > _failClosedAfterMs)
                _consecutiveSuccesses = 0;
            _lastSuccessMs = nowMs;
            _consecutiveSuccesses++;
            _consecutiveFailures = 0;
            _lastError = null;
        }
    }

    /// <summary>The inference attempt failed. The previous evidence is kept.</summary>
    public void ReportFailure(long nowMs, string? error)
    {
        lock (_gate)
        {
            if (_state == ProtectionState.Stopped) return;
            _lastFailureMs = nowMs;
            _consecutiveFailures++;
            _consecutiveSuccesses = 0;
            _lastError = error;
        }
    }

    /// <summary>The capture layer failed; the screen can no longer be trusted.</summary>
    public void ReportCaptureFailure(string? error)
    {
        lock (_gate)
        {
            _captureFailed = true;
            _captureError = error;
        }
    }

    /// <summary>The capture layer recovered.</summary>
    public void ReportCaptureHealthy()
    {
        lock (_gate)
        {
            _captureFailed = false;
            _captureError = null;
        }
    }

    /// <summary>
    /// Recomputes the protection state. Must be called before the overlay is
    /// built, because the result decides whether the whole output is covered.
    /// </summary>
    public ProtectionState Evaluate(long nowMs)
    {
        ProtectionState previous;
        ProtectionState current;
        string? reason;

        lock (_gate)
        {
            if (_state == ProtectionState.Stopped) return ProtectionState.Stopped;

            previous = _state;
            var neverTrusted = _lastSuccessMs < 0;
            var evidenceStale = !neverTrusted && (nowMs - _lastSuccessMs) > _failClosedAfterMs;
            var frameStale = nowMs - _lastFrameMs > _failClosedAfterMs;

            if (previous == ProtectionState.Protecting)
            {
                if (neverTrusted)
                {
                    current = ProtectionState.FailClosed;
                    reason = "no analysis has completed yet";
                }
                else if (evidenceStale)
                {
                    current = ProtectionState.FailClosed;
                    reason = $"no successful analysis for {nowMs - _lastSuccessMs} ms";
                }
                else if (_captureFailed || frameStale)
                {
                    current = ProtectionState.FailClosed;
                    reason = "capture unavailable or stale";
                }
                else
                {
                    current = ProtectionState.Protecting;
                    reason = _reason;
                }
            }
            else
            {
                var healthy = !neverTrusted && !evidenceStale && !_captureFailed && !frameStale;
                var dwellElapsed = _failClosedSinceMs >= 0 && (nowMs - _failClosedSinceMs) >= _minPausedMs;
                var enoughPasses = _consecutiveSuccesses >= _recoveryPasses;
                if (healthy && dwellElapsed && enoughPasses)
                {
                    current = ProtectionState.Protecting;
                    reason = "recovered after " + _consecutiveSuccesses + " successful analyses";
                }
                else
                {
                    current = ProtectionState.FailClosed;
                    reason = neverTrusted
                        ? "no analysis has completed yet"
                        : evidenceStale
                            ? $"no successful analysis for {nowMs - _lastSuccessMs} ms"
                            : _captureFailed
                                ? "capture failed: " + (_captureError ?? "unknown error")
                                : "waiting for a stable recovery";
                }
            }

            if (current != previous)
            {
                _state = current;
                if (current == ProtectionState.FailClosed)
                {
                    _failClosedSinceMs = nowMs;
                    _consecutiveSuccesses = 0;
                }
                _reason = reason;
            }
            else if (current == ProtectionState.Protecting && _reason != reason)
            {
                _reason = reason;
            }
        }

        if (current != previous) Raise(previous, current, reason);
        return current;
    }

    /// <summary>
    /// Effective fail-closed flag. The user setting can disable the safety
    /// cover, but it can never turn the supervisor into a "reveal" signal.
    /// </summary>
    public bool EffectiveFailClosed(AppSettings settings)
        => settings.Censoring.FailClosed && FailClosed;

    private void Raise(ProtectionState previous, ProtectionState current, string? reason)
    {
        try
        {
            StateChanged?.Invoke(previous, current, reason);
        }
        catch
        {
            // A diagnostic listener must never break protection.
        }
    }
}
