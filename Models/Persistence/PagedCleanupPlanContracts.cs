using System.Numerics;
using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Models.Persistence;

public sealed record PagedCleanupPlanSummary(
    ScanInventoryIdentity Inventory,
    long EligibleGroupCount,
    long CandidateFileCount,
    long FileEntryCount,
    BigInteger PotentiallyRecoverableBytes);

public sealed record PagedCleanupPlanEntry(
    DuplicateGroupIdentity GroupIdentity,
    DuplicateFileDecision Decision,
    DriveFileCacheRecord File);

public sealed record PagedCleanupPlanPage(
    PagedCleanupPlanSummary Summary,
    IReadOnlyList<PagedCleanupPlanEntry> Items,
    int Offset,
    int PageSize)
{
    public bool HasMore => (long)Offset + Items.Count < Summary.FileEntryCount;
}

public sealed record PagedCleanupPlanDisplayEntry(
    DuplicateGroupIdentity GroupIdentity,
    DuplicateFileDecision Decision,
    string FileId,
    string Name,
    string Path,
    long SizeBytes);

public sealed record PagedCleanupPlanDisplayPage(
    PagedCleanupPlanSummary Summary,
    IReadOnlyList<PagedCleanupPlanDisplayEntry> Items,
    int Offset,
    int PageSize)
{
    public bool HasMore => (long)Offset + Items.Count < Summary.FileEntryCount;
}
