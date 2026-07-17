namespace DriveDuplicateFinder.Models;

public sealed class DuplicateGroupReview
{
    public required string StableId { get; init; }

    public required DuplicateGroup Group { get; init; }

    public Dictionary<string, DuplicateFileDecision> DecisionsByFileId { get; } = new(StringComparer.Ordinal);

    public bool ReviewedWithoutCleanup { get; set; }

    public string? Notes { get; set; }

    public DateTimeOffset LastModifiedUtc { get; set; } = DateTimeOffset.UtcNow;

    public DuplicateGroupReviewStatus Status { get; internal set; }
}
