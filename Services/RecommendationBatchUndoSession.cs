using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

public sealed class RecommendationBatchUndoSession
{
    private RecommendationBatchUndoSnapshot? _snapshot;

    public bool HasSnapshot => _snapshot is not null;

    public bool TryGet(out RecommendationBatchUndoSnapshot? snapshot)
    {
        snapshot = _snapshot;
        return snapshot is not null;
    }

    public void Store(RecommendationBatchUndoSnapshot snapshot) => _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));

    public bool InvalidateIfIncludes(string? stableId)
    {
        if (string.IsNullOrWhiteSpace(stableId) || _snapshot is null ||
            !_snapshot.Groups.Any(group => string.Equals(group.GroupStableId, stableId, StringComparison.Ordinal)))
        {
            return false;
        }

        _snapshot = null;
        return true;
    }

    public void Clear() => _snapshot = null;
}
