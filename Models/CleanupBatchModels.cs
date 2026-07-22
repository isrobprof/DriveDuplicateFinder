namespace DriveDuplicateFinder.Models;

public enum CleanupBatchExclusionReason
{
    GroupNotFound,
    ResultsAreStale,
    NotReadyForCleanup,
    NoKeepFile,
    MultipleKeepFiles,
    NoCandidates,
    ReviewedWithoutCleanup,
    CandidateLimitExceeded,
    PreflightFailed,
    MetadataChanged,
    UnsafeCandidate,
    InvalidGroupState
}

public enum CleanupBatchStatus
{
    Pending,
    Completed,
    CompletedWithExclusions,
    CancelledBeforeChanges,
    CancelledAfterChanges,
    FailedBeforeChanges,
    FailedAfterChanges
}

public enum CleanupBatchGroupStatus
{
    Pending,
    Completed,
    Partial,
    NotProcessed,
    FailedBeforeChanges
}

public sealed class CleanupBatchPreview
{
    public required Guid BatchId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public int SelectedGroupCount { get; init; }
    public IReadOnlyList<CleanupBatchGroupPlan> ApplicableGroups { get; init; } = Array.Empty<CleanupBatchGroupPlan>();
    public IReadOnlyList<CleanupBatchExcludedGroup> ExcludedGroups { get; init; } = Array.Empty<CleanupBatchExcludedGroup>();
    public int CandidateFileCount { get; init; }
    public long CandidateBytes { get; init; }
    public bool LimitsExceeded { get; init; }
    public string? LimitMessage { get; init; }
    public int ApplicableGroupCount => ApplicableGroups.Count;
    public int ExcludedGroupCount => ExcludedGroups.Count;
    public bool CanExecute => ApplicableGroupCount > 0 && !LimitsExceeded;
}

public sealed class CleanupBatchGroupPlan
{
    public required string StableGroupId { get; init; }
    public required string DisplayName { get; init; }
    public required CleanupFileSnapshot KeepFile { get; init; }
    public IReadOnlyList<CleanupFileSnapshot> Candidates { get; init; } = Array.Empty<CleanupFileSnapshot>();
    public required CleanupGroupSnapshot Snapshot { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}

public sealed class CleanupGroupSnapshot
{
    public required string StableGroupId { get; init; }
    public required DuplicateGroupReviewStatus Status { get; init; }
    public required bool ReviewedWithoutCleanup { get; init; }
    public required DateTimeOffset LastModifiedUtc { get; init; }
    public required IReadOnlyDictionary<string, DuplicateFileDecision> DecisionsByFileId { get; init; }
    public required CleanupFileSnapshot KeepFile { get; init; }
    public IReadOnlyList<CleanupFileSnapshot> CandidateFiles { get; init; } = Array.Empty<CleanupFileSnapshot>();
}

public sealed class CleanupBatchExcludedGroup
{
    public required string StableGroupId { get; init; }
    public required string DisplayName { get; init; }
    public required CleanupBatchExclusionReason Reason { get; init; }
    public required string Detail { get; init; }
}

public sealed class CleanupBatchGroupResult
{
    public required string StableGroupId { get; init; }
    public required string DisplayName { get; init; }
    public required CleanupBatchGroupStatus Status { get; set; }
    public IReadOnlyList<CleanupFileResult> FileResults { get; init; } = Array.Empty<CleanupFileResult>();
    public string? Detail { get; set; }
}

public sealed class CleanupFailureInfo
{
    public required string Stage { get; init; }
    public required string Message { get; init; }
    public string? StableGroupId { get; init; }
    public string? FileId { get; init; }
}

public sealed class CleanupBatchResult
{
    public required Guid BatchId { get; init; }
    public required CleanupBatchStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset FinishedAt { get; set; }
    public int PlannedGroupCount { get; init; }
    public int PlannedFileCount { get; init; }
    public int CompletedGroupCount { get; set; }
    public int PartiallyProcessedGroupCount { get; set; }
    public int UnprocessedGroupCount { get; set; }
    public int TrashedFileCount { get; set; }
    public int FailedFileCount { get; set; }
    public long TrashedBytes { get; set; }
    public bool DriveChanged { get; set; }
    public IReadOnlyList<CleanupBatchExcludedGroup> ExcludedGroups { get; init; } = Array.Empty<CleanupBatchExcludedGroup>();
    public List<CleanupBatchGroupResult> Groups { get; } = [];
    public CleanupFailureInfo? Failure { get; set; }
}

public sealed record CleanupBatchProgress(
    string Status,
    int CurrentGroup,
    int TotalGroups,
    int CurrentFile,
    int TotalFiles,
    string? FileName,
    string? FilePath,
    int TrashedFiles,
    long TrashedBytes);

public sealed class CleanupBatchRecord
{
    public const int CurrentFormatVersion = 1;
    public int FormatVersion { get; init; } = CurrentFormatVersion;
    public required Guid BatchId { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset? FinishedAtUtc { get; set; }
    public CleanupBatchStatus Status { get; set; } = CleanupBatchStatus.Pending;
    public CleanupBatchPreview? Preview { get; set; }
    public CleanupBatchResult? Result { get; set; }
}
