namespace VeilDesk.Core.Censoring;

/// <summary>Per-session Schmitt trigger; acquisition and release have distinct thresholds.</summary>
public sealed class ConfidenceGate
{
    private readonly HashSet<int> _acquired = new();

    public bool Allows(int id, double confidence, double threshold, double releaseRatio)
    {
        var minimum = _acquired.Contains(id) ? threshold * releaseRatio : threshold;
        if (confidence >= minimum) { _acquired.Add(id); return true; }
        _acquired.Remove(id);
        return false;
    }

    public void RetainOnly(IEnumerable<int> ids) => _acquired.IntersectWith(ids);
    public void Reset() => _acquired.Clear();
}
