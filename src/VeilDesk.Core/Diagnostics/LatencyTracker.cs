namespace VeilDesk.Core.Diagnostics;

/// <summary>Thread-safe rolling latency statistics with real percentiles.</summary>
public sealed class LatencyTracker
{
    private readonly double[] _samples;
    private int _count;
    private int _next;
    private readonly object _gate = new();

    public LatencyTracker(int capacity = 512)
    {
        _samples = new double[Math.Max(8, capacity)];
    }

    public int Count { get { lock (_gate) return _count; } }

    public void Add(double milliseconds)
    {
        if (double.IsNaN(milliseconds) || double.IsInfinity(milliseconds) || milliseconds < 0) return;
        lock (_gate)
        {
            _samples[_next] = milliseconds;
            _next = (_next + 1) % _samples.Length;
            if (_count < _samples.Length) _count++;
        }
    }

    public LatencySummary Snapshot()
    {
        double[] copy;
        lock (_gate) copy = _samples.Take(_count).ToArray();
        if (copy.Length == 0) return LatencySummary.Empty;
        Array.Sort(copy);
        return new LatencySummary(
            copy.Average(),
            Percentile(copy, 0.50),
            Percentile(copy, 0.95),
            Percentile(copy, 0.99),
            copy[^1],
            copy.Length);
    }

    public void Clear() { lock (_gate) { _count = 0; _next = 0; } }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0;
        var index = (int)Math.Ceiling(p * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}

public readonly record struct LatencySummary(double MeanMs, double P50Ms, double P95Ms, double P99Ms, double MaxMs, int Count)
{
    public static readonly LatencySummary Empty = new(0, 0, 0, 0, 0, 0);
}
