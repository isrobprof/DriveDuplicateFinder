namespace DriveDuplicateFinder.Models;

public enum CleanupOperationStatus
{
    Pending,
    PreflightFailed,
    Cancelled,
    Completed,
    Partial,
    Failed
}

public enum CleanupFileStatus
{
    Pending,
    Trashed,
    Failed,
    Cancelled,
    NotProcessed
}

public sealed class CleanupPlanValidationResult
{
    public bool IsAllowed => Reasons.Count == 0;

    public List<string> Reasons { get; } = [];
}

public sealed class CleanupEligibilityResult
{
    public required bool IsAllowed { get; init; }

    public IReadOnlyList<string> BlockingReasons { get; init; } = Array.Empty<string>();
}

public sealed record CleanupEligibilityContext(
    bool CleanupModeActive,
    bool SearchCompleted,
    bool ResultsAreObsolete,
    bool OperationInProgress);

public sealed class CleanupFileSnapshot
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string MimeType { get; init; }

    public long? Size { get; init; }

    public string? Md5Checksum { get; init; }

    public DateTimeOffset? ModifiedTime { get; init; }

    public IReadOnlyList<string> ParentIds { get; init; } = Array.Empty<string>();

    public bool? Trashed { get; init; }

    public bool? ExplicitlyTrashed { get; init; }

    public bool? OwnedByMe { get; init; }

    public bool? IsShared { get; init; }

    public bool? IsStarred { get; init; }

    public string? DriveId { get; init; }

    public long? Version { get; init; }

    public bool? CanTrash { get; init; }

    public string Path { get; init; } = string.Empty;
}

public sealed class CleanupPreflightResult
{
    public required DuplicateGroupReview Review { get; init; }

    public bool IsSuccessful { get; set; }

    public bool WasCancelled { get; set; }

    public CleanupPreflightFailureKind FailureKind { get; set; }

    public List<string> ValidationMessages { get; } = [];

    public CleanupFileSnapshot? KeepFile { get; set; }

    public List<CleanupFileSnapshot> CandidateFiles { get; } = [];
}

public enum CleanupPreflightFailureKind
{
    None,
    Cancelled,
    AuthenticationRequired,
    PermissionDenied,
    FileUnavailable,
    NetworkUnavailable,
    RemoteFailure
}

public sealed class CleanupFileResult
{
    public required CleanupFileSnapshot File { get; init; }

    public CleanupFileStatus Status { get; set; } = CleanupFileStatus.Pending;

    public string? Message { get; set; }

    public bool VerificationSucceeded { get; set; }

    public string? FailureStage { get; set; }

    public int? HttpStatusCode { get; set; }

    public string? HttpStatusDescription { get; set; }

    public int? GoogleErrorCode { get; set; }

    public string? FailureReason { get; set; }

    public string? SanitizedTechnicalMessage { get; set; }

    public List<CleanupApiErrorDetail> ApiErrors { get; } = [];
}

public sealed class CleanupApiErrorDetail
{
    public string? Reason { get; init; }

    public string? Domain { get; init; }

    public string? Location { get; init; }

    public string? LocationType { get; init; }

    public string? Message { get; init; }
}

public sealed class CleanupOperationRecord
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public required string OperationId { get; init; }

    public DateTimeOffset StartedAtUtc { get; init; }

    public DateTimeOffset? FinishedAtUtc { get; set; }

    public CleanupOperationStatus Status { get; set; } = CleanupOperationStatus.Pending;

    public required string StableGroupId { get; init; }

    public long FileSize { get; init; }

    public required string Md5Checksum { get; init; }

    public CleanupFileSnapshot? KeepFile { get; set; }

    public List<CleanupFileSnapshot> CandidateFiles { get; } = [];

    public List<string> Validations { get; } = [];

    public List<CleanupFileResult> FileResults { get; } = [];

    public string? SanitizedErrorMessage { get; set; }

    public int ProcessedFiles { get; set; }

    public long BytesSentToTrash { get; set; }
}

public sealed class CleanupOperationResult
{
    public required CleanupOperationRecord Record { get; init; }

    public bool WriteAttempted { get; init; }

    public bool ShouldInvalidateScan { get; init; }

    public bool NoFilesChanged { get; init; }
}
