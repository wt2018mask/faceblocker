namespace VeilDesk.Core.Safety;

/// <summary>Remembers which live tracks have required censorship in this session.</summary>
public sealed class CoverageGuard
{
    private readonly HashSet<int> _required = new();

    public bool RequiresCover(IEnumerable<int> liveTrackIds, IEnumerable<int> coveredTrackIds,
        IEnumerable<int>? uncertainCandidateIds = null)
    {
        var covered = coveredTrackIds.ToHashSet();
        _required.IntersectWith(liveTrackIds);
        if (uncertainCandidateIds is not null) _required.UnionWith(uncertainCandidateIds);
        var missing = _required.Any(id => !covered.Contains(id));
        _required.UnionWith(covered);
        return missing;
    }

    public void Reset() => _required.Clear();
}
