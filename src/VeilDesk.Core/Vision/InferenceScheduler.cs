namespace VeilDesk.Core.Vision;

/// <summary>
/// Decides when a neural inference pass is worth running. Rendering remains at
/// display refresh rate; only detection is throttled. This is deliberately
/// conservative: a static scene still gets a periodic safety scan.
/// </summary>
public sealed class InferenceScheduler
{
    private long _lastRunMs;
    private bool _hasRun;
    private double _lastDelta = 1.0;

    public long LastRunMs => _lastRunMs;
    public double LastSceneDelta => _lastDelta;

    public bool ShouldRun(long nowMs, double sceneDelta, int scanRateHz, bool adaptive, double staticThreshold)
    {
        _lastDelta = sceneDelta;
        var rate = Math.Clamp(scanRateHz, 1, 60);
        var interval = 1000.0 / rate;
        var elapsed = nowMs - _lastRunMs;
        if (!_hasRun || elapsed >= interval)
        {
            if (!adaptive) return true;
            // On a still scene, do not spend power at the full requested rate.
            if (sceneDelta >= staticThreshold) return true;
            return !_hasRun || elapsed >= Math.Max(interval, 1000.0);
        }
        return false;
    }

    public void MarkRun(long nowMs) { _lastRunMs = nowMs; _hasRun = true; }
    public void Reset() { _lastRunMs = 0; _hasRun = false; _lastDelta = 1.0; }
}
