using System.Security.Cryptography;
using System.Text;
using DriveDuplicateFinder.Data.Sqlite;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Google.Apis.Drive.v3;
using GoogleFile = Google.Apis.Drive.v3.Data.File;

namespace DriveDuplicateFinder.Services;

public sealed record FullScanPreparation(
    string AccountKey,
    string ScopeKey,
    string RootFolderId,
    ScanSessionRecord? IncompleteSession);

public sealed record RecoverableFullScanResult(
    ScanInventoryIdentity Inventory,
    DriveScanResult ScanResult,
    long DuplicateGroupCount);

public sealed record RecoverableGroupMembersDisplayPage(
    DuplicateGroupIdentity Identity,
    IReadOnlyList<DriveFileInfo> Items,
    long TotalCount,
    int Offset,
    int PageSize)
{
    public bool HasMore => (long)Offset + Items.Count < TotalCount;
}

public sealed class InvalidScanCheckpointException : Exception
{
    public InvalidScanCheckpointException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Persists each complete files.list page before advancing its resumable token.</summary>
public sealed class RecoverableFullScanService
{
    public const string ScopeKeyValue = "files.list|q=trashed=false|corpora=user|includeItemsFromAllDrives=true|supportsAllDrives=true";
    private const int FolderLookupBatchSize = 500;

    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly SqliteDatabaseInitializer _initializer;
    private readonly ScanSessionRepository _sessions;
    private readonly ScanCheckpointRepository _checkpoints;
    private readonly DriveFileCacheRepository _files;
    private readonly ReviewStateRepository _reviewStates;
    private readonly ScanPagePersistenceService _pagePersistence;
    private readonly GoogleDriveFileService _fileMapper;

    public RecoverableFullScanService(GoogleDriveFileService fileMapper, string? baseDataDirectory = null)
    {
        _fileMapper = fileMapper ?? throw new ArgumentNullException(nameof(fileMapper));
        _connectionFactory = new SqliteConnectionFactory(new LocalDataPathService(baseDataDirectory));
        _initializer = new SqliteDatabaseInitializer(_connectionFactory);
        _sessions = new ScanSessionRepository(_connectionFactory);
        _checkpoints = new ScanCheckpointRepository(_connectionFactory);
        _files = new DriveFileCacheRepository(_connectionFactory);
        _reviewStates = new ReviewStateRepository(_connectionFactory);
        _pagePersistence = new ScanPagePersistenceService(_connectionFactory);
    }

    public async Task<FullScanPreparation> PrepareAsync(DriveService driveService, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driveService);
        await _initializer.InitializeAsync(cancellationToken);

        var aboutRequest = driveService.About.Get();
        aboutRequest.Fields = "user(emailAddress)";
        var about = await aboutRequest.ExecuteAsync(cancellationToken);
        string email = about.User?.EmailAddress?.Trim().ToLowerInvariant() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(email))
        {
            throw new InvalidOperationException("Google Drive no devolvió una identidad verificable; la ruta paginada se detuvo. Si procede, selecciona explícitamente el escaneo heredado.");
        }

        string accountKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(email)));
        string? rootFolderId = await _fileMapper.GetMyDriveRootIdAsync(driveService, cancellationToken);
        if (string.IsNullOrWhiteSpace(rootFolderId))
        {
            throw new InvalidOperationException("No se pudo identificar la raíz de Mi unidad; la ruta paginada se detuvo. Si procede, selecciona explícitamente el escaneo heredado.");
        }

        ScanSessionRecord? incomplete = await _sessions.GetLatestIncompleteAsync(accountKey, ScopeKeyValue, cancellationToken);
        return new FullScanPreparation(accountKey, ScopeKeyValue, rootFolderId, incomplete);
    }

    public Task AbandonAsync(ScanSessionRecord session, CancellationToken cancellationToken = default) =>
        _sessions.UpdateStatusAsync(session.ScanId, ScanStatus.Abandoned, DateTimeOffset.UtcNow,
            "Descartada por el usuario antes de iniciar un nuevo escaneo completo.", cancellationToken);

    /// <summary>Opens the latest existing completed inventory without enumerating Drive files.</summary>
    public async Task<RecoverableFullScanResult> OpenLatestCompletedAsync(
        DriveService driveService,
        CancellationToken cancellationToken = default)
    {
        FullScanPreparation identity = await PrepareAsync(driveService, cancellationToken);
        ScanSessionRecord session = await _sessions.GetLatestCompletedFullAsync(
            identity.AccountKey, identity.ScopeKey, cancellationToken)
            ?? throw new InvalidOperationException("No existe un análisis Full completado para esta cuenta y alcance.");
        RecoverableFullScanResult result = await BuildCompletedResultAsync(session, cancellationToken);
        return result;
    }

    /// <summary>Opens a specifically identified local completed inventory without OAuth or Drive access.</summary>
    public async Task<RecoverableFullScanResult> OpenCompletedInventoryAsync(
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ScanSessionRecord session = await _sessions.GetByIdAsync(inventory.ScanId, cancellationToken)
            ?? throw new InvalidOperationException("No existe la sesión local solicitada.");
        if (session.ScanType != ScanType.Full || session.Status != ScanStatus.Completed ||
            !string.Equals(session.AccountKey, inventory.AccountKey, StringComparison.Ordinal) ||
            !string.Equals(session.ScopeKey, inventory.ScopeKey, StringComparison.Ordinal))
            throw new InvalidOperationException("La identidad no corresponde a un inventario Full completado.");
        return await BuildCompletedResultAsync(session, cancellationToken);
    }

    public async Task<DuplicateGroupSummaryPage> GetDuplicateGroupSummariesPageAsync(
        ScanInventoryIdentity inventory,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);
        DuplicateGroupSummaryPage page = await _files.GetDuplicateGroupSummariesPageAsync(
            inventory, offset, pageSize, cancellationToken);
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);
        return page;
    }

    public async Task<RecoverableGroupMembersDisplayPage> GetDuplicateGroupMembersDisplayPageAsync(
        DuplicateGroupIdentity identity,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await EnsureCurrentCompletedInventoryAsync(identity.Inventory, cancellationToken);
        DuplicateGroupMembersPage page = await _files.GetDuplicateGroupMembersPageAsync(
            identity, offset, pageSize, cancellationToken);
        if (page.Items.Count == 0 && page.TotalCount != 0)
            throw new InvalidOperationException("La página de miembros dejó de corresponder con el inventario activo.");

        ScanSessionRecord session = await _sessions.GetByIdAsync(identity.Inventory.ScanId, cancellationToken)
            ?? throw new InvalidOperationException("No se encontró la sesión asociada a la página de miembros.");
        var recordsNeededForPaths = new List<DriveFileCacheRecord>(page.Items.Count);
        recordsNeededForPaths.AddRange(page.Items);
        if (page.Items.Count > 0)
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            IReadOnlyList<DriveFileCacheRecord> folders = await LoadAncestorFoldersAsync(
                connection, session, page.Items, cancellationToken);
            recordsNeededForPaths.AddRange(folders);
        }

        IReadOnlyList<DriveFileInfo> mapped = _fileMapper.BuildResultFromCache(
            recordsNeededForPaths, session.RootFolderId, cancellationToken).ComparableFiles;
        Dictionary<string, DriveFileInfo> byId = mapped.ToDictionary(file => file.Id, StringComparer.Ordinal);
        DriveFileInfo[] items = page.Items.Select(record => byId.TryGetValue(record.FileId, out DriveFileInfo? file)
                ? file
                : throw new InvalidOperationException("No se pudieron reconstruir los metadatos visibles del miembro."))
            .ToArray();
        await EnsureCurrentCompletedInventoryAsync(identity.Inventory, cancellationToken);
        return new RecoverableGroupMembersDisplayPage(identity, Array.AsReadOnly(items), page.TotalCount, offset, pageSize);
    }

    public async Task<PagedCleanupPlanSummary> GetCleanupPlanSummaryAsync(
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);
        PagedCleanupPlanSummary summary = await _reviewStates.GetCleanupPlanSummaryAsync(inventory, cancellationToken);
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);
        return summary;
    }

    public async Task<PagedCleanupPlanDisplayPage> GetCleanupPlanDisplayPageAsync(
        ScanInventoryIdentity inventory,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);
        PagedCleanupPlanPage page = await _reviewStates.GetCleanupPlanPageAsync(inventory, offset, pageSize, cancellationToken);
        ScanSessionRecord session = await _sessions.GetByIdAsync(inventory.ScanId, cancellationToken)
            ?? throw new InvalidOperationException("No se encontró la sesión Full del plan local.");
        var records = page.Items.Select(item => item.File).ToList();
        if (records.Count > 0)
        {
            await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
            records.AddRange(await LoadAncestorFoldersAsync(connection, session, page.Items.Select(item => item.File).ToArray(), cancellationToken));
        }

        IReadOnlyList<DriveFileInfo> mapped = _fileMapper.BuildResultFromCache(records, session.RootFolderId, cancellationToken).ComparableFiles;
        Dictionary<string, DriveFileInfo> byId = mapped.ToDictionary(file => file.Id, StringComparer.Ordinal);
        PagedCleanupPlanDisplayEntry[] items = page.Items.Select(item =>
        {
            if (!byId.TryGetValue(item.File.FileId, out DriveFileInfo? file))
                throw new InvalidOperationException("No se pudieron reconstruir las rutas de todos los elementos del plan; vista previa bloqueada.");
            return new PagedCleanupPlanDisplayEntry(item.GroupIdentity, item.Decision, file.Id, file.Name, file.Path, item.GroupIdentity.SizeBytes);
        }).ToArray();
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);
        return new PagedCleanupPlanDisplayPage(page.Summary, Array.AsReadOnly(items), offset, pageSize);
    }

    public Task<RecoverableFullScanResult> RunAsync(
        DriveService driveService,
        FullScanPreparation preparation,
        ScanSessionRecord? resumeSession,
        IProgress<DriveScanProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        RunPreparedAsync(preparation, resumeSession,
            new GoogleDriveFullScanPageSource(driveService, preparation.AccountKey, preparation.ScopeKey),
            progress, cancellationToken);

    public async Task<RecoverableFullScanResult> RunPreparedAsync(
        FullScanPreparation preparation,
        ScanSessionRecord? resumeSession,
        IFullScanPageSource pageSource,
        IProgress<DriveScanProgress>? progress = null,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? beforePageCommitForChecks = null)
    {
        ArgumentNullException.ThrowIfNull(preparation);
        ArgumentNullException.ThrowIfNull(pageSource);
        if (resumeSession is not null &&
            (resumeSession.ScanType != ScanType.Full ||
             !string.Equals(resumeSession.AccountKey, preparation.AccountKey, StringComparison.Ordinal) ||
             !string.Equals(resumeSession.ScopeKey, preparation.ScopeKey, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("La sesión guardada pertenece a otra cuenta o alcance; no puede reanudarse.");
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        ScanSessionRecord session;
        if (resumeSession is null)
        {
            session = new ScanSessionRecord
            {
                ScanId = Guid.NewGuid().ToString("N"),
                ScanType = ScanType.Full,
                Status = ScanStatus.Running,
                StartedAtUtc = now,
                UpdatedAtUtc = now,
                AccountKey = preparation.AccountKey,
                ScopeKey = preparation.ScopeKey,
                RootFolderId = preparation.RootFolderId
            };
            await _sessions.CreateAsync(session, cancellationToken);
        }
        else
        {
            session = CopySession(resumeSession, ScanStatus.Running, now, null);
            await _sessions.UpdateStatusAsync(session.ScanId, ScanStatus.Running, now, null, cancellationToken);
        }

        ScanCheckpointRecord? checkpoint = await _checkpoints.GetByScanIdAsync(session.ScanId, cancellationToken);
        string? pageToken = checkpoint?.NextPageToken;
        if (checkpoint is { LastCommittedPageNumber: > 0, NextPageToken: null })
        {
            await CompleteAsync(session, preparation, cancellationToken);
            return await BuildCompletedResultAsync(session, cancellationToken);
        }

        long processedItems = session.ProcessedItemCount;
        long processedPages = session.ProcessedPageCount;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                FullScanPage page;
                try
                {
                    page = await pageSource.ReadPageAsync(pageToken, cancellationToken);
                }
                catch (InvalidScanCheckpointException exception)
                {
                    await _sessions.UpdateStatusAsync(session.ScanId, ScanStatus.Abandoned, DateTimeOffset.UtcNow,
                        $"El token de página guardado expiró o dejó de ser válido; hay que iniciar un escaneo completo nuevo. {exception.Message}", CancellationToken.None);
                    throw;
                }

                IReadOnlyList<DriveFileCacheRecord> pageRecords = page.Files
                    .Where(file => !string.IsNullOrWhiteSpace(file.FileId))
                    .Select(file => file with
                    {
                        AccountKey = preparation.AccountKey,
                        ScopeKey = preparation.ScopeKey,
                        LastSeenScanId = session.ScanId,
                        IsRemoved = false
                    })
                    .ToArray();

                processedItems += page.ItemsReturned;
                processedPages++;
                DateTimeOffset committedAt = DateTimeOffset.UtcNow;
                session = CopySession(session, ScanStatus.Running, committedAt, null) with
                {
                    ProcessedItemCount = processedItems,
                    ProcessedPageCount = processedPages,
                    DiscoveredItemCount = processedItems,
                    UpdatedItemCount = session.UpdatedItemCount + pageRecords.Count
                };
                var nextCheckpoint = new ScanCheckpointRecord
                {
                    ScanId = session.ScanId,
                    NextPageToken = page.NextPageToken,
                    LastCommittedPageNumber = processedPages,
                    LastCommittedItemCount = processedItems,
                    LastCommittedAtUtc = committedAt
                };
                await _pagePersistence.PersistPageAsync(new ScanPagePersistenceRequest
                {
                    Session = session,
                    Checkpoint = nextCheckpoint,
                    Files = pageRecords,
                    BeforeCommitAsync = beforePageCommitForChecks
                }, cancellationToken);

                progress?.Report(new DriveScanProgress(
                    $"Guardada página {processedPages:N0}: {processedItems:N0} elementos confirmados.",
                    checked((int)Math.Min(processedItems, int.MaxValue))));

                pageToken = page.NextPageToken;
                if (string.IsNullOrWhiteSpace(pageToken)) break;
            }

            await CompleteAsync(session, preparation, cancellationToken);
            return await BuildCompletedResultAsync(session, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await _sessions.UpdateStatusAsync(session.ScanId, ScanStatus.Cancelled, DateTimeOffset.UtcNow,
                "El usuario canceló el escaneo; se conserva la última página confirmada.", CancellationToken.None);
            throw;
        }
        catch (InvalidScanCheckpointException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await _sessions.UpdateStatusAsync(session.ScanId, ScanStatus.Paused, DateTimeOffset.UtcNow,
                exception.Message, CancellationToken.None);
            throw;
        }
    }

    private async Task CompleteAsync(ScanSessionRecord session, FullScanPreparation preparation, CancellationToken cancellationToken)
    {
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var transaction = (Microsoft.Data.Sqlite.SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            await ScanSessionRepository.CompleteFullScanAsync(
                connection, transaction, session.ScanId, preparation.AccountKey, preparation.ScopeKey,
                DateTimeOffset.UtcNow, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<RecoverableFullScanResult> BuildCompletedResultAsync(ScanSessionRecord session, CancellationToken cancellationToken)
    {
        var inventory = new ScanInventoryIdentity(session.AccountKey, session.ScopeKey, session.ScanId);
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);

        CompletedScanFileCounts counts;
        await using (Microsoft.Data.Sqlite.SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken))
        {
            counts = await DriveFileCacheRepository.GetCompletedScanFileCountsAsync(
                connection, session.ScanId, session.AccountKey, session.ScopeKey, cancellationToken);
        }
        DuplicateGroupSummaryPage firstPage = await _files.GetDuplicateGroupSummariesPageAsync(inventory, 0, 1, cancellationToken);
        await EnsureCurrentCompletedInventoryAsync(inventory, cancellationToken);

        var scanResult = new DriveScanResult
        {
            ItemsExamined = ToDisplayCount(counts.ItemsExamined),
            FilesWithoutMd5Ignored = ToDisplayCount(counts.FilesWithoutMd5Ignored),
            ComparableFilesCount = ToDisplayCount(counts.ComparableFiles),
            ComparableFiles = Array.Empty<DriveFileInfo>()
        };
        return new RecoverableFullScanResult(inventory, scanResult, firstPage.TotalCount);
    }

    private async Task EnsureCurrentCompletedInventoryAsync(
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        await using var connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*) FROM ScanSessions s
            WHERE s.ScanId=$scanId AND s.AccountKey=$accountKey AND s.ScopeKey=$scopeKey
              AND s.ScanType='Full' AND s.Status='Completed'
              AND s.rowid=(
                  SELECT MAX(newest.rowid) FROM ScanSessions newest
                  WHERE newest.AccountKey=$accountKey AND newest.ScopeKey=$scopeKey AND newest.ScanType='Full'
              );
            """;
        command.Parameters.AddWithValue("$scanId", inventory.ScanId);
        command.Parameters.AddWithValue("$accountKey", inventory.AccountKey);
        command.Parameters.AddWithValue("$scopeKey", inventory.ScopeKey);
        if (Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture) != 1)
            throw new InvalidOperationException("El inventario no es el Full completado más reciente de esta cuenta y alcance; la vista paginada queda bloqueada.");
    }

    private async Task<List<DriveFileCacheRecord>> LoadAncestorFoldersAsync(
        Microsoft.Data.Sqlite.SqliteConnection connection,
        ScanSessionRecord session,
        IReadOnlyCollection<DriveFileCacheRecord> memberRecords,
        CancellationToken cancellationToken)
    {
        var loadedFolderIds = new HashSet<string>(StringComparer.Ordinal);
        var queriedFolderIds = new HashSet<string>(StringComparer.Ordinal);
        var folders = new List<DriveFileCacheRecord>();
        var pendingIds = new Queue<string>(memberRecords.SelectMany(file => file.ParentIds)
            .Where(parentId => !string.IsNullOrWhiteSpace(parentId))
            .Distinct(StringComparer.Ordinal));

        while (pendingIds.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = new List<string>(FolderLookupBatchSize);
            while (pendingIds.Count > 0 && batch.Count < FolderLookupBatchSize)
            {
                string folderId = pendingIds.Dequeue();
                if (queriedFolderIds.Add(folderId)) batch.Add(folderId);
            }
            if (batch.Count == 0) continue;

            IReadOnlyList<DriveFileCacheRecord> fetched = await DriveFileCacheRepository.GetCompletedFoldersByIdsAsync(
                connection, session.ScanId, session.AccountKey, session.ScopeKey, batch, cancellationToken);
            foreach (DriveFileCacheRecord folder in fetched)
            {
                if (!loadedFolderIds.Add(folder.FileId)) continue;
                folders.Add(folder);
                foreach (string parentId in folder.ParentIds)
                {
                    if (!string.IsNullOrWhiteSpace(parentId) && !queriedFolderIds.Contains(parentId))
                    {
                        pendingIds.Enqueue(parentId);
                    }
                }
            }
        }

        return folders;
    }

    private static int ToDisplayCount(long value) => (int)Math.Clamp(value, 0, int.MaxValue);

    private static ScanSessionRecord CopySession(ScanSessionRecord source, ScanStatus status, DateTimeOffset updatedAtUtc, string? lastError) => new()
    {
        ScanId = source.ScanId,
        ScanType = source.ScanType,
        Status = status,
        StartedAtUtc = source.StartedAtUtc,
        UpdatedAtUtc = updatedAtUtc,
        CompletedAtUtc = source.CompletedAtUtc,
        CancelledAtUtc = source.CancelledAtUtc,
        FailedAtUtc = source.FailedAtUtc,
        LastError = lastError,
        ProcessedItemCount = source.ProcessedItemCount,
        ProcessedPageCount = source.ProcessedPageCount,
        DiscoveredItemCount = source.DiscoveredItemCount,
        UpdatedItemCount = source.UpdatedItemCount,
        RemovedItemCount = source.RemovedItemCount,
        AccountKey = source.AccountKey,
        ScopeKey = source.ScopeKey,
        RootFolderId = source.RootFolderId
    };
}
