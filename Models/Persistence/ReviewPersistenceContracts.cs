using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Models.Persistence;

public sealed record ReviewGroupPersistenceState(
    DuplicateGroupIdentity Identity,
    long MemberCount,
    DuplicateGroupReviewStatus Status,
    bool ReviewedWithoutCleanup,
    string? Notes,
    bool IsSelected,
    bool IsReviewConfirmed,
    DateTimeOffset LastModifiedAtUtc);

public sealed record ReviewDecisionCounts(long MemberCount, long KeepCount, long CandidateCount, long UndecidedCount);

public sealed record PersistedFileDecision(string FileId, DuplicateFileDecision Decision);

public sealed record PersistedFileDecisionPage(
    DuplicateGroupIdentity Identity,
    IReadOnlyList<PersistedFileDecision> Items,
    long TotalCount,
    int Offset,
    int PageSize)
{
    public bool HasMore => (long)Offset + Items.Count < TotalCount;
}

public sealed record ReviewGroupSummary(
    DuplicateGroupSummary Group,
    DuplicateGroupReviewStatus Status,
    bool ReviewedWithoutCleanup,
    string? Notes,
    bool IsSelected,
    bool IsReviewConfirmed,
    DateTimeOffset? LastModifiedAtUtc);

public sealed record ReviewGroupSummaryPage(
    IReadOnlyList<ReviewGroupSummary> Items,
    long TotalCount,
    int Offset,
    int PageSize)
{
    public bool HasMore => (long)Offset + Items.Count < TotalCount;
}

public sealed record SelectedReviewGroupPage(
    IReadOnlyList<DuplicateGroupIdentity> Items,
    long TotalCount,
    int Offset,
    int PageSize)
{
    public bool HasMore => (long)Offset + Items.Count < TotalCount;
}

public sealed record PersistedDecisionUpdate(
    ReviewGroupPersistenceState State,
    string? AutomaticallyKeptFileId);
