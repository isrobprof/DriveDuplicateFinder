namespace DriveDuplicateFinder.Models;

public enum RecommendationSkipReason
{
    Shared,
    Starred,
    NotOwnedByUser,
    CannotTrash,
    SharedDrive,
    IncompletePath,
    InsufficientMetadata,
    MissingMd5,
    InvalidSize
}

public sealed class RecommendationSkippedFile
{
    public required DriveFileInfo File { get; init; }

    public IReadOnlyList<RecommendationSkipReason> Reasons { get; init; } = Array.Empty<RecommendationSkipReason>();
}

public sealed class ApplyRecommendationPreview
{
    public required string GroupStableId { get; init; }

    public required DriveFileInfo KeepFile { get; init; }

    public required KeepRecommendation Recommendation { get; init; }

    public IReadOnlyList<DriveFileInfo> CandidateFiles { get; init; } = Array.Empty<DriveFileInfo>();

    public IReadOnlyList<RecommendationSkippedFile> SkippedFiles { get; init; } = Array.Empty<RecommendationSkippedFile>();

    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    public long CandidateBytes { get; init; }

    public bool ReplacesExistingDecisions { get; init; }

    public DuplicateGroupReviewStatus ExpectedStatus { get; init; }
}

public sealed class RecommendationApplicationSnapshot
{
    public required string GroupStableId { get; init; }

    public required IReadOnlyDictionary<string, DuplicateFileDecision> DecisionsByFileId { get; init; }

    public required DuplicateGroupReviewStatus Status { get; init; }

    public bool ReviewedWithoutCleanup { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset LastModifiedUtc { get; init; }
}

public sealed class RecommendationApplicationResult
{
    public required RecommendationApplicationSnapshot PreviousState { get; init; }

    public required ApplyRecommendationPreview Preview { get; init; }
}
