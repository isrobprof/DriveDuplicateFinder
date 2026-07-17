namespace DriveDuplicateFinder.Models;

public sealed class DuplicateReviewState
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public DateTimeOffset SavedAtUtc { get; init; }

    public string? UserIdentifier { get; init; }

    public List<StoredDuplicateGroupReview> Groups { get; init; } = [];
}

public sealed class StoredDuplicateGroupReview
{
    public required string StableId { get; init; }

    public long FileSize { get; init; }

    public required string Md5Checksum { get; init; }

    public Dictionary<string, DuplicateFileDecision> DecisionsByFileId { get; init; } = new(StringComparer.Ordinal);

    public bool ReviewedWithoutCleanup { get; init; }

    public string? Notes { get; init; }

    public DateTimeOffset LastModifiedUtc { get; init; }
}
