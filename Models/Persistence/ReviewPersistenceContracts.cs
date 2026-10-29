using DriveDuplicateFinder.Models;
using System.Security.Cryptography;
using System.Text;

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

/// <summary>
/// Exact local binding for one future single-candidate trial. It contains no OAuth material.
/// Production currently has no issuance path; the only issuer is synthetic test setup.
/// </summary>
public sealed record PagedCleanupTrialAuthorization(
    string PermitId,
    string AccountKey,
    string ScopeKey,
    string ScanId,
    string GroupId,
    long GroupSizeBytes,
    string NormalizedChecksum,
    string CandidateFileId,
    long CandidateSizeBytes,
    DateTimeOffset ConfirmedAtUtc,
    string KeepSetSha256)
{
    public static string ComputeKeepSetSha256(IEnumerable<string> keepIds)
    {
        ArgumentNullException.ThrowIfNull(keepIds);
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            foreach (string id in keepIds.OrderBy(value => value, StringComparer.Ordinal))
            {
                byte[] bytes = Encoding.UTF8.GetBytes(id);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }
        return Convert.ToHexString(SHA256.HashData(stream.ToArray()));
    }
}
