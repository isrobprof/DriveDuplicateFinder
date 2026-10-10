using System.Net;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using DriveDuplicateFinder.Services;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite;

public sealed record RecoverableFullScanCheckResult(
    string TemporaryDirectory,
    bool TemporaryDirectoryDeleted,
    bool MultiplePagesPersisted,
    bool ResumeAfterConnectionRestart,
    bool MidPageCancellationRolledBack,
    bool RetryWasIdempotent,
    bool InvalidTokenAbandonedSession,
    bool PageTokenClassifierCasesPassed,
    bool UnclassifiedApiErrorRemainsResumable,
    bool AccountAndScopeMismatchRejected,
    bool IncompleteInventoryNotPublished,
    bool CompletedInventoryReconciledWithinScope,
    bool PagedDuplicateQueriesPassed,
    IReadOnlyList<int> MigrationVersions);

/// <summary>Offline deterministic checks for the recoverable full scan pipeline.</summary>
public static class RecoverableFullScanLocalChecks
{
    public static async Task<RecoverableFullScanCheckResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        VerifyPageTokenClassifier();
        string temp = Path.Combine(Path.GetTempPath(), "DriveDuplicateFinder", $"full-scan-check-{Guid.NewGuid():N}");
        RecoverableFullScanCheckResult? result = null;
        try
        {
            var factory = new SqliteConnectionFactory(new LocalDataPathService(temp));
            await new SqliteDatabaseInitializer(factory).InitializeAsync(cancellationToken);
            await new SqliteDatabaseInitializer(factory).InitializeAsync(cancellationToken);
            var files = new DriveFileCacheRepository(factory);
            var sessions = new ScanSessionRepository(factory);
            var checkpoints = new ScanCheckpointRepository(factory);
            var scanner = new RecoverableFullScanService(new GoogleDriveFileService(), temp);
            FullScanPreparation scopeA = Preparation("account-a", "scope-a");

            await files.UpsertAsync(File("stale-a", "stale.txt", scopeA, "old-scan"), cancellationToken);
            await files.UpsertAsync(File("other-scope", "other.txt", Preparation("account-a", "scope-other"), "old-other"), cancellationToken);

            using var cancellation = new CancellationTokenSource();
            var firstPageSource = new FakePageSource(
                (null, Page([File("page-a", "a.txt", scopeA, null), File("page-b", "b.txt", scopeA, null)], "page-two")),
                ("page-two", Page([File("page-c", "c.txt", scopeA, null)], null)));
            var cancelAfterCommittedPage = new SynchronousProgress<DriveScanProgress>(progress =>
            {
                if (progress.ProcessedItems >= 2) cancellation.Cancel();
            });

            bool cancelledAfterPage = false;
            try
            {
                await scanner.RunPreparedAsync(scopeA, null, firstPageSource, cancelAfterCommittedPage, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelledAfterPage = true;
            }
            Assert(cancelledAfterPage, "No se pudo cancelar después de confirmar la primera página.");

            ScanSessionRecord? interrupted = await new ScanSessionRepository(new SqliteConnectionFactory(new LocalDataPathService(temp)))
                .GetLatestIncompleteAsync(scopeA.AccountKey, scopeA.ScopeKey, cancellationToken);
            Assert(interrupted is { Status: ScanStatus.Cancelled, ProcessedPageCount: 1, ProcessedItemCount: 2 }, "La sesión cancelada no conservó el progreso confirmado.");
            ScanCheckpointRecord? savedCheckpoint = await new ScanCheckpointRepository(new SqliteConnectionFactory(new LocalDataPathService(temp)))
                .GetByScanIdAsync(interrupted!.ScanId, cancellationToken);
            Assert(savedCheckpoint?.NextPageToken == "page-two", "El token de página no coincide con la última transacción confirmada.");
            Assert((await files.GetForCompletedScanAsync(interrupted.ScanId, scopeA.AccountKey, scopeA.ScopeKey, cancellationToken)).Count == 0,
                "Se publicaron archivos de una sesión incompleta.");
            await using (SqliteConnection incompleteConnection = await factory.OpenAsync(cancellationToken))
            {
                incompleteConnection.CreateCollation("DOTNET_ORDINAL_IGNORE_CASE", (left, right) =>
                    StringComparer.OrdinalIgnoreCase.Compare(left, right));
                IReadOnlyList<ExactDuplicateGroupKey> incompleteGroups =
                    await DriveFileCacheRepository.GetExactDuplicateGroupKeysAsync(
                        incompleteConnection, interrupted.ScanId, scopeA.AccountKey, scopeA.ScopeKey, cancellationToken);
                Assert(incompleteGroups.Count == 0, "Una sesión incompleta produjo grupos exactos desde SQLite.");
            }

            var reopenedScanner = new RecoverableFullScanService(new GoogleDriveFileService(), temp);
            var resumedSource = new FakePageSource(
                ("page-two", Page([File("page-c", "c.txt", scopeA, null)], null)));
            RecoverableFullScanResult resumedResult = await reopenedScanner.RunPreparedAsync(scopeA, interrupted, resumedSource, cancellationToken: cancellationToken);
            Assert(resumedSource.RequestedTokens.SequenceEqual(["page-two"]), "La reanudación no utilizó el token persistido.");
            Assert(resumedResult.ScanResult.ComparableFilesCount == 3 && resumedResult.ScanResult.ItemsExamined == 3,
                "No se recuperaron todos los archivos comparables de las páginas completas.");
            Assert(resumedResult.ScanResult.ComparableFiles.Count == 0,
                "El resultado recuperable volvió a cargar todos los archivos comparables en memoria.");
            DuplicateGroupSummaryPage resumedGroups = await reopenedScanner.GetDuplicateGroupSummariesPageAsync(
                resumedResult.Inventory, 0, 50, cancellationToken);
            DuplicateGroupMembersPage resumedMembers = await files.GetDuplicateGroupMembersPageAsync(
                resumedGroups.Items.Single().Identity, 0, 100, cancellationToken);
            Assert(resumedGroups.TotalCount == 1 && resumedMembers.TotalCount == 3,
                "La consulta SQLite paginada no agrupó los miembros duplicados tras reanudar.");
            Assert(resumedSource.RequestCount == 1, "La reanudación volvió a solicitar páginas ya confirmadas.");

            DriveFileCacheRecord? stale = await files.GetByIdAsync("stale-a", cancellationToken);
            DriveFileCacheRecord? otherScope = await files.GetByIdAsync("other-scope", cancellationToken);
            Assert(stale?.IsRemoved == true && otherScope?.IsRemoved == false, "La reconciliación salió del alcance del inventario.");

            FullScanPreparation scopeB = Preparation("account-b", "scope-b");
            using var midPageCancellation = new CancellationTokenSource();
            bool midPageRollback = false;
            try
            {
                await reopenedScanner.RunPreparedAsync(
                    scopeB,
                    null,
                    new FakePageSource((null, Page([File("retry-id", "first.txt", scopeB, null)], "retry-page"))),
                    cancellationToken: midPageCancellation.Token,
                    beforePageCommitForChecks: _ =>
                    {
                        midPageCancellation.Cancel();
                        return Task.CompletedTask;
                    });
            }
            catch (OperationCanceledException)
            {
                midPageRollback = true;
            }
            Assert(midPageRollback, "La cancelación durante la escritura de página no se propagó.");
            ScanSessionRecord? retrySession = await sessions.GetLatestIncompleteAsync(scopeB.AccountKey, scopeB.ScopeKey, cancellationToken);
            Assert(retrySession is { ProcessedPageCount: 0, ProcessedItemCount: 0 }, "La cancelación a mitad de página cambió los contadores.");
            Assert(await files.GetByIdAsync("retry-id", cancellationToken) is null, "La cancelación a mitad de página dejó un archivo parcial.");
            Assert(await checkpoints.GetByScanIdAsync(retrySession!.ScanId, cancellationToken) is null, "La cancelación a mitad de página avanzó el checkpoint.");

            var retryScanner = new RecoverableFullScanService(new GoogleDriveFileService(), temp);
            RecoverableFullScanResult retryResult = await retryScanner.RunPreparedAsync(
                scopeB,
                retrySession,
                new FakePageSource(
                    (null, Page([File("retry-id", "updated.txt", scopeB, null)], "retry-page")),
                    ("retry-page", Page([File("retry-id", "updated.txt", scopeB, null)], null))),
                cancellationToken: cancellationToken);
            DuplicateGroupSummaryPage retryGroups = await retryScanner.GetDuplicateGroupSummariesPageAsync(
                retryResult.Inventory, 0, 50, cancellationToken);
            Assert(retryResult.ScanResult.ComparableFilesCount == 1 && retryGroups.TotalCount == 0,
                "Repetir la página no fue idempotente.");

            FullScanPreparation duplicateScope = Preparation("account-exact", "scope-exact");
            const string exactHashUpper = "ABCDEF0123456789ABCDEF0123456789";
            const string exactHashLower = "abcdef0123456789abcdef0123456789";
            DriveFileCacheRecord duplicateFolder = File("exact-folder", "Carpeta", duplicateScope, null) with
            {
                MimeType = "application/vnd.google-apps.folder",
                SizeBytes = null,
                Md5Checksum = null,
                ParentIds = ["root-id"]
            };
            DriveFileCacheRecord duplicateOne = File("exact-one", "uno.pdf", duplicateScope, null) with
            {
                SizeBytes = 100,
                Md5Checksum = exactHashUpper,
                ParentIds = ["exact-folder"]
            };
            DriveFileCacheRecord duplicateTwo = File("exact-two", "dos.pdf", duplicateScope, null) with
            {
                SizeBytes = 100,
                Md5Checksum = exactHashLower,
                ParentIds = ["exact-folder"]
            };
            DriveFileCacheRecord smallerDuplicateOne = File("small-one", "pequeno-uno.pdf", duplicateScope, null) with
            {
                SizeBytes = 50,
                Md5Checksum = "small-duplicate-hash"
            };
            DriveFileCacheRecord smallerDuplicateTwo = File("small-two", "pequeno-dos.pdf", duplicateScope, null) with
            {
                SizeBytes = 50,
                Md5Checksum = "small-duplicate-hash"
            };
            DriveFileCacheRecord sameSizeDifferentHash = File("different-hash", "otro.pdf", duplicateScope, null) with
            {
                SizeBytes = 100,
                Md5Checksum = "different-md5-value"
            };
            DriveFileCacheRecord workspaceFile = File("workspace-file", "documento-google", duplicateScope, null) with
            {
                MimeType = "application/vnd.google-apps.document",
                SizeBytes = 100,
                Md5Checksum = exactHashUpper
            };
            DriveFileCacheRecord negativeSizeFile = File("negative-size", "tamano-invalido.pdf", duplicateScope, null) with
            {
                SizeBytes = -1,
                Md5Checksum = exactHashUpper
            };
            DriveFileCacheRecord missingHash = File("missing-hash", "sin-md5.pdf", duplicateScope, null) with
            {
                SizeBytes = 100,
                Md5Checksum = null
            };
            DriveFileCacheRecord blankHash = File("blank-hash", "md5-blanco.pdf", duplicateScope, null) with
            {
                SizeBytes = 100,
                Md5Checksum = "   "
            };
            DriveFileCacheRecord secondBlankHash = File("blank-hash-2", "md5-blanco-2.pdf", duplicateScope, null) with
            {
                SizeBytes = 100,
                Md5Checksum = "   "
            };
            RecoverableFullScanResult exactScan = await retryScanner.RunPreparedAsync(
                duplicateScope,
                null,
                new FakePageSource((null, Page([
                    duplicateFolder, duplicateOne, duplicateTwo, smallerDuplicateOne, smallerDuplicateTwo,
                    sameSizeDifferentHash, workspaceFile, negativeSizeFile, missingHash, blankHash, secondBlankHash
                ], null))),
                cancellationToken: cancellationToken);
            Assert(exactScan.ScanResult.ItemsExamined == 11 && exactScan.ScanResult.ComparableFilesCount == 5 &&
                exactScan.ScanResult.FilesWithoutMd5Ignored == 4,
                "Los contadores del inventario completo no coinciden con los filtros originales.");
            Assert(exactScan.ScanResult.ComparableFiles.Count == 0,
                "El análisis SQLite materializó la lista completa de archivos comparables.");
            DuplicateGroupSummaryPage exactGroups = await retryScanner.GetDuplicateGroupSummariesPageAsync(
                exactScan.Inventory, 0, 50, cancellationToken);
            RecoverableGroupMembersDisplayPage largeMembers = await retryScanner.GetDuplicateGroupMembersDisplayPageAsync(
                exactGroups.Items[0].Identity, 0, 100, cancellationToken);
            RecoverableGroupMembersDisplayPage smallMembers = await retryScanner.GetDuplicateGroupMembersDisplayPageAsync(
                exactGroups.Items[1].Identity, 0, 100, cancellationToken);
            using (var cancelledPageRequest = new CancellationTokenSource())
            {
                cancelledPageRequest.Cancel();
                bool cancellationObserved = false;
                try
                {
                    await retryScanner.GetDuplicateGroupSummariesPageAsync(exactScan.Inventory, 0, 50, cancelledPageRequest.Token);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved = true;
                }
                Assert(cancellationObserved, "La consulta paginada ignoró la cancelación solicitada.");
            }
            Assert(exactScan.DuplicateGroupCount == 2 && exactGroups.Items.Count == 2 && exactGroups.Items[0].Identity.SizeBytes == 100 &&
                largeMembers.Items.Select(file => file.Id).Order(StringComparer.Ordinal)
                    .SequenceEqual(["exact-one", "exact-two"]),
                "El agrupamiento exacto no preservó tamaño y checksum case-insensitive.");
            Assert(exactGroups.Items[1].Identity.SizeBytes == 50 &&
                smallMembers.Items.Select(file => file.Id).Order(StringComparer.Ordinal)
                    .SequenceEqual(["small-one", "small-two"]),
                "Los grupos no se ordenaron por bytes recuperables descendentes.");
            Assert(largeMembers.Items.All(file => file.Path.StartsWith("Mi unidad/Carpeta/", StringComparison.Ordinal)),
                "La carga de los ancestros de carpeta no conservó las rutas de los miembros.");

            IReadOnlyList<DuplicateGroup> legacyGroups = new DuplicateFinderService().FindDuplicates(
            [
                LegacyFile("exact-one", "uno.pdf", 100, exactHashUpper, "Mi unidad/Carpeta/uno.pdf"),
                LegacyFile("exact-two", "dos.pdf", 100, exactHashLower, "Mi unidad/Carpeta/dos.pdf"),
                LegacyFile("small-one", "pequeno-uno.pdf", 50, "small-duplicate-hash", "Mi unidad/pequeno-uno.pdf"),
                LegacyFile("small-two", "pequeno-dos.pdf", 50, "small-duplicate-hash", "Mi unidad/pequeno-dos.pdf"),
                LegacyFile("different-hash", "otro.pdf", 100, "different-md5-value", "Mi unidad/Carpeta/otro.pdf")
            ]);
            Assert(legacyGroups.Count == exactGroups.TotalCount &&
                legacyGroups.Select(group => group.Files.Select(file => file.Id).ToArray())
                    .Zip(new[] { largeMembers, smallMembers }, (legacy, current) =>
                        legacy.Order(StringComparer.Ordinal).SequenceEqual(current.Items.Select(file => file.Id).Order(StringComparer.Ordinal)))
                    .All(matches => matches),
                "La selección de miembros no coincide con DuplicateFinderService anterior.");

            await files.UpsertAsync(File("foreign-account-a", "externo-a.pdf",
                Preparation("account-foreign", duplicateScope.ScopeKey), exactScan.Inventory.ScanId) with
                { SizeBytes = 100, Md5Checksum = exactHashUpper }, cancellationToken);
            await files.UpsertAsync(File("foreign-account-b", "externo-b.pdf",
                Preparation("account-foreign", duplicateScope.ScopeKey), exactScan.Inventory.ScanId) with
                { SizeBytes = 100, Md5Checksum = exactHashLower }, cancellationToken);
            await files.UpsertAsync(File("foreign-scope-a", "otro-alcance-a.pdf",
                Preparation(duplicateScope.AccountKey, "scope-other"), exactScan.Inventory.ScanId) with
                { SizeBytes = 100, Md5Checksum = exactHashUpper }, cancellationToken);
            await files.UpsertAsync(File("foreign-scope-b", "otro-alcance-b.pdf",
                Preparation(duplicateScope.AccountKey, "scope-other"), exactScan.Inventory.ScanId) with
                { SizeBytes = 100, Md5Checksum = exactHashLower }, cancellationToken);
            await files.UpsertAsync(File("previous-session-a", "anterior-a.pdf", duplicateScope, "previous-scan") with
                { SizeBytes = 100, Md5Checksum = exactHashUpper }, cancellationToken);
            await files.UpsertAsync(File("previous-session-b", "anterior-b.pdf", duplicateScope, "previous-scan") with
                { SizeBytes = 100, Md5Checksum = exactHashLower }, cancellationToken);
            await using (SqliteConnection scopedConnection = await factory.OpenAsync(cancellationToken))
            {
                scopedConnection.CreateCollation("DOTNET_ORDINAL_IGNORE_CASE", (left, right) =>
                    StringComparer.OrdinalIgnoreCase.Compare(left, right));
                IReadOnlyList<ExactDuplicateGroupKey> scopedGroups =
                    await DriveFileCacheRepository.GetExactDuplicateGroupKeysAsync(
                        scopedConnection, exactScan.Inventory.ScanId, duplicateScope.AccountKey, duplicateScope.ScopeKey, cancellationToken);
                bool expectedGroups = scopedGroups.Count == 2 &&
                    scopedGroups.All(group => group.MemberCount == 2) &&
                    scopedGroups.Count(group => group.FileSize == 100 &&
                        string.Equals(group.Md5Checksum, exactHashUpper, StringComparison.OrdinalIgnoreCase)) == 1 &&
                    scopedGroups.Count(group => group.FileSize == 50 &&
                        string.Equals(group.Md5Checksum, "small-duplicate-hash", StringComparison.OrdinalIgnoreCase)) == 1;
                string actualGroups = string.Join("; ", scopedGroups.Select(group =>
                    $"size={group.FileSize}, md5={group.Md5Checksum}, members={group.MemberCount}"));
                Assert(expectedGroups,
                    $"Grupos esperados: dos grupos exactos de la sesión {exactScan.Inventory.ScanId} (tamaños 100 y 50, dos miembros cada uno). Reales: {actualGroups}.");

                Dictionary<int, List<DriveFileCacheRecord>> scopedMembers =
                    await DriveFileCacheRepository.GetExactDuplicateMembersAsync(
                        scopedConnection, exactScan.Inventory.ScanId, duplicateScope.AccountKey, duplicateScope.ScopeKey, scopedGroups, cancellationToken);
                bool expectedMembers = scopedMembers.Count == 2 && scopedMembers.All(pair =>
                {
                    if (pair.Key < 0 || pair.Key >= scopedGroups.Count)
                    {
                        return false;
                    }

                    ExactDuplicateGroupKey group = scopedGroups[pair.Key];
                    string[]? expectedIds = group.FileSize == 100 &&
                        string.Equals(group.Md5Checksum, exactHashUpper, StringComparison.OrdinalIgnoreCase)
                            ? ["exact-one", "exact-two"]
                            : group.FileSize == 50 &&
                              string.Equals(group.Md5Checksum, "small-duplicate-hash", StringComparison.OrdinalIgnoreCase)
                                ? ["small-one", "small-two"]
                                : null;
                    return expectedIds is not null && pair.Value.Count == 2 &&
                        pair.Value.Select(file => file.FileId).Order(StringComparer.Ordinal)
                            .SequenceEqual(expectedIds.Order(StringComparer.Ordinal)) &&
                        pair.Value.All(file => file.AccountKey == duplicateScope.AccountKey &&
                            file.ScopeKey == duplicateScope.ScopeKey && file.LastSeenScanId == exactScan.Inventory.ScanId);
                });
                string actualMembers = string.Join("; ", scopedMembers.OrderBy(pair => pair.Key).Select(pair =>
                    $"group={pair.Key}, files=[{string.Join(",", pair.Value.Select(file => file.FileId))}], " +
                    $"scope=[{string.Join(",", pair.Value.Select(file => $"{file.AccountKey}/{file.ScopeKey}/{file.LastSeenScanId}"))}]"));
                Assert(expectedMembers,
                    $"Los miembros deben ser únicamente exact-one/exact-two y small-one/small-two de {duplicateScope.AccountKey}/{duplicateScope.ScopeKey}/{exactScan.Inventory.ScanId}. Reales: {actualMembers}.");
            }

            bool pagedDuplicateQueriesPassed = await VerifyPagedDuplicateQueriesAsync(
                factory, files, sessions, retryScanner, cancellationToken);

            FullScanPreparation scopeC = Preparation("account-c", "scope-c");
            ScanSessionRecord invalidTokenSession = Session("invalid-token", scopeC, ScanStatus.Paused);
            await sessions.CreateAsync(invalidTokenSession, cancellationToken);
            await checkpoints.UpsertAsync(new ScanCheckpointRecord
            {
                ScanId = invalidTokenSession.ScanId,
                NextPageToken = "expired-token",
                LastCommittedPageNumber = 2,
                LastCommittedItemCount = 10,
                LastCommittedAtUtc = DateTimeOffset.UtcNow
            }, cancellationToken);
            bool invalidTokenDetected = false;
            try
            {
                await retryScanner.RunPreparedAsync(scopeC, invalidTokenSession,
                    new FakePageSource(("expired-token", null, true)), cancellationToken: cancellationToken);
            }
            catch (InvalidScanCheckpointException)
            {
                invalidTokenDetected = true;
            }
            Assert(invalidTokenDetected, "No se detectó el token inválido.");
            Assert(await sessions.GetLatestIncompleteAsync(scopeC.AccountKey, scopeC.ScopeKey, cancellationToken) is null,
                "La sesión con cursor inválido siguió disponible para reanudación.");
            Assert((await files.GetForCompletedScanAsync(invalidTokenSession.ScanId, scopeC.AccountKey, scopeC.ScopeKey, cancellationToken)).Count == 0,
                "El inventario del cursor inválido se publicó como completo.");

            FullScanPreparation scopeD = Preparation("account-d", "scope-d");
            var originalApiError = new InvalidOperationException("HTTP 400: invalid query parameter");
            bool originalErrorPropagated = false;
            try
            {
                await retryScanner.RunPreparedAsync(scopeD, null,
                    new FailingPageSource(originalApiError), cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException exception) when (ReferenceEquals(exception, originalApiError))
            {
                originalErrorPropagated = true;
            }
            ScanSessionRecord? recoverableAfterOtherError = await sessions.GetLatestIncompleteAsync(
                scopeD.AccountKey, scopeD.ScopeKey, cancellationToken);
            bool unclassifiedApiErrorRemainsResumable = originalErrorPropagated &&
                recoverableAfterOtherError?.Status == ScanStatus.Paused;
            Assert(unclassifiedApiErrorRemainsResumable,
                "Un error que no identifica claramente un pageToken no conservó la sesión recuperable.");

            var sourceMustNotRun = new FakePageSource((null, Page([], null)));
            bool mismatchRejected = false;
            try
            {
                await retryScanner.RunPreparedAsync(Preparation("account-other", "scope-c"), invalidTokenSession,
                    sourceMustNotRun, cancellationToken: cancellationToken);
            }
            catch (InvalidOperationException)
            {
                mismatchRejected = true;
            }
            Assert(mismatchRejected && sourceMustNotRun.RequestCount == 0, "Se permitió reanudar con otra cuenta o alcance.");

            IReadOnlyList<int> versions = await ReadMigrationVersionsAsync(factory, cancellationToken);
            Assert(versions.SequenceEqual([1, 2, 3, 4]), "Las migraciones 1 a 3 no se conservaron junto a la migración 4.");
            result = new RecoverableFullScanCheckResult(
                temp, false, true, true, midPageRollback, true, invalidTokenDetected, true,
                unclassifiedApiErrorRemainsResumable, mismatchRejected,
                true, stale?.IsRemoved == true && otherScope?.IsRemoved == false,
                pagedDuplicateQueriesPassed, versions);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true);
        }

        return result is null
            ? throw new InvalidOperationException("Las comprobaciones de escaneo recuperable no produjeron resultado.")
            : result with { TemporaryDirectoryDeleted = !Directory.Exists(temp) };
    }

    private static async Task<bool> VerifyPagedDuplicateQueriesAsync(
        SqliteConnectionFactory factory,
        DriveFileCacheRepository files,
        ScanSessionRepository sessions,
        RecoverableFullScanService scanner,
        CancellationToken cancellationToken)
    {
        FullScanPreparation scope = Preparation("paged-account", "paged-scope");
        var scanFiles = new List<DriveFileCacheRecord>();
        for (int index = 0; index < 17; index++)
        {
            scanFiles.Add(File($"many-{index:D2}", $"many-{index:D2}.pdf", scope, null) with
            {
                SizeBytes = 30,
                Md5Checksum = "large-group-hash"
            });
        }
        for (int index = 0; index < 3; index++)
        {
            scanFiles.Add(File($"tie-large-{index:D2}", $"tie-large-{index:D2}.pdf", scope, null) with
            {
                SizeBytes = 60,
                Md5Checksum = "tie-large-hash"
            });
        }
        for (int index = 0; index < 4; index++)
        {
            scanFiles.Add(File($"tie-small-{index:D2}", $"tie-small-{index:D2}.pdf", scope, null) with
            {
                SizeBytes = 40,
                Md5Checksum = "tie-small-hash"
            });
        }
        scanFiles.Add(File("sigma-lower", "sigma-lower.pdf", scope, null) with { SizeBytes = 20, Md5Checksum = "σ-checksum" });
        scanFiles.Add(File("sigma-final", "sigma-final.pdf", scope, null) with { SizeBytes = 20, Md5Checksum = "ς-checksum" });

        RecoverableFullScanResult scan = await scanner.RunPreparedAsync(
            scope,
            null,
            new FakePageSource((null, Page(scanFiles, null))),
            cancellationToken: cancellationToken);
        var inventory = scan.Inventory;

        Assert(StringComparer.OrdinalIgnoreCase.Equals("σ-checksum", "ς-checksum"),
            "El caso Unicode elegido no representa equivalencia OrdinalIgnoreCase de .NET.");
        await using (SqliteConnection connection = await factory.OpenAsync(cancellationToken))
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT $left = $right COLLATE NOCASE;";
            command.Parameters.AddWithValue("$left", "σ-checksum");
            command.Parameters.AddWithValue("$right", "ς-checksum");
            Assert(Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken)) == 0,
                "La prueba Unicode no demuestra que SQLite NOCASE difiera de OrdinalIgnoreCase.");
        }

        await files.UpsertAsync(File("paged-foreign-account-a", "foreign-account-a.pdf",
            Preparation("another-account", scope.ScopeKey), scan.Inventory.ScanId) with { SizeBytes = 20, Md5Checksum = "σ-checksum" }, cancellationToken);
        await files.UpsertAsync(File("paged-foreign-account-b", "foreign-account-b.pdf",
            Preparation("another-account", scope.ScopeKey), scan.Inventory.ScanId) with { SizeBytes = 20, Md5Checksum = "ς-checksum" }, cancellationToken);
        await files.UpsertAsync(File("paged-foreign-scope-a", "foreign-scope-a.pdf",
            Preparation(scope.AccountKey, "another-scope"), scan.Inventory.ScanId) with { SizeBytes = 20, Md5Checksum = "σ-checksum" }, cancellationToken);
        await files.UpsertAsync(File("paged-foreign-scope-b", "foreign-scope-b.pdf",
            Preparation(scope.AccountKey, "another-scope"), scan.Inventory.ScanId) with { SizeBytes = 20, Md5Checksum = "ς-checksum" }, cancellationToken);

        ScanSessionRecord oldCompletedScan = Session("paged-old-completed", scope, ScanStatus.Completed);
        await sessions.CreateAsync(oldCompletedScan, cancellationToken);
        await files.UpsertAsync(File("paged-old-session-a", "old-session-a.pdf", scope, oldCompletedScan.ScanId)
            with { SizeBytes = 20, Md5Checksum = "σ-checksum" }, cancellationToken);
        await files.UpsertAsync(File("paged-old-session-b", "old-session-b.pdf", scope, oldCompletedScan.ScanId)
            with { SizeBytes = 20, Md5Checksum = "ς-checksum" }, cancellationToken);

        var repository = new DriveFileCacheRepository(factory);
        DuplicateGroupSummaryPage first = await repository.GetDuplicateGroupSummariesPageAsync(
            inventory, 0, 2, cancellationToken);
        DuplicateGroupSummaryPage second = await repository.GetDuplicateGroupSummariesPageAsync(
            inventory, 2, 2, cancellationToken);
        DuplicateGroupSummaryPage afterEnd = await repository.GetDuplicateGroupSummariesPageAsync(
            inventory, 4, 2, cancellationToken);
        Assert(first.TotalCount == 4 && first.Offset == 0 && first.PageSize == 2 && first.Items.Count == 2 && first.HasMore,
            "La primera página de grupos no respeta el límite o el total.");
        Assert(second.TotalCount == 4 && second.Offset == 2 && second.Items.Count == 2 && !second.HasMore,
            "La segunda página de grupos no conserva el orden/offset.");
        Assert(afterEnd.TotalCount == 4 && afterEnd.Items.Count == 0 && !afterEnd.HasMore,
            "Un offset posterior al final devolvió grupos.");
        Assert(first.Items[0].Identity.SizeBytes == 30 && first.Items[0].MemberCount == 17 &&
            first.Items[0].RecoverableBytes == 480 && first.Items[1].Identity.SizeBytes == 60 &&
            first.Items[1].RecoverableBytes == 120 && second.Items[0].Identity.SizeBytes == 40 &&
            second.Items[0].RecoverableBytes == 120 && second.Items[1].Identity.SizeBytes == 20,
            "El orden por bytes recuperables o el desempate por tamaño no es determinista.");
        Assert(second.Items[1].Identity.NormalizedChecksum == "Σ-CHECKSUM",
            "La identidad del grupo no normalizó el checksum conforme a mayúsculas invariantes.");

        DuplicateGroupMembersPage memberPage1 = await repository.GetDuplicateGroupMembersPageAsync(
            first.Items[0].Identity, 0, 5, cancellationToken);
        DuplicateGroupMembersPage memberPage2 = await repository.GetDuplicateGroupMembersPageAsync(
            first.Items[0].Identity, 5, 5, cancellationToken);
        DuplicateGroupMembersPage memberPage3 = await repository.GetDuplicateGroupMembersPageAsync(
            first.Items[0].Identity, 10, 5, cancellationToken);
        DuplicateGroupMembersPage memberPage4 = await repository.GetDuplicateGroupMembersPageAsync(
            first.Items[0].Identity, 15, 5, cancellationToken);
        DuplicateGroupMembersPage allMembers = await repository.GetDuplicateGroupMembersPageAsync(
            first.Items[0].Identity, 0, 17, cancellationToken);
        Assert(memberPage1.TotalCount == 17 && memberPage1.Items.Count == 5 && memberPage1.IsPartial && memberPage1.HasMore &&
            memberPage2.Items.Count == 5 && memberPage2.IsPartial && memberPage2.HasMore &&
            memberPage3.Items.Count == 5 && memberPage3.IsPartial && memberPage3.HasMore &&
            memberPage4.Items.Count == 2 && memberPage4.IsPartial && !memberPage4.HasMore,
            "La paginación de miembros no marcó explícitamente las páginas parciales.");
        Assert(memberPage1.IsPartial && !PagedReadOnlyViewSafety.LegacyActionsEnabled(pagedReadOnlyMode: true),
            "Una página parcial no quedó aislada de las acciones de revisión y limpieza.");
        Assert(allMembers.IsCompleteGroup && allMembers.Items.Count == 17 &&
            allMembers.Items.Select(member => member.FileId).SequenceEqual(Enumerable.Range(0, 17).Select(index => $"many-{index:D2}")),
            "La página integral no devolvió todos los miembros en orden determinista.");

        DuplicateGroupSummary sigmaGroup = second.Items[1];
        DuplicateGroupMembersPage sigmaMembers = await repository.GetDuplicateGroupMembersPageAsync(
            sigmaGroup.Identity, 0, 10, cancellationToken);
        Assert(sigmaMembers.IsCompleteGroup && sigmaMembers.TotalCount == 2 &&
            sigmaMembers.Items.Select(member => member.FileId).SequenceEqual(["sigma-final", "sigma-lower"]),
            "La equivalencia Unicode o el aislamiento por cuenta, alcance y sesión no se respetó.");

        bool overLimitRejected = false;
        try
        {
            await repository.GetDuplicateGroupSummariesPageAsync(inventory, 0, 501, cancellationToken);
        }
        catch (ArgumentOutOfRangeException)
        {
            overLimitRejected = true;
        }
        Assert(overLimitRejected, "La consulta aceptó una página mayor que el máximo configurado.");

        ScanSessionRecord incomplete = Session("paged-incomplete", scope, ScanStatus.Running);
        await sessions.CreateAsync(incomplete, cancellationToken);
        await files.UpsertAsync(File("incomplete-a", "incomplete-a.pdf", scope, incomplete.ScanId)
            with { SizeBytes = 10, Md5Checksum = "incomplete-hash" }, cancellationToken);
        await files.UpsertAsync(File("incomplete-b", "incomplete-b.pdf", scope, incomplete.ScanId)
            with { SizeBytes = 10, Md5Checksum = "incomplete-hash" }, cancellationToken);
        DuplicateGroupSummaryPage incompleteGroups = await repository.GetDuplicateGroupSummariesPageAsync(
            new ScanInventoryIdentity(scope.AccountKey, scope.ScopeKey, incomplete.ScanId), 0, 10, cancellationToken);
        DuplicateGroupMembersPage incompleteMembers = await repository.GetDuplicateGroupMembersPageAsync(
            new DuplicateGroupIdentity(new ScanInventoryIdentity(scope.AccountKey, scope.ScopeKey, incomplete.ScanId), 10, "incomplete-hash"),
            0, 10, cancellationToken);
        Assert(incompleteGroups.TotalCount == 0 && incompleteGroups.Items.Count == 0 &&
            incompleteMembers.TotalCount == 0 && incompleteMembers.Items.Count == 0 && !incompleteMembers.IsCompleteGroup,
            "Una sesión Full incompleta publicó grupos o miembros.");
        bool priorCompletedInventoryRejectedAfterNewFullStarted = false;
        try
        {
            await scanner.GetDuplicateGroupSummariesPageAsync(inventory, 0, 2, cancellationToken);
        }
        catch (InvalidOperationException)
        {
            priorCompletedInventoryRejectedAfterNewFullStarted = true;
        }
        Assert(priorCompletedInventoryRejectedAfterNewFullStarted,
            "La ruta de presentación mantuvo visible un inventario invalidado por el inicio de otro Full.");

        ScanSessionRecord incremental = Session("paged-incremental", scope, ScanStatus.Completed) with { ScanType = ScanType.Incremental };
        await sessions.CreateAsync(incremental, cancellationToken);
        await files.UpsertAsync(File("incremental-a", "incremental-a.pdf", scope, incremental.ScanId)
            with { SizeBytes = 9, Md5Checksum = "incremental-hash" }, cancellationToken);
        await files.UpsertAsync(File("incremental-b", "incremental-b.pdf", scope, incremental.ScanId)
            with { SizeBytes = 9, Md5Checksum = "incremental-hash" }, cancellationToken);
        DuplicateGroupSummaryPage incrementalGroups = await repository.GetDuplicateGroupSummariesPageAsync(
            new ScanInventoryIdentity(scope.AccountKey, scope.ScopeKey, incremental.ScanId), 0, 10, cancellationToken);
        Assert(incrementalGroups.TotalCount == 0,
            "Una sesión Completed que no es Full publicó grupos duplicados.");

        return true;
    }

    private static async Task<IReadOnlyList<int>> ReadMigrationVersionsAsync(SqliteConnectionFactory factory, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await factory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM SchemaMigrations ORDER BY Version;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static void VerifyPageTokenClassifier()
    {
        PageTokenErrorDetail explicitLocation = new("invalid", "Invalid Value", "pageToken");
        PageTokenErrorDetail explicitReason = new("invalidPageToken", "The page token is expired", null);
        PageTokenErrorDetail unrelatedBadRequest = new("invalid", "Invalid Value", "q");

        Assert(GoogleDriveFullScanPageSource.IsInvalidPageToken(
            HttpStatusCode.BadRequest, null, [explicitLocation]),
            "No se reconoció el error estructurado de pageToken inválido.");
        Assert(GoogleDriveFullScanPageSource.IsInvalidPageToken(
            HttpStatusCode.BadRequest, null, [explicitReason]),
            "No se reconoció el motivo explícito de pageToken expirado.");
        Assert(!GoogleDriveFullScanPageSource.IsInvalidPageToken(
            HttpStatusCode.BadRequest, null, [unrelatedBadRequest]),
            "Un HTTP 400 ajeno al token se clasificó como pageToken inválido.");
        Assert(!GoogleDriveFullScanPageSource.IsInvalidPageToken(
            HttpStatusCode.BadRequest, "Invalid Value", null),
            "Un mensaje genérico sin error estructurado se clasificó como pageToken inválido.");
        Assert(GoogleDriveFullScanPageSource.IsInvalidPageToken(
            HttpStatusCode.BadRequest, "Invalid pageToken", null),
            "No se reconoció el mensaje explícito de pageToken inválido.");
        Assert(!GoogleDriveFullScanPageSource.IsInvalidPageToken(
            HttpStatusCode.Forbidden, null, [explicitLocation]),
            "Un HTTP 403 se clasificó como pageToken inválido.");
        Assert(!GoogleDriveFullScanPageSource.IsInvalidPageToken(
            HttpStatusCode.ServiceUnavailable, null,
            [new PageTokenErrorDetail("backendError", "Page token temporarily unavailable", "pageToken")]),
            "Un error transitorio se clasificó como pageToken inválido.");
    }

    private static FullScanPreparation Preparation(string account, string scope) =>
        new(account, scope, "root-id", null);

    private static FullScanPage Page(IReadOnlyCollection<DriveFileCacheRecord> files, string? nextToken) =>
        new(files, nextToken, files.Count);

    private static ScanSessionRecord Session(string id, FullScanPreparation preparation, ScanStatus status) => new()
    {
        ScanId = id,
        ScanType = ScanType.Full,
        Status = status,
        StartedAtUtc = DateTimeOffset.UtcNow,
        UpdatedAtUtc = DateTimeOffset.UtcNow,
        AccountKey = preparation.AccountKey,
        ScopeKey = preparation.ScopeKey,
        RootFolderId = preparation.RootFolderId
    };

    private static DriveFileCacheRecord File(string id, string name, FullScanPreparation preparation, string? scanId) => new()
    {
        FileId = id,
        Name = name,
        AccountKey = preparation.AccountKey,
        ScopeKey = preparation.ScopeKey,
        MimeType = "application/pdf",
        SizeBytes = 123,
        Md5Checksum = "abcdef0123456789abcdef0123456789",
        ParentIds = ["root-id"],
        OwnerNames = ["Owner"],
        Version = 1,
        OwnedByMe = true,
        CanTrash = true,
        LastSeenScanId = scanId,
        CachedAtUtc = DateTimeOffset.UtcNow
    };

    private static DriveFileInfo LegacyFile(string id, string name, long size, string checksum, string path) => new()
    {
        Id = id,
        Name = name,
        MimeType = "application/pdf",
        Size = size,
        Md5Checksum = checksum,
        Path = path
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class FakePageSource(params (string? Token, FullScanPage? Page, bool Invalid)[] responses) : IFullScanPageSource
    {
        private readonly Dictionary<string, (FullScanPage? Page, bool Invalid)> _responses =
            responses.ToDictionary(item => item.Token ?? "<first>", item => (item.Page, item.Invalid), StringComparer.Ordinal);

        public List<string?> RequestedTokens { get; } = [];
        public int RequestCount => RequestedTokens.Count;

        public FakePageSource(params (string? Token, FullScanPage? Page)[] responses)
            : this(responses.Select(item => (item.Token, item.Page, false)).ToArray()) { }

        public Task<FullScanPage> ReadPageAsync(string? pageToken, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedTokens.Add(pageToken);
            if (!_responses.TryGetValue(pageToken ?? "<first>", out var response))
                throw new InvalidOperationException($"No hay página simulada para el token '{pageToken}'.");
            if (response.Invalid)
                throw new InvalidScanCheckpointException("pageToken expired", new InvalidOperationException("invalid pageToken"));
            return Task.FromResult(response.Page ?? throw new InvalidOperationException("La página simulada no está configurada."));
        }
    }

    private sealed class SynchronousProgress<T>(Action<T> callback) : IProgress<T>
    {
        public void Report(T value) => callback(value);
    }

    private sealed class FailingPageSource(Exception exception) : IFullScanPageSource
    {
        public Task<FullScanPage> ReadPageAsync(string? pageToken, CancellationToken cancellationToken) =>
            Task.FromException<FullScanPage>(exception);
    }
}
