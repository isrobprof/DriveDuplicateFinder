using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models.Persistence;
using DriveDuplicateFinder.Services;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite;

public sealed record SqliteInfrastructureCheckResult(
    string TemporaryDirectory,
    bool TemporaryDirectoryDeleted,
    IReadOnlyList<string> Tables,
    IReadOnlyList<string> Indexes,
    IReadOnlyList<int> MigrationVersions,
    long ForeignKeysPragma,
    string JournalMode,
    long SynchronousPragma,
    long BusyTimeoutPragma,
    long UpsertRowCount,
    long UpsertSizeBytes,
    ScanCheckpointRecord CheckpointBeforeRollback,
    ScanCheckpointRecord CheckpointAfterRollback,
    bool ExactCascadeVerified,
    bool PossibleCascadeVerified);

/// <summary>
/// Comprobaciones deterministas que solo se ejecutan explícitamente mediante
/// --verify-sqlite-infrastructure. Nunca se invocan desde la interfaz.
/// </summary>
public static class SqliteInfrastructureLocalChecks
{
    private static readonly string[] ExpectedTables =
    [
        "DriveFiles", "ScanSessions", "ScanCheckpoints", "DriveChangeState",
        "ExactDuplicateGroups", "ExactDuplicateMembers",
        "PossibleDuplicateGroups", "PossibleDuplicateMembers",
        "ReviewInventories", "ReviewGroupStates", "ReviewFileDecisions"
    ];

    private static readonly string[] ExpectedIndexes =
    [
        "IX_DriveFiles_Size_Md5", "IX_DriveFiles_NormalizedName_Size",
        "IX_DriveFiles_Size_ModifiedTime", "IX_DriveFiles_LastSeenScanId",
        "IX_DriveFiles_IsTrashed_IsRemoved", "IX_ScanSessions_Status",
        "IX_ExactDuplicateMembers_FileId", "IX_PossibleDuplicateMembers_FileId",
        "IX_ScanSessions_Account_Scope_Status", "IX_DriveFiles_Account_Scope_LastSeen",
        "IX_ReviewGroupStates_Inventory_Status", "IX_ReviewGroupStates_Inventory_Selected",
        "IX_ReviewFileDecisions_Group_Decision"
    ];

    public static async Task<SqliteInfrastructureCheckResult> VerifyAsync(CancellationToken cancellationToken = default)
    {
        string temporaryDirectory = Path.Combine(
            Path.GetTempPath(),
            "DriveDuplicateFinder",
            $"sqlite-check-{Guid.NewGuid():N}");
        SqliteInfrastructureCheckResult? result = null;

        try
        {
            var paths = new LocalDataPathService(temporaryDirectory);
            var factory = new SqliteConnectionFactory(paths);
            var initializer = new SqliteDatabaseInitializer(factory);
            var files = new DriveFileCacheRepository(factory);
            var sessions = new ScanSessionRepository(factory);
            var checkpoints = new ScanCheckpointRepository(factory);
            var pagePersistence = new ScanPagePersistenceService(factory);
            DateTimeOffset now = DateTimeOffset.UtcNow;

            await initializer.InitializeAsync(cancellationToken);
            await initializer.InitializeAsync(cancellationToken);
            SchemaSnapshot schema = await GetSchemaSnapshotAsync(factory, cancellationToken);
            Assert(schema.MigrationVersions.SequenceEqual([1, 2, 3, 4]), "Las migraciones 1 a 4 no se registraron exactamente una vez.");
            Assert(schema.Tables.SequenceEqual(ExpectedTables.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal), "Las tablas de las migraciones aplicadas no coinciden.");
            Assert(schema.Indexes.SequenceEqual(ExpectedIndexes.OrderBy(name => name, StringComparer.Ordinal), StringComparer.Ordinal), "Los índices de las migraciones aplicadas no coinciden.");
            Assert(schema.ForeignKeys == 1, "PRAGMA foreign_keys debe ser 1.");
            Assert(string.Equals(schema.JournalMode, "wal", StringComparison.OrdinalIgnoreCase), "El journal debe estar en WAL.");
            Assert(schema.Synchronous == 2, "PRAGMA synchronous debe conservar FULL (2).");
            Assert(schema.BusyTimeout == 10000, "PRAGMA busy_timeout debe ser 10000.");

            const long initialSize = 9_000_000_000_000_000_000;
            const long updatedSize = 8_000_000_000_000_000_000;
            await files.UpsertAsync(File("file-1", "uno.txt", initialSize, now, "scan-cache", []), cancellationToken);
            await files.UpsertAsync(File("file-1", "uno-renombrado.txt", updatedSize, now, "scan-cache", []), cancellationToken);
            DriveFileCacheRecord? updatedFile = await files.GetByIdAsync("file-1", cancellationToken);
            long upsertRowCount = await files.CountAsync(cancellationToken);
            Assert(upsertRowCount == 1, "El UPSERT debe mantener una única fila.");
            if (updatedFile is null) throw new InvalidOperationException("El UPSERT no devolvió el archivo guardado.");
            Assert(updatedFile.SizeBytes == updatedSize && updatedFile.Name == "uno-renombrado.txt", "El UPSERT no conservó los valores actualizados de 64 bits.");
            Assert(updatedFile.ParentIds.Count == 0 && updatedFile.OwnedByMe is null && updatedFile.CanTrash is null, "Los valores anulables o la lista vacía no se conservaron.");
            Assert(await GetParentIdsJsonAsync(factory, "file-1", cancellationToken) == "[]", "Una lista vacía debe persistirse como [].");

            await initializer.InitializeAsync(cancellationToken);
            Assert(await files.CountAsync(cancellationToken) == 1, "La inicialización idempotente no debe borrar datos existentes.");
            Assert((await GetSchemaSnapshotAsync(factory, cancellationToken)).MigrationVersions.SequenceEqual([1, 2, 3, 4]), "La inicialización idempotente duplicó una migración.");

            await VerifyIncompleteSessionsAsync(sessions, now, cancellationToken);

            ScanSessionRecord sessionBeforePage = Session("scan-page", ScanStatus.Running, now, processedItems: 100, processedPages: 3);
            await sessions.CreateAsync(sessionBeforePage, cancellationToken);
            ScanSessionRecord sessionAfterPage = Session("scan-page", ScanStatus.Running, now.AddMinutes(1), processedItems: 102, processedPages: 4);
            ScanCheckpointRecord checkpointBeforeRollback = new()
            {
                ScanId = "scan-page",
                NextPageToken = "next-success",
                ChangePageToken = "change-success",
                NewStartPageToken = "start-success",
                LastCommittedPageNumber = 4,
                LastCommittedItemCount = 102,
                LastCommittedAtUtc = now.AddMinutes(1)
            };
            await pagePersistence.PersistPageAsync(new ScanPagePersistenceRequest
            {
                Session = sessionAfterPage,
                Checkpoint = checkpointBeforeRollback,
                Files =
                [
                    File("page-1", "pagina-1.txt", 10, now, "scan-page", ["root"]),
                    File("page-2", "pagina-2.txt", 20, now, "scan-page", ["root"])
                ]
            }, cancellationToken);
            Assert(await files.GetByIdAsync("page-1", cancellationToken) is not null && await files.GetByIdAsync("page-2", cancellationToken) is not null, "La página válida no guardó sus archivos.");
            Assert((await sessions.GetByIdAsync("scan-page", cancellationToken))?.ProcessedItemCount == 102, "La página válida no actualizó los contadores.");
            Assert(CheckpointEquals(await checkpoints.GetByScanIdAsync("scan-page", cancellationToken), checkpointBeforeRollback), "La página válida no guardó el checkpoint.");

            using var cancellation = new CancellationTokenSource();
            bool rollbackObserved = false;
            try
            {
                await pagePersistence.PersistPageAsync(new ScanPagePersistenceRequest
                {
                    Session = Session("scan-page", ScanStatus.Running, now.AddMinutes(2), processedItems: 103, processedPages: 5),
                    Checkpoint = new ScanCheckpointRecord
                    {
                        ScanId = "scan-page",
                        NextPageToken = "must-not-advance",
                        ChangePageToken = "must-not-advance",
                        NewStartPageToken = "must-not-advance",
                        LastCommittedPageNumber = 5,
                        LastCommittedItemCount = 103,
                        LastCommittedAtUtc = now.AddMinutes(2)
                    },
                    Files = [File("must-rollback", "rollback.txt", 30, now, "scan-page", ["root"])],
                    BeforeCommitAsync = _ =>
                    {
                        cancellation.Cancel();
                        return Task.CompletedTask;
                    }
                }, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                rollbackObserved = true;
            }

            Assert(rollbackObserved, "La cancelación controlada antes del COMMIT no se propagó.");
            Assert(await files.GetByIdAsync("must-rollback", cancellationToken) is null, "El rollback dejó archivos parciales.");
            ScanSessionRecord? sessionAfterRollback = await sessions.GetByIdAsync("scan-page", cancellationToken);
            ScanCheckpointRecord? checkpointAfterRollback = await checkpoints.GetByScanIdAsync("scan-page", cancellationToken);
            Assert(sessionAfterRollback?.ProcessedItemCount == 102 && sessionAfterRollback.ProcessedPageCount == 4, "El rollback cambió los contadores de la sesión.");
            Assert(CheckpointEquals(checkpointAfterRollback, checkpointBeforeRollback), "El rollback avanzó el checkpoint.");

            (bool exactCascadeVerified, bool possibleCascadeVerified) = await VerifyCascadesAsync(factory, cancellationToken);

            var reopenedFactory = new SqliteConnectionFactory(new LocalDataPathService(temporaryDirectory));
            var reopenedFiles = new DriveFileCacheRepository(reopenedFactory);
            var reopenedSessions = new ScanSessionRepository(reopenedFactory);
            var reopenedCheckpoints = new ScanCheckpointRepository(reopenedFactory);
            await new SqliteDatabaseInitializer(reopenedFactory).InitializeAsync(cancellationToken);
            Assert((await reopenedFiles.GetByIdAsync("page-1", cancellationToken)) is not null, "Los archivos confirmados no persistieron tras reabrir la base.");
            Assert((await reopenedSessions.GetByIdAsync("scan-page", cancellationToken))?.ProcessedItemCount == 102, "La sesión confirmada no persistió tras reabrir la base.");
            Assert(CheckpointEquals(await reopenedCheckpoints.GetByScanIdAsync("scan-page", cancellationToken), checkpointBeforeRollback), "El checkpoint no persistió tras reabrir la base.");
            Assert(await reopenedFiles.GetByIdAsync("must-rollback", cancellationToken) is null, "Los datos de la transacción fallida aparecieron tras reabrir la base.");

            result = new SqliteInfrastructureCheckResult(
                temporaryDirectory,
                false,
                schema.Tables,
                schema.Indexes,
                schema.MigrationVersions,
                schema.ForeignKeys,
                schema.JournalMode,
                schema.Synchronous,
                schema.BusyTimeout,
                upsertRowCount,
                updatedSize,
                checkpointBeforeRollback,
                checkpointAfterRollback!,
                exactCascadeVerified,
                possibleCascadeVerified);
        }
        finally
        {
            // Las conexiones desechadas pueden permanecer en el pool del proveedor;
            // se vacía solo en esta comprobación temporal antes de borrar su directorio.
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(temporaryDirectory))
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
        }

        return result is null
            ? throw new InvalidOperationException("Las comprobaciones SQLite no produjeron resultado.")
            : result with { TemporaryDirectoryDeleted = !Directory.Exists(temporaryDirectory) };
    }

    private static async Task VerifyIncompleteSessionsAsync(ScanSessionRepository sessions, DateTimeOffset now, CancellationToken cancellationToken)
    {
        ScanStatus[] incompleteStatuses = [ScanStatus.Pending, ScanStatus.Running, ScanStatus.Paused, ScanStatus.Failed];
        for (int index = 0; index < incompleteStatuses.Length; index++)
        {
            ScanStatus status = incompleteStatuses[index];
            string scanId = $"incomplete-{status}";
            await sessions.CreateAsync(Session(scanId, status, now.AddMinutes(-10 + index)), cancellationToken);
            Assert((await sessions.GetLatestIncompleteAsync(cancellationToken))?.ScanId == scanId, $"No se recuperó la sesión incompleta {status}.");
            await sessions.UpdateStatusAsync(scanId, ScanStatus.Completed, now.AddMinutes(index), cancellationToken: cancellationToken);
            Assert((await sessions.GetLatestIncompleteAsync(cancellationToken)) is null, $"La sesión {status} completada siguió apareciendo como incompleta.");
        }
    }

    private static async Task<SchemaSnapshot> GetSchemaSnapshotAsync(SqliteConnectionFactory factory, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await factory.OpenAsync(cancellationToken);
        IReadOnlyList<string> tables = await ReadNamesAsync(connection, "table", ExpectedTables, cancellationToken);
        IReadOnlyList<string> indexes = await ReadNamesAsync(connection, "index", ExpectedIndexes, cancellationToken);
        IReadOnlyList<int> migrationVersions = await ReadMigrationVersionsAsync(connection, cancellationToken);
        return new SchemaSnapshot(
            tables,
            indexes,
            migrationVersions,
            await ReadLongAsync(connection, "PRAGMA foreign_keys;", cancellationToken),
            await ReadStringAsync(connection, "PRAGMA journal_mode;", cancellationToken),
            await ReadLongAsync(connection, "PRAGMA synchronous;", cancellationToken),
            await ReadLongAsync(connection, "PRAGMA busy_timeout;", cancellationToken));
    }

    private static async Task<IReadOnlyList<string>> ReadNamesAsync(SqliteConnection connection, string type, IEnumerable<string> expectedNames, CancellationToken cancellationToken)
    {
        string[] names = expectedNames.ToArray();
        await using SqliteCommand command = connection.CreateCommand();
        string[] parameters = names.Select((_, index) => $"$name{index}").ToArray();
        command.CommandText = $"SELECT name FROM sqlite_master WHERE type = $type AND name IN ({string.Join(", ", parameters)}) ORDER BY name;";
        command.Parameters.AddWithValue("$type", type);
        for (int index = 0; index < names.Length; index++) command.Parameters.AddWithValue(parameters[index], names[index]);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(reader.GetString(0));
        return result;
    }

    private static async Task<IReadOnlyList<int>> ReadMigrationVersionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM SchemaMigrations ORDER BY Version;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new List<int>();
        while (await reader.ReadAsync(cancellationToken)) versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static async Task<string?> GetParentIdsJsonAsync(SqliteConnectionFactory factory, string fileId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await factory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ParentIdsJson FROM DriveFiles WHERE FileId = $fileId;";
        command.Parameters.AddWithValue("$fileId", fileId);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is DBNull or null ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<(bool Exact, bool Possible)> VerifyCascadesAsync(SqliteConnectionFactory factory, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await factory.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await ExecuteAsync(connection, transaction, "INSERT INTO ExactDuplicateGroups (GroupId, StableKey, SizeBytes, Md5Checksum, MemberCount, CalculatedAtUtc) VALUES ('exact-group','exact-key',1,'md5',1,$utc);", cancellationToken, ("$utc", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow)));
        await ExecuteAsync(connection, transaction, "INSERT INTO ExactDuplicateMembers (GroupId, FileId, Position) VALUES ('exact-group','file-1',0);", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM ExactDuplicateGroups WHERE GroupId = 'exact-group';", cancellationToken);
        bool exact = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM ExactDuplicateMembers WHERE GroupId = 'exact-group';", cancellationToken) == 0;

        await ExecuteAsync(connection, transaction, "INSERT INTO PossibleDuplicateGroups (GroupId, StableKey, ConfidenceLevel, MatchReason, MemberCount, CalculatedAtUtc) VALUES ('possible-group','possible-key','medium','name-and-size',1,$utc);", cancellationToken, ("$utc", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow)));
        await ExecuteAsync(connection, transaction, "INSERT INTO PossibleDuplicateMembers (GroupId, FileId, Position) VALUES ('possible-group','file-1',0);", cancellationToken);
        await ExecuteAsync(connection, transaction, "DELETE FROM PossibleDuplicateGroups WHERE GroupId = 'possible-group';", cancellationToken);
        bool possible = await CountAsync(connection, transaction, "SELECT COUNT(*) FROM PossibleDuplicateMembers WHERE GroupId = 'possible-group';", cancellationToken) == 0;
        await transaction.CommitAsync(cancellationToken);
        Assert(exact && possible, "No funcionaron todas las cascadas de grupos de duplicados.");
        return (exact, possible);
    }

    private static async Task ExecuteAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken, params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters) command.Parameters.AddWithValue(name, value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> CountAsync(SqliteConnection connection, SqliteTransaction transaction, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ReadLongAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<string> ReadStringAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture)
            ?? throw new InvalidOperationException($"La consulta no devolvió valor: {sql}");
    }

    private static bool CheckpointEquals(ScanCheckpointRecord? actual, ScanCheckpointRecord expected) =>
        actual is not null && actual.ScanId == expected.ScanId && actual.NextPageToken == expected.NextPageToken &&
        actual.ChangePageToken == expected.ChangePageToken && actual.NewStartPageToken == expected.NewStartPageToken &&
        actual.LastCommittedPageNumber == expected.LastCommittedPageNumber && actual.LastCommittedItemCount == expected.LastCommittedItemCount &&
        actual.LastCommittedAtUtc == expected.LastCommittedAtUtc;

    private static ScanSessionRecord Session(string id, ScanStatus status, DateTimeOffset now, long processedItems = 0, long processedPages = 0) => new()
    {
        ScanId = id,
        ScanType = ScanType.Full,
        Status = status,
        StartedAtUtc = now,
        UpdatedAtUtc = now,
        ProcessedItemCount = processedItems,
        ProcessedPageCount = processedPages
    };

    private static DriveFileCacheRecord File(string id, string name, long size, DateTimeOffset now, string scanId, IReadOnlyList<string> parentIds) => new()
    {
        FileId = id,
        Name = name,
        NormalizedName = name.ToUpperInvariant(),
        SizeBytes = size,
        Md5Checksum = "0123456789abcdef0123456789abcdef",
        ParentIds = parentIds,
        LastSeenScanId = scanId,
        CachedAtUtc = now
    };

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record SchemaSnapshot(
        IReadOnlyList<string> Tables,
        IReadOnlyList<string> Indexes,
        IReadOnlyList<int> MigrationVersions,
        long ForeignKeys,
        string JournalMode,
        long Synchronous,
        long BusyTimeout);
}
