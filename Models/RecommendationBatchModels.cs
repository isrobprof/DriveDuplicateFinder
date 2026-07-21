namespace DriveDuplicateFinder.Models;

public enum RecommendationBatchExclusionReason
{
    GroupNotFound,
    TooFewFiles,
    MissingRecommendation,
    InvalidRecommendation,
    ReviewedWithoutCleanup,
    ResultsObsolete,
    UnsafeProposal
}

public sealed class RecommendationBatchGroupPreview
{
    public required string GroupStableId { get; init; }

    public required string DisplayName { get; init; }

    public required ApplyRecommendationPreview IndividualPreview { get; init; }

    public required RecommendationApplicationSnapshot StateAtPreview { get; init; }

    public required string ExpectedStatusText { get; init; }
}

public sealed class RecommendationBatchExcludedGroup
{
    public required string GroupStableId { get; init; }

    public required string DisplayName { get; init; }

    public required RecommendationBatchExclusionReason Reason { get; init; }

    public required string Detail { get; init; }
}

public sealed class RecommendationBatchPreview
{
    public IReadOnlyList<RecommendationBatchGroupPreview> ApplicableGroups { get; init; } = Array.Empty<RecommendationBatchGroupPreview>();

    public IReadOnlyList<RecommendationBatchExcludedGroup> ExcludedGroups { get; init; } = Array.Empty<RecommendationBatchExcludedGroup>();

    public int SelectedGroupCount { get; init; }

    public int ApplicableGroupCount => ApplicableGroups.Count;

    public int ExcludedGroupCount => ExcludedGroups.Count;

    public int KeepFileCount => ApplicableGroups.Count;

    public int CandidateFileCount => ApplicableGroups.Sum(group => group.IndividualPreview.CandidateFiles.Count);

    public int SkippedFileCount => ApplicableGroups.Sum(group => group.IndividualPreview.SkippedFiles.Count);

    public long CandidateBytes => ApplicableGroups.Sum(group => group.IndividualPreview.CandidateBytes);

    public int GroupsReplacingExistingDecisions => ApplicableGroups.Count(group => group.IndividualPreview.ReplacesExistingDecisions);

    public int GroupsExpectedReadyForCleanup => ApplicableGroups.Count(group => group.IndividualPreview.ExpectedStatus == DuplicateGroupReviewStatus.ReadyForCleanup);

    public int GroupsExpectedPartiallyReviewed => ApplicableGroups.Count(group => group.IndividualPreview.ExpectedStatus == DuplicateGroupReviewStatus.PartiallyReviewed);
}

public sealed class RecommendationBatchUndoSnapshot
{
    public required Guid BatchId { get; init; }

    public required DateTimeOffset AppliedAtUtc { get; init; }

    public IReadOnlyList<RecommendationApplicationSnapshot> Groups { get; init; } = Array.Empty<RecommendationApplicationSnapshot>();
}

public sealed class RecommendationBatchApplyResult
{
    public required RecommendationBatchUndoSnapshot UndoSnapshot { get; init; }

    public required RecommendationBatchPreview Preview { get; init; }
}
