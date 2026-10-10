namespace DriveDuplicateFinder.Models.Persistence;

public enum ScanType { Full, Incremental }

public enum ScanStatus { Pending, Running, Paused, Completed, Cancelled, Failed, Abandoned }

public sealed record DriveFileCacheRecord
{
    public required string FileId { get; init; }
    public required string Name { get; init; }
    public string AccountKey { get; init; } = string.Empty;
    public string ScopeKey { get; init; } = string.Empty;
    public string? NormalizedName { get; init; }
    public string? Extension { get; init; }
    public string? MimeType { get; init; }
    public long? SizeBytes { get; init; }
    public string? Md5Checksum { get; init; }
    public long? Version { get; init; }
    public IReadOnlyList<string> OwnerNames { get; init; } = Array.Empty<string>();
    public DateTimeOffset? ModifiedTimeUtc { get; init; }
    public DateTimeOffset? CreatedTimeUtc { get; init; }
    public IReadOnlyList<string> ParentIds { get; init; } = Array.Empty<string>();
    public string? DriveId { get; init; }
    public bool? OwnedByMe { get; init; }
    public bool? CanTrash { get; init; }
    public bool? IsShared { get; init; }
    public bool? IsStarred { get; init; }
    public bool IsTrashed { get; init; }
    public bool IsRemoved { get; init; }
    public string? LastSeenScanId { get; init; }
    public DateTimeOffset? LastChangedAtUtc { get; init; }
    public DateTimeOffset CachedAtUtc { get; init; }
}

public sealed record ScanSessionRecord
{
    public required string ScanId { get; init; }
    public string AccountKey { get; init; } = string.Empty;
    public string ScopeKey { get; init; } = string.Empty;
    public string? RootFolderId { get; init; }
    public ScanType ScanType { get; init; }
    public ScanStatus Status { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public DateTimeOffset? CancelledAtUtc { get; init; }
    public DateTimeOffset? FailedAtUtc { get; init; }
    public string? LastError { get; init; }
    public long ProcessedItemCount { get; init; }
    public long ProcessedPageCount { get; init; }
    public long DiscoveredItemCount { get; init; }
    public long UpdatedItemCount { get; init; }
    public long RemovedItemCount { get; init; }
}

public sealed class ScanCheckpointRecord
{
    public required string ScanId { get; init; }
    public string? NextPageToken { get; init; }
    public string? ChangePageToken { get; init; }
    public string? NewStartPageToken { get; init; }
    public long LastCommittedPageNumber { get; init; }
    public long LastCommittedItemCount { get; init; }
    public DateTimeOffset LastCommittedAtUtc { get; init; }
}

public sealed class DriveChangeStateRecord
{
    public required string ScopeKey { get; init; }
    public string? StartPageToken { get; init; }
    public string? LastProcessedPageToken { get; init; }
    public string? NewStartPageToken { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
}
