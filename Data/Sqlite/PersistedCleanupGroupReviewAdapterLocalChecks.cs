using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using DriveDuplicateFinder.Services;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite;

public sealed record PersistedCleanupGroupReviewAdapterCheckResult(
    string TemporaryDirectory,
    bool TemporaryDirectoryDeleted,
    bool NormalGroupMappedExactly,
    bool LargeGroupPagesAndPendingMappedExactly,
    bool MultipleKeepDecisionsPreserved,
    bool PreflightProjectionKeepsOnlyExplicitCandidates,
    bool ChangedConfirmationRejected,
    bool OldIncompleteAndWrongAccountRejected,
    bool WrongScopeRejected,
    bool ForeignFileIdRejected,
    bool OversizedGroupRejectedBeforeMemberRead,
    bool CancellationReturnsNoPartialReview,
    bool AdapterHasNoRemoteExecutionDependency);

/// <summary>Offline SQLite checks for adapting one confirmed group to the existing review contract.</summary>
public static class PersistedCleanupGroupReviewAdapterLocalChecks
{
    private const string NormalHash = "11111111111111111111111111111111";
    private const string LargeHash = "22222222222222222222222222222222";
    private const string OversizedHash = "33333333333333333333333333333333";
    private const string ForeignHash = "44444444444444444444444444444444";

    public static async Task<PersistedCleanupGroupReviewAdapterCheckResult> VerifyAsync(
        CancellationToken cancellationToken = default)
    {
        string root = Path.Combine(Path.GetTempPath(), "DriveDuplicateFinder", $"cleanup-adapter-check-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var factory = new SqliteConnectionFactory(new LocalDataPathService(root));
            await new SqliteDatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var sessions = new ScanSessionRepository(factory);
            var files = new DriveFileCacheRepository(factory);
            var reviews = new ReviewStateRepository(factory);
            var adapter = new PersistedCleanupGroupReviewAdapter(factory, new GoogleDriveFileService());

            var oldInventory = new ScanInventoryIdentity("adapter-account", "adapter-scope", "adapter-old");
            await CreateInventoryAsync(factory, sessions, files, oldInventory, cancellationToken,
                (120, "old-hash", ["old-keep", "old-candidate"]));
            var oldGroup = new DuplicateGroupIdentity(oldInventory, 120, "old-hash");
            await MarkAndConfirmAsync(reviews, oldGroup, ["old-keep"], ["old-candidate"], cancellationToken);

            var inventory = new ScanInventoryIdentity("adapter-account", "adapter-scope", "adapter-current");
            string[] largeIds = Enumerable.Range(0, 205).Select(index => $"large-{index:D4}").ToArray();
            string[] oversizedIds = Enumerable.Range(0, 2_001).Select(index => $"oversized-{index:D4}").ToArray();
            await CreateInventoryAsync(factory, sessions, files, inventory, cancellationToken,
                (100, NormalHash, ["normal-keep", "normal-candidate", "normal-pending"]),
                (200, LargeHash, largeIds),
                (300, OversizedHash, oversizedIds),
                (400, ForeignHash, ["foreign-keep", "foreign-candidate"]));

            var normal = new DuplicateGroupIdentity(inventory, 100, NormalHash);
            await MarkAndConfirmAsync(reviews, normal, ["normal-keep"], ["normal-candidate"], cancellationToken);
            var large = new DuplicateGroupIdentity(inventory, 200, LargeHash);
            await MarkAndConfirmAsync(reviews, large, ["large-0000", "large-0175"], ["large-0100", "large-0204"], cancellationToken);
            var oversized = new DuplicateGroupIdentity(inventory, 300, OversizedHash);
            await MarkAndConfirmAsync(reviews, oversized, ["oversized-0000"], ["oversized-0001"], cancellationToken);
            var foreign = new DuplicateGroupIdentity(inventory, 400, ForeignHash);
            await MarkAndConfirmAsync(reviews, foreign, ["foreign-keep"], ["foreign-candidate"], cancellationToken);

            PersistedCleanupGroupReview normalReview = await adapter.BuildAsync(normal, cancellationToken);
            bool normalGroupMappedExactly = normalReview.MemberCount == 3 &&
                normalReview.Review.Group.Files.Count == 3 &&
                normalReview.Review.Status == DuplicateGroupReviewStatus.PartiallyReviewed &&
                normalReview.KeepCount == 1 && normalReview.CandidateCount == 1 && normalReview.UndecidedCount == 1 &&
                normalReview.Review.DecisionsByFileId["normal-keep"] == DuplicateFileDecision.Keep &&
                normalReview.Review.DecisionsByFileId["normal-candidate"] == DuplicateFileDecision.CandidateForTrash &&
                normalReview.Review.DecisionsByFileId["normal-pending"] == DuplicateFileDecision.Undecided &&
                normalReview.Review.Group.Files.All(file => file.Path.StartsWith("Mi unidad/", StringComparison.Ordinal)) &&
                normalReview.RemotePreflightPending && !normalReview.ExecutionEnabled;

            PersistedCleanupGroupReview largeReview = await adapter.BuildAsync(large, cancellationToken);
            string[] adaptedCandidates = largeReview.Review.DecisionsByFileId
                .Where(pair => pair.Value == DuplicateFileDecision.CandidateForTrash)
                .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            string[] adaptedKeeps = largeReview.Review.DecisionsByFileId
                .Where(pair => pair.Value == DuplicateFileDecision.Keep)
                .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            bool largeGroupPagesAndPendingMappedExactly = largeReview.MemberCount == 205 &&
                largeReview.Review.Group.Files.Count == 205 &&
                adaptedCandidates.SequenceEqual(["large-0100", "large-0204"], StringComparer.Ordinal) &&
                largeReview.UndecidedCount == 201 &&
                !largeReview.Review.DecisionsByFileId.Values.Any(value => value is not DuplicateFileDecision.Keep and
                    not DuplicateFileDecision.CandidateForTrash and not DuplicateFileDecision.Undecided);
            bool multipleKeepDecisionsPreserved = adaptedKeeps.SequenceEqual(["large-0000", "large-0175"], StringComparer.Ordinal);
            DuplicateGroupReview largePreflightProjection = largeReview.CreateReadOnlyPreflightReview();
            string[] projectedCandidates = largePreflightProjection.DecisionsByFileId
                .Where(pair => pair.Value == DuplicateFileDecision.CandidateForTrash)
                .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
            bool preflightProjectionKeepsOnlyExplicitCandidates = largePreflightProjection.Status == DuplicateGroupReviewStatus.ReadyForCleanup &&
                largePreflightProjection.Group.Files.Count == 3 &&
                largePreflightProjection.DecisionsByFileId.Count == 3 &&
                largePreflightProjection.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Keep) == 1 &&
                projectedCandidates.SequenceEqual(["large-0100", "large-0204"], StringComparer.Ordinal) &&
                !largePreflightProjection.DecisionsByFileId.Values.Contains(DuplicateFileDecision.Undecided) &&
                !largePreflightProjection.DecisionsByFileId.ContainsKey("large-0175");

            bool changedConfirmationRejected = false;
            await reviews.SetExplicitDecisionAsync(normal, "normal-candidate", DuplicateFileDecision.Undecided, cancellationToken);
            try { _ = await adapter.BuildAsync(normal, cancellationToken); }
            catch (InvalidOperationException) { changedConfirmationRejected = true; }

            bool oldRejected = false;
            try { _ = await adapter.BuildAsync(oldGroup, cancellationToken); }
            catch (InvalidOperationException) { oldRejected = true; }
            bool wrongAccountRejected = false;
            var wrongAccount = new DuplicateGroupIdentity(
                new ScanInventoryIdentity("different-account", inventory.ScopeKey, inventory.ScanId), 100, NormalHash);
            try { _ = await adapter.BuildAsync(wrongAccount, cancellationToken); }
            catch (InvalidOperationException) { wrongAccountRejected = true; }
            bool foreignFileIdRejected = false;
            await using (SqliteConnection connection = await factory.OpenAsync(cancellationToken))
            await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
            {
                await using SqliteCommand command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE ReviewFileDecisions SET FileId='zz-foreign-id'
                    WHERE AccountKey=$account AND ScopeKey=$scope AND ScanId=$scan
                      AND SizeBytes=400 AND NormalizedChecksum=$checksum COLLATE BINARY AND FileId='foreign-candidate';
                    """;
                command.Parameters.AddWithValue("$account", inventory.AccountKey);
                command.Parameters.AddWithValue("$scope", inventory.ScopeKey);
                command.Parameters.AddWithValue("$scan", inventory.ScanId);
                command.Parameters.AddWithValue("$checksum", ForeignHash);
                await command.ExecuteNonQueryAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            try { _ = await adapter.BuildAsync(foreign, cancellationToken); }
            catch (InvalidOperationException) { foreignFileIdRejected = true; }

            bool oversizedRejectedBeforeMemberRead = false;
            try { _ = await adapter.BuildAsync(oversized, cancellationToken); }
            catch (CleanupGroupMemberLimitExceededException exception)
            {
                oversizedRejectedBeforeMemberRead = exception.MemberCount == 2_001 &&
                    exception.MaximumMembers == PersistedCleanupGroupReviewAdapter.MaximumMembersPerGroup;
            }

            bool wrongScopeRejected = false;
            var wrongScope = new DuplicateGroupIdentity(
                new ScanInventoryIdentity(inventory.AccountKey, "different-scope", inventory.ScanId), 100, NormalHash);
            try { _ = await adapter.BuildAsync(wrongScope, cancellationToken); }
            catch (InvalidOperationException) { wrongScopeRejected = true; }

            // Starting a newer Full scan invalidates the completed inventory. Do this last so
            // the earlier corruption and oversized-group checks exercise their intended paths.
            var incompleteInventory = new ScanInventoryIdentity(inventory.AccountKey, inventory.ScopeKey, "adapter-incomplete");
            await sessions.CreateAsync(new ScanSessionRecord
            {
                ScanId = incompleteInventory.ScanId,
                AccountKey = incompleteInventory.AccountKey,
                ScopeKey = incompleteInventory.ScopeKey,
                ScanType = ScanType.Full,
                Status = ScanStatus.Running,
                StartedAtUtc = DateTimeOffset.UtcNow,
                UpdatedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
            bool incompleteRejected = false;
            try
            {
                _ = await adapter.BuildAsync(new DuplicateGroupIdentity(incompleteInventory, 100, NormalHash), cancellationToken);
            }
            catch (InvalidOperationException) { incompleteRejected = true; }

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            bool cancellationReturnsNoPartialReview = false;
            try { _ = await adapter.BuildAsync(large, cancelled.Token); }
            catch (OperationCanceledException) { cancellationReturnsNoPartialReview = true; }

            bool adapterHasNoRemoteExecutionDependency = typeof(PersistedCleanupGroupReviewAdapter)
                .GetConstructors().SelectMany(constructor => constructor.GetParameters())
                .All(parameter => parameter.ParameterType != typeof(Google.Apis.Drive.v3.DriveService)) &&
                typeof(PersistedCleanupGroupReviewAdapter).GetMethods()
                    .Where(method => method.DeclaringType == typeof(PersistedCleanupGroupReviewAdapter))
                    .SelectMany(method => method.GetParameters())
                    .All(parameter => parameter.ParameterType != typeof(Google.Apis.Drive.v3.DriveService));

            Assert(normalGroupMappedExactly, "El grupo normal no se convirtió conservando membresía y decisiones exactas.");
            Assert(largeGroupPagesAndPendingMappedExactly && multipleKeepDecisionsPreserved && preflightProjectionKeepsOnlyExplicitCandidates,
                "El grupo grande no preservó los Keep múltiples o incluyó pendientes entre candidatos.");
            Assert(changedConfirmationRejected, "Se adaptó un grupo tras invalidarse la confirmación.");
            Assert(oldRejected && wrongAccountRejected && wrongScopeRejected && incompleteRejected,
                "Se aceptó una sesión antigua, incompleta o de otra cuenta.");
            Assert(foreignFileIdRejected, "Se aceptó una decisión con FileId ajeno al grupo.");
            Assert(oversizedRejectedBeforeMemberRead, "Un grupo mayor de 2.000 no se rechazó antes de leer miembros.");
            Assert(cancellationReturnsNoPartialReview, "La cancelación no detuvo la adaptación antes de producir un resultado.");
            Assert(adapterHasNoRemoteExecutionDependency, "El adaptador adquirió una dependencia de Drive remoto.");

            return new PersistedCleanupGroupReviewAdapterCheckResult(root, true, normalGroupMappedExactly,
                largeGroupPagesAndPendingMappedExactly, multipleKeepDecisionsPreserved,
                preflightProjectionKeepsOnlyExplicitCandidates, changedConfirmationRejected,
                oldRejected && wrongAccountRejected && incompleteRejected, wrongScopeRejected, foreignFileIdRejected,
                oversizedRejectedBeforeMemberRead, cancellationReturnsNoPartialReview, adapterHasNoRemoteExecutionDependency);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private static async Task CreateInventoryAsync(
        SqliteConnectionFactory factory,
        ScanSessionRepository sessions,
        DriveFileCacheRepository files,
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken,
        params (long Size, string Hash, string[] FileIds)[] groups)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await sessions.CreateAsync(new ScanSessionRecord
        {
            ScanId = inventory.ScanId,
            AccountKey = inventory.AccountKey,
            ScopeKey = inventory.ScopeKey,
            RootFolderId = "root",
            ScanType = ScanType.Full,
            Status = ScanStatus.Running,
            StartedAtUtc = now,
            UpdatedAtUtc = now
        }, cancellationToken);
        await using (SqliteConnection connection = await factory.OpenAsync(cancellationToken))
        await using (SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken))
        {
            foreach ((long size, string hash, string[] fileIds) in groups)
            {
                foreach (string fileId in fileIds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await DriveFileCacheRepository.UpsertAsync(connection, transaction, new DriveFileCacheRecord
                    {
                        FileId = fileId,
                        Name = $"{fileId}.pdf",
                        AccountKey = inventory.AccountKey,
                        ScopeKey = inventory.ScopeKey,
                        MimeType = "application/pdf",
                        SizeBytes = size,
                        Md5Checksum = hash,
                        ModifiedTimeUtc = now,
                        CreatedTimeUtc = now,
                        ParentIds = ["root"],
                        OwnedByMe = true,
                        CanTrash = true,
                        IsShared = false,
                        IsStarred = false,
                        LastSeenScanId = inventory.ScanId,
                        CachedAtUtc = now
                    }, cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        await sessions.UpdateStatusAsync(inventory.ScanId, ScanStatus.Completed, DateTimeOffset.UtcNow,
            cancellationToken: cancellationToken);
        await new ReviewStateRepository(factory).RegisterCompletedInventoryAsync(inventory, cancellationToken);
    }

    private static async Task MarkAndConfirmAsync(
        ReviewStateRepository reviews,
        DuplicateGroupIdentity identity,
        IReadOnlyList<string> keepIds,
        IReadOnlyList<string> candidateIds,
        CancellationToken cancellationToken)
    {
        await reviews.RegisterGroupAsync(identity, cancellationToken);
        foreach (string fileId in keepIds)
            await reviews.SetExplicitDecisionAsync(identity, fileId, DuplicateFileDecision.Keep, cancellationToken);
        foreach (string fileId in candidateIds)
            await reviews.SetExplicitDecisionAsync(identity, fileId, DuplicateFileDecision.CandidateForTrash, cancellationToken);
        await reviews.ConfirmGroupReviewAsync(identity, cancellationToken);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
