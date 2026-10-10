using System.Numerics;

namespace DriveDuplicateFinder.Models.Persistence;

/// <summary>Identifies one immutable inventory snapshot for paged duplicate reads.</summary>
public sealed record ScanInventoryIdentity(string AccountKey, string ScopeKey, string ScanId);

/// <summary>Stable group identity; it is independent of display order and group number.</summary>
public sealed record DuplicateGroupIdentity
{
    public DuplicateGroupIdentity(ScanInventoryIdentity inventory, long sizeBytes, string checksum)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.AccountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.ScopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.ScanId);
        ArgumentNullException.ThrowIfNull(checksum);
        if (sizeBytes < 0) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (string.IsNullOrWhiteSpace(checksum)) throw new ArgumentException("El checksum no puede estar vacío.", nameof(checksum));

        Inventory = inventory;
        SizeBytes = sizeBytes;
        NormalizedChecksum = checksum.ToUpperInvariant();
    }

    public ScanInventoryIdentity Inventory { get; }
    public long SizeBytes { get; }
    public string NormalizedChecksum { get; }
}

/// <summary>Small list projection. It does not contain the group's member collection.</summary>
public sealed record DuplicateGroupSummary(
    DuplicateGroupIdentity Identity,
    long MemberCount,
    BigInteger RecoverableBytes,
    string RepresentativeFileId,
    string RepresentativeName);

public sealed record DuplicateGroupSummaryPage(
    IReadOnlyList<DuplicateGroupSummary> Items,
    long TotalCount,
    int Offset,
    int PageSize)
{
    public bool HasMore => (long)Offset + Items.Count < TotalCount;
}

/// <summary>
/// A member page is deliberately not a DuplicateGroup. Callers may use it for display,
/// but must obtain every member and verify IsCompleteGroup before creating a cleanup review.
/// </summary>
public sealed class DuplicateGroupMembersPage
{
    internal DuplicateGroupMembersPage(
        DuplicateGroupIdentity identity,
        IReadOnlyList<DriveFileCacheRecord> items,
        long totalCount,
        int offset,
        int pageSize)
    {
        Identity = identity;
        Items = items;
        TotalCount = totalCount;
        Offset = offset;
        PageSize = pageSize;
    }

    public DuplicateGroupIdentity Identity { get; }
    public IReadOnlyList<DriveFileCacheRecord> Items { get; }
    public long TotalCount { get; }
    public int Offset { get; }
    public int PageSize { get; }
    public bool IsCompleteGroup => TotalCount >= 2 && Offset == 0 && Items.Count == TotalCount;
    public bool IsPartial => !IsCompleteGroup;
    public bool HasMore => (long)Offset + Items.Count < TotalCount;
}
