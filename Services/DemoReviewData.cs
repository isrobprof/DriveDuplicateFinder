using DriveDuplicateFinder.Data.Sqlite;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;

namespace DriveDuplicateFinder.Services;

public sealed record DemoReviewSeed(string DataDirectory, ScanInventoryIdentity Inventory);

/// <summary>Creates synthetic review data exclusively beneath a unique system-temp directory.</summary>
public static class DemoReviewData
{
    private const string DemoAccountKey = "DEMO-ONLY-NOT-A-GOOGLE-ACCOUNT";
    private const string DemoScopeKey = "demo-review-only-v1";

    public static string CreateTemporaryDirectoryPath() =>
        Path.Combine(Path.GetTempPath(), "DriveDuplicateFinder", "demo-" + Guid.NewGuid().ToString("N"));

    public static async Task<DemoReviewSeed> CreateAsync(string? dataDirectory = null, CancellationToken cancellationToken = default)
    {
        dataDirectory ??= CreateTemporaryDirectoryPath();
        if (!IsIsolatedDemoDirectory(dataDirectory))
            throw new ArgumentException("La demostración solo puede usar una carpeta demo-* bajo el directorio temporal del sistema.", nameof(dataDirectory));
        try
        {
            var factory = new SqliteConnectionFactory(new LocalDataPathService(dataDirectory));
            await new SqliteDatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var sessions = new ScanSessionRepository(factory);
            var files = new DriveFileCacheRepository(factory);
            var reviews = new ReviewStateRepository(factory);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            string scanId = "demo-scan-" + Guid.NewGuid().ToString("N");
            var session = new ScanSessionRecord
            {
                ScanId = scanId,
                AccountKey = DemoAccountKey,
                ScopeKey = DemoScopeKey,
                RootFolderId = "demo-root",
                ScanType = ScanType.Full,
                Status = ScanStatus.Running,
                StartedAtUtc = now,
                UpdatedAtUtc = now
            };
            await sessions.CreateAsync(session, cancellationToken);

            var records = new List<DriveFileCacheRecord>(210);
            AddGroup(records, scanId, "large", count: 205, size: 1_572_864, checksum: "a4d1f7862a1b5c9e3f0a8b7d6c5e4f21", now);
            AddGroup(records, scanId, "photos", count: 3, size: 5_242_880, checksum: "b5e2a8973b2c6d0f4a1b9c8e7d6f5032", now.AddDays(-4));
            AddGroup(records, scanId, "archive", count: 2, size: 41_943_040, checksum: "c6f3b9084c3d7e1a5b2c0d9f8e7a6143", now.AddDays(-20));

            await using (var connection = await factory.OpenAsync(cancellationToken))
            await using (var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
            {
                foreach (DriveFileCacheRecord record in records)
                    await DriveFileCacheRepository.UpsertAsync(connection, transaction, record, cancellationToken);
                await ScanSessionRepository.CompleteFullScanAsync(
                    connection, transaction, scanId, DemoAccountKey, DemoScopeKey, now, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            var inventory = new ScanInventoryIdentity(DemoAccountKey, DemoScopeKey, scanId);
            await reviews.RegisterCompletedInventoryAsync(inventory, cancellationToken);
            return new DemoReviewSeed(dataDirectory, inventory);
        }
        catch
        {
            TryDelete(dataDirectory);
            throw;
        }
    }

    public static async Task<DemoReviewCheckResult> VerifyIsolationAsync(CancellationToken cancellationToken = default)
    {
        DemoReviewSeed seed = await CreateAsync(cancellationToken: cancellationToken);
        try
        {
            string personalDirectory = new LocalDataPathService().BaseDataDirectory;
            var service = new RecoverableFullScanService(new GoogleDriveFileService(), seed.DataDirectory);
            RecoverableFullScanResult opened = await service.OpenCompletedInventoryAsync(seed.Inventory, cancellationToken);
            var navigation = new List<DuplicateGroupIdentity>();
            foreach (int offset in new[] { 0, 1, 2, 1, 0 })
            {
                DuplicateGroupSummaryPage page = await service.GetDuplicateGroupSummariesPageAsync(seed.Inventory, offset, 1, cancellationToken);
                if (page.Items.Count != 1)
                    throw new InvalidOperationException($"La navegación demo no devolvió un grupo en offset {offset}.");
                navigation.Add(page.Items[0].Identity);
            }
            bool groupNavigationWorks = navigation[0] == navigation[4] && navigation[1] == navigation[3] &&
                navigation[0] != navigation[1] && navigation[1] != navigation[2];
            DuplicateGroupSummaryPage allGroups = await service.GetDuplicateGroupSummariesPageAsync(seed.Inventory, 0, 10, cancellationToken);
            DuplicateGroupSummary largeGroup = allGroups.Items.Single(item => item.MemberCount == 205);
            DuplicateGroupIdentity firstGroupIdentity = navigation[0];
            var reviewRepository = new ReviewStateRepository(new SqliteConnectionFactory(new LocalDataPathService(seed.DataDirectory)));
            await reviewRepository.RegisterGroupAsync(firstGroupIdentity, cancellationToken);
            DuplicateGroupMembersPage firstGroupMembers = await new DriveFileCacheRepository(
                new SqliteConnectionFactory(new LocalDataPathService(seed.DataDirectory)))
                .GetDuplicateGroupMembersPageAsync(firstGroupIdentity, 0, 1, cancellationToken);
            string retainedFileId = firstGroupMembers.Items.Single().FileId;
            await reviewRepository.SetExplicitDecisionAsync(firstGroupIdentity, retainedFileId, DuplicateFileDecision.Keep, cancellationToken);
            _ = await service.GetDuplicateGroupSummariesPageAsync(seed.Inventory, 1, 1, cancellationToken);
            _ = await service.GetDuplicateGroupSummariesPageAsync(seed.Inventory, 0, 1, cancellationToken);
            PersistedFileDecisionPage retainedDecision = await reviewRepository.GetDecisionsPageAsync(firstGroupIdentity, 0, 1, cancellationToken);
            bool decisionSurvivesGroupNavigation = retainedDecision.Items.Single().Decision == DuplicateFileDecision.Keep;
            var memberRepository = new DriveFileCacheRepository(new SqliteConnectionFactory(new LocalDataPathService(seed.DataDirectory)));
            var memberPageCounts = new List<int>();
            foreach (int offset in new[] { 0, 100, 200 })
            {
                DuplicateGroupMembersPage page = await memberRepository.GetDuplicateGroupMembersPageAsync(
                    largeGroup.Identity, offset, 100, cancellationToken);
                memberPageCounts.Add(page.Items.Count);
            }
            bool memberPaginationWorks = memberPageCounts.SequenceEqual(new[] { 100, 100, 5 });
            bool groupNavigationBoundariesWork =
                !PagedReadOnlyViewSafety.HasPreviousPage(0) && PagedReadOnlyViewSafety.HasNextPage(0, 1, 3) &&
                PagedReadOnlyViewSafety.HasPreviousPage(2) && !PagedReadOnlyViewSafety.HasNextPage(2, 1, 3);
            var check = new DemoReviewCheckResult(
                !string.Equals(Path.GetFullPath(seed.DataDirectory), Path.GetFullPath(personalDirectory), StringComparison.OrdinalIgnoreCase),
                seed.Inventory.AccountKey.StartsWith("DEMO-ONLY-", StringComparison.Ordinal),
                opened.Inventory == seed.Inventory,
                allGroups.TotalCount == 3 && allGroups.Items.Any(item => item.MemberCount == 205),
                groupNavigationWorks,
                memberPaginationWorks,
                groupNavigationBoundariesWork,
                decisionSurvivesGroupNavigation,
                !PagedReadOnlyViewSafety.LegacyActionsEnabled(pagedReadOnlyMode: false, demoMode: true));
            if (!check.UsesIsolatedTemporaryDirectory || !check.UsesSyntheticIdentity || !check.OpensCompletedInventory ||
                !check.SeedsMultipleGroupsIncludingLargeGroup || !check.GroupNavigationWorks ||
                !check.MemberPaginationWorks || !check.GroupNavigationBoundariesWork ||
                !check.DecisionSurvivesGroupNavigation || !check.LegacyActionsBlocked)
                throw new InvalidOperationException("La verificación de aislamiento o de datos de demostración no se completó correctamente.");
            return check;
        }
        finally
        {
            TryDelete(seed.DataDirectory);
        }
    }

    public static void TryDelete(string dataDirectory)
    {
        string fullPath = Path.GetFullPath(dataDirectory);
        if (IsIsolatedDemoDirectory(fullPath) && Directory.Exists(fullPath))
        {
            try { Directory.Delete(fullPath, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsIsolatedDemoDirectory(string dataDirectory)
    {
        string allowedRoot = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "DriveDuplicateFinder")) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(dataDirectory);
        return fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase) &&
            Path.GetFileName(fullPath).StartsWith("demo-", StringComparison.Ordinal);
    }

    private static void AddGroup(
        ICollection<DriveFileCacheRecord> records,
        string scanId,
        string groupName,
        int count,
        long size,
        string checksum,
        DateTimeOffset modifiedAt)
    {
        for (int i = 1; i <= count; i++)
        {
            string extension = groupName == "photos" ? ".jpg" : groupName == "archive" ? ".zip" : ".pdf";
            records.Add(new DriveFileCacheRecord
            {
                FileId = $"demo-{groupName}-{i:D3}",
                Name = groupName == "large" ? $"Manual de muestra {i:D3}{extension}" : $"{groupName}-{i:D2}{extension}",
                AccountKey = DemoAccountKey,
                ScopeKey = DemoScopeKey,
                NormalizedName = $"{groupName}-{i:D3}",
                Extension = extension,
                MimeType = "application/octet-stream",
                SizeBytes = size,
                Md5Checksum = checksum,
                ModifiedTimeUtc = modifiedAt.AddMinutes(-i),
                CreatedTimeUtc = modifiedAt.AddYears(-1).AddMinutes(-i),
                ParentIds = ["demo-root"],
                OwnedByMe = true,
                CanTrash = true,
                IsShared = false,
                IsStarred = false,
                LastSeenScanId = scanId,
                CachedAtUtc = modifiedAt
            });
        }
    }
}

public sealed record DemoReviewCheckResult(
    bool UsesIsolatedTemporaryDirectory,
    bool UsesSyntheticIdentity,
    bool OpensCompletedInventory,
    bool SeedsMultipleGroupsIncludingLargeGroup,
    bool GroupNavigationWorks,
    bool MemberPaginationWorks,
    bool GroupNavigationBoundariesWork,
    bool DecisionSurvivesGroupNavigation,
    bool LegacyActionsBlocked);
