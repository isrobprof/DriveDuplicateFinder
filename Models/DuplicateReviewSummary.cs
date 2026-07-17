namespace DriveDuplicateFinder.Models;

public sealed class DuplicateReviewSummary
{
    public int TotalGroups { get; init; }

    public int PendingGroups { get; init; }

    public int PartiallyReviewedGroups { get; init; }

    public int ReadyForCleanupGroups { get; init; }

    public int ReviewedWithoutCleanupGroups { get; init; }

    public int FilesMarkedKeep { get; init; }

    public int CandidateFiles { get; init; }

    public long CandidateBytes { get; init; }

    public int SharedCandidateFiles { get; init; }

    public int StarredCandidateFiles { get; init; }

    public int UndecidedFilesInPartiallyReviewedGroups { get; init; }
}
