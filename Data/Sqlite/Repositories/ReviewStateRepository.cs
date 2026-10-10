using System.Globalization;
using System.Numerics;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Repositories;

/// <summary>Persists review state only for explicitly registered, current completed inventories.</summary>
public sealed class ReviewStateRepository
{
    private const int MaxPageSize = 500;
    private const string WorkspaceMimeTypePrefix = "application/vnd.google-apps.";
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly DriveFileCacheRepository _files;

    public ReviewStateRepository(SqliteConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _files = new DriveFileCacheRepository(connectionFactory);
    }

    public async Task RegisterCompletedInventoryAsync(
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken = default)
    {
        ValidateInventory(inventory);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureLatestCompletedFullScanAsync(connection, transaction, inventory, cancellationToken);

        bool? existingIsObsolete = await GetInventoryObsoleteFlagAsync(connection, transaction, inventory, cancellationToken);
        if (existingIsObsolete == true)
        {
            throw new InvalidOperationException("El inventario de revisión ya fue invalidado y no puede reactivarse.");
        }

        await using (SqliteCommand obsolete = connection.CreateCommand())
        {
            obsolete.Transaction = transaction;
            obsolete.CommandText = """
                UPDATE ReviewInventories
                SET IsObsolete=1, InvalidatedAtUtc=$at
                WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId<>$scanId AND IsObsolete=0;
                """;
            AddInventoryParameters(obsolete, inventory);
            obsolete.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
            await obsolete.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (SqliteCommand insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO ReviewInventories(AccountKey, ScopeKey, ScanId, IsObsolete, RegisteredAtUtc)
                VALUES($accountKey, $scopeKey, $scanId, 0, $registeredAtUtc)
                ON CONFLICT(AccountKey, ScopeKey, ScanId) DO NOTHING;
                """;
            AddInventoryParameters(insert, inventory);
            insert.Parameters.AddWithValue("$registeredAtUtc", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await EnsureActiveInventoryAsync(connection, transaction, inventory, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>Invalidation is durable and intentionally does not delete historical decisions.</summary>
    public async Task InvalidateInventoryAsync(
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken = default)
    {
        ValidateInventory(inventory);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE ReviewInventories SET IsObsolete=1, InvalidatedAtUtc=$at
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId AND IsObsolete=0;
            """;
        AddInventoryParameters(command, inventory);
        command.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ReviewGroupPersistenceState> RegisterGroupAsync(
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateInventory(identity.Inventory);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        ReviewGroupPersistenceState state = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el estado del grupo registrado.");
        await transaction.CommitAsync(cancellationToken);
        return state;
    }

    public async Task<ReviewGroupPersistenceState?> GetGroupStateAsync(
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidateInventory(identity.Inventory);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await EnsureActiveInventoryAsync(connection, null, identity.Inventory, cancellationToken);
        ReviewGroupPersistenceState? state = await ReadGroupStateAsync(connection, null, identity, cancellationToken);
        if (state is not null)
            await ValidateRegisteredGroupCurrentAsync(connection, null, identity, cancellationToken);
        return state;
    }

    public async Task<ReviewGroupSummaryPage> GetReviewGroupSummaryPageAsync(
        ScanInventoryIdentity inventory,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(inventory, offset, pageSize);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await EnsureActiveInventoryAsync(connection, null, inventory, cancellationToken);

        DuplicateGroupSummaryPage groups = await _files.GetDuplicateGroupSummariesPageAsync(
            inventory, offset, pageSize, cancellationToken);
        if (groups.Items.Count == 0)
        {
            return new ReviewGroupSummaryPage(Array.Empty<ReviewGroupSummary>(), groups.TotalCount, offset, pageSize);
        }

        var values = new List<string>(groups.Items.Count);
        for (int index = 0; index < groups.Items.Count; index++)
        {
            values.Add($"({index}, $size{index}, $checksum{index})");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RequestedGroups(GroupOrdinal, SizeBytes, NormalizedChecksum) AS (VALUES {string.Join(",", values)})
            SELECT g.GroupOrdinal, s.ReviewStatus, s.ReviewedWithoutCleanup, s.Notes, s.IsSelected, s.IsReviewConfirmed, s.LastModifiedAtUtc
            FROM RequestedGroups g
            LEFT JOIN ReviewGroupStates s ON s.AccountKey=$accountKey AND s.ScopeKey=$scopeKey AND s.ScanId=$scanId
                AND s.SizeBytes=g.SizeBytes AND s.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY
            ORDER BY g.GroupOrdinal;
            """;
        AddInventoryParameters(command, inventory);
        for (int index = 0; index < groups.Items.Count; index++)
        {
            command.Parameters.AddWithValue($"$size{index}", groups.Items[index].Identity.SizeBytes);
            command.Parameters.AddWithValue($"$checksum{index}", groups.Items[index].Identity.NormalizedChecksum);
        }

        var statesByOrdinal = new Dictionary<int, (DuplicateGroupReviewStatus Status, bool Reviewed, string? Notes, bool Selected, bool Confirmed, DateTimeOffset? Modified)>();
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                int ordinal = reader.GetInt32(0);
                statesByOrdinal[ordinal] = reader.IsDBNull(1)
                    ? (DuplicateGroupReviewStatus.Pending, false, null, false, false, null)
                    : ((DuplicateGroupReviewStatus)reader.GetInt32(1), reader.GetInt64(2) != 0,
                        reader.IsDBNull(3) ? null : reader.GetString(3), reader.GetInt64(4) != 0,
                        reader.GetInt64(5) != 0,
                        reader.IsDBNull(6) ? null : SqlitePersistenceFormat.FromUtcText(reader.GetString(6)));
            }
        }

        ReviewGroupSummary[] summaries = groups.Items.Select((group, index) =>
        {
            var state = statesByOrdinal.GetValueOrDefault(index,
                (DuplicateGroupReviewStatus.Pending, false, null, false, false, (DateTimeOffset?)null));
            return new ReviewGroupSummary(group, state.Status, state.Reviewed, state.Notes, state.Selected, state.Confirmed, state.Modified);
        }).ToArray();
        return new ReviewGroupSummaryPage(Array.AsReadOnly(summaries), groups.TotalCount, offset, pageSize);
    }

    public async Task<PersistedFileDecisionPage> GetDecisionsPageAsync(
        DuplicateGroupIdentity identity,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidatePage(identity.Inventory, offset, pageSize);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await EnsureActiveInventoryAsync(connection, null, identity.Inventory, cancellationToken);
        await ValidateRegisteredGroupCurrentAsync(connection, null, identity, cancellationToken);

        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM ReviewFileDecisions
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(count, identity);
        long total = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using SqliteCommand page = connection.CreateCommand();
        page.CommandText = """
            SELECT FileId, Decision FROM ReviewFileDecisions
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY
            ORDER BY FileId COLLATE BINARY LIMIT $pageSize OFFSET $offset;
            """;
        AddGroupParameters(page, identity);
        AddPageParameters(page, offset, pageSize);
        var items = new List<PersistedFileDecision>(pageSize);
        await using (SqliteDataReader reader = await page.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                items.Add(new PersistedFileDecision(reader.GetString(0), (DuplicateFileDecision)reader.GetInt32(1)));
            }
        }

        return new PersistedFileDecisionPage(identity, Array.AsReadOnly(items.ToArray()), total, offset, pageSize);
    }

    public async Task<SelectedReviewGroupPage> GetSelectedGroupsPageAsync(
        ScanInventoryIdentity inventory,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(inventory, offset, pageSize);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await EnsureActiveInventoryAsync(connection, null, inventory, cancellationToken);

        await using SqliteCommand count = connection.CreateCommand();
        count.CommandText = """
            SELECT COUNT(*) FROM ReviewGroupStates
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId AND IsSelected=1;
            """;
        AddInventoryParameters(count, inventory);
        long total = (long)(await count.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using SqliteCommand page = connection.CreateCommand();
        page.CommandText = """
            SELECT SizeBytes, NormalizedChecksum FROM ReviewGroupStates
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId AND IsSelected=1
            ORDER BY SizeBytes DESC, NormalizedChecksum COLLATE BINARY
            LIMIT $pageSize OFFSET $offset;
            """;
        AddInventoryParameters(page, inventory);
        AddPageParameters(page, offset, pageSize);
        var identities = new List<DuplicateGroupIdentity>(pageSize);
        await using (SqliteDataReader reader = await page.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                identities.Add(new DuplicateGroupIdentity(inventory, reader.GetInt64(0), reader.GetString(1)));
            }
        }

        return new SelectedReviewGroupPage(Array.AsReadOnly(identities.ToArray()), total, offset, pageSize);
    }

    public async Task<PagedCleanupPlanSummary> GetCleanupPlanSummaryAsync(
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(inventory, 0, 1);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureActiveInventoryAsync(connection, transaction, inventory, cancellationToken);
        PagedCleanupPlanSummary summary = await ReadCleanupPlanSummaryAsync(connection, transaction, inventory, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return summary;
    }

    public async Task<PagedCleanupPlanPage> GetCleanupPlanPageAsync(
        ScanInventoryIdentity inventory,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePage(inventory, offset, pageSize);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureActiveInventoryAsync(connection, transaction, inventory, cancellationToken);
        PagedCleanupPlanSummary summary = await ReadCleanupPlanSummaryAsync(connection, transaction, inventory, cancellationToken);

        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT {DriveFileCacheRepository.SelectRecordColumns}, g.SizeBytes, g.NormalizedChecksum, d.Decision
            FROM ReviewGroupStates g
            JOIN ReviewFileDecisions d ON d.AccountKey=g.AccountKey AND d.ScopeKey=g.ScopeKey AND d.ScanId=g.ScanId
                AND d.SizeBytes=g.SizeBytes AND d.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY
            JOIN DriveFiles f ON f.FileId=d.FileId AND f.AccountKey=g.AccountKey AND f.ScopeKey=g.ScopeKey
            WHERE g.AccountKey=$accountKey AND g.ScopeKey=$scopeKey AND g.ScanId=$scanId
              AND g.IsReviewConfirmed=1 AND d.Decision IN ($keep, $candidate)
              AND f.LastSeenScanId=$scanId AND f.IsTrashed=0 AND f.IsRemoved=0
              AND f.SizeBytes=g.SizeBytes AND f.Md5Checksum IS NOT NULL
              AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
              AND substr(COALESCE(f.MimeType,''),1,length($workspacePrefix))<>$workspacePrefix COLLATE BINARY
              AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=g.NormalizedChecksum COLLATE DOTNET_ORDINAL_IGNORE_CASE
              AND EXISTS (SELECT 1 FROM ReviewFileDecisions k WHERE k.AccountKey=g.AccountKey AND k.ScopeKey=g.ScopeKey
                  AND k.ScanId=g.ScanId AND k.SizeBytes=g.SizeBytes AND k.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY
                  AND k.Decision=$keep)
              AND EXISTS (SELECT 1 FROM ReviewFileDecisions c WHERE c.AccountKey=g.AccountKey AND c.ScopeKey=g.ScopeKey
                  AND c.ScanId=g.ScanId AND c.SizeBytes=g.SizeBytes AND c.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY
                  AND c.Decision=$candidate)
            ORDER BY g.SizeBytes DESC, g.NormalizedChecksum COLLATE BINARY,
                     CASE d.Decision WHEN $candidate THEN 0 ELSE 1 END, d.FileId COLLATE BINARY
            LIMIT $pageSize OFFSET $offset;
            """;
        AddInventoryParameters(command, inventory);
        command.Parameters.AddWithValue("$keep", (int)DuplicateFileDecision.Keep);
        command.Parameters.AddWithValue("$candidate", (int)DuplicateFileDecision.CandidateForTrash);
        command.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        AddPageParameters(command, offset, pageSize);
        var items = new List<PagedCleanupPlanEntry>(pageSize);
        await using (SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var identity = new DuplicateGroupIdentity(inventory, reader.GetInt64(24), reader.GetString(25));
                items.Add(new PagedCleanupPlanEntry(identity,
                    (DuplicateFileDecision)reader.GetInt32(26), DriveFileCacheRepository.Read(reader)));
            }
        }

        int expectedCount = (int)Math.Min(pageSize, Math.Max(0, summary.FileEntryCount - offset));
        if (items.Count != expectedCount)
            throw new InvalidOperationException("La página del plan no coincide con los candidatos y conservados confirmados; se bloqueó la vista previa.");
        await transaction.CommitAsync(cancellationToken);
        return new PagedCleanupPlanPage(summary, Array.AsReadOnly(items.ToArray()), offset, pageSize);
    }

    public async Task<ReviewGroupPersistenceState> SetExplicitDecisionAsync(
        DuplicateGroupIdentity identity,
        string fileId,
        DuplicateFileDecision decision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        await ValidateRegisteredGroupCurrentAsync(connection, transaction, identity, cancellationToken);
        DuplicateFileDecision? current = await GetDecisionAsync(connection, transaction, identity, fileId, cancellationToken);
        if (current is null)
            throw new InvalidOperationException("El FileId no pertenece a este grupo del inventario vigente.");

        if (current == decision)
        {
            ReviewGroupPersistenceState unchanged = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
                ?? throw new InvalidOperationException("No se pudo recuperar el estado de decisión actual.");
            await transaction.CommitAsync(cancellationToken);
            return unchanged;
        }

        await UpdateDecisionAsync(connection, transaction, identity, fileId, decision, null, null, cancellationToken);
        var counts = await GetDecisionCountsAsync(connection, transaction, identity, cancellationToken);
        DuplicateGroupReviewStatus status = GetReviewStatus(false, counts.Keep, counts.Candidate, counts.Undecided);
        await UpdateGroupReviewStatusAsync(connection, transaction, identity, status, reviewedWithoutCleanup: false, cancellationToken);
        ReviewGroupPersistenceState updated = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el estado de decisión actualizado.");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task SetSelectedAsync(
        DuplicateGroupIdentity identity,
        bool selected,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ReviewGroupStates SET IsSelected=$selected
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$selected", selected ? 1 : 0);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException("No se encontró el grupo registrado para cambiar su selección.");
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<PersistedDecisionUpdate> SetDecisionAsync(
        DuplicateGroupIdentity identity,
        string fileId,
        DuplicateFileDecision decision,
        CancellationToken cancellationToken = default,
        string? automaticallyKeptFileId = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        if (!Enum.IsDefined(decision)) throw new ArgumentOutOfRangeException(nameof(decision));
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        long memberCount = await GetRegisteredMemberCountAsync(connection, transaction, identity, cancellationToken);
        DuplicateFileDecision? current = await GetDecisionAsync(connection, transaction, identity, fileId, cancellationToken);
        if (current is null) throw new InvalidOperationException("El FileId no pertenece a este grupo del inventario confirmado.");

        var counts = await GetDecisionCountsAsync(connection, transaction, identity, cancellationToken);
        if (decision == DuplicateFileDecision.CandidateForTrash && current == DuplicateFileDecision.Keep)
            throw new InvalidOperationException("El archivo marcado para conservar no puede ser candidato a papelera.");
        if (decision == DuplicateFileDecision.Undecided && current == DuplicateFileDecision.Keep && counts.Candidate == memberCount - 1)
            throw new InvalidOperationException("El grupo debe conservar al menos un archivo.");

        if (decision == DuplicateFileDecision.Keep)
        {
            await UpdateDecisionAsync(connection, transaction, identity, null, DuplicateFileDecision.Undecided,
                fileId, DuplicateFileDecision.Keep, cancellationToken);
        }
        await UpdateDecisionAsync(connection, transaction, identity, fileId, decision, null, null, cancellationToken);
        var countsAfterChange = await GetDecisionCountsAsync(connection, transaction, identity, cancellationToken);
        long keepCount = countsAfterChange.Keep;
        long candidateCount = countsAfterChange.Candidate;
        bool requiresAutomaticKeep = decision == DuplicateFileDecision.CandidateForTrash &&
            keepCount == 0 && candidateCount == memberCount - 1;
        if (requiresAutomaticKeep)
        {
            DuplicateFileDecision? autoKeepDecision = string.IsNullOrWhiteSpace(automaticallyKeptFileId)
                ? null
                : await GetDecisionAsync(connection, transaction, identity, automaticallyKeptFileId, cancellationToken);
            if (string.IsNullOrWhiteSpace(automaticallyKeptFileId) || automaticallyKeptFileId == fileId ||
                autoKeepDecision is null || autoKeepDecision == DuplicateFileDecision.CandidateForTrash)
                throw new InvalidOperationException("El cambio requiere el FileId de la copia que la revisión asignó automáticamente para conservar.");
            await UpdateDecisionAsync(connection, transaction, identity, automaticallyKeptFileId,
                DuplicateFileDecision.Keep, null, null, cancellationToken);
        }
        else if (automaticallyKeptFileId is not null)
        {
            throw new InvalidOperationException("Se recibió una conservación automática que no corresponde al estado resultante.");
        }

        var finalCounts = await GetDecisionCountsAsync(connection, transaction, identity, cancellationToken);
        keepCount = finalCounts.Keep;
        candidateCount = finalCounts.Candidate;
        long undecidedCount = finalCounts.Undecided;
        DuplicateGroupReviewStatus status = GetReviewStatus(false, keepCount, candidateCount, undecidedCount);
        await UpdateGroupReviewStatusAsync(connection, transaction, identity, status, reviewedWithoutCleanup: false, cancellationToken);
        ReviewGroupPersistenceState updated = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el estado actualizado.");
        await transaction.CommitAsync(cancellationToken);
        return new PersistedDecisionUpdate(updated, automaticallyKeptFileId);
    }

    /// <summary>Changes only an explicitly selected Keep decision; it never classifies other members.</summary>
    public async Task<ReviewGroupPersistenceState> SetExplicitKeepAsync(
        DuplicateGroupIdentity identity,
        string fileId,
        bool keep,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileId);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        if (await GetDecisionAsync(connection, transaction, identity, fileId, cancellationToken) is null)
            throw new InvalidOperationException("El FileId no pertenece a este grupo del inventario confirmado.");
        await UpdateDecisionAsync(connection, transaction, identity, fileId,
            keep ? DuplicateFileDecision.Keep : DuplicateFileDecision.Undecided, null, null, cancellationToken);
        var counts = await GetDecisionCountsAsync(connection, transaction, identity, cancellationToken);
        DuplicateGroupReviewStatus status = GetReviewStatus(false, counts.Keep, counts.Candidate, counts.Undecided);
        await UpdateGroupReviewStatusAsync(connection, transaction, identity, status, reviewedWithoutCleanup: false, cancellationToken);
        ReviewGroupPersistenceState updated = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el estado de revisión actualizado.");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<ReviewDecisionCounts> GetGroupDecisionCountsAsync(
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await EnsureActiveInventoryAsync(connection, null, identity.Inventory, cancellationToken);
        await ValidateRegisteredGroupCurrentAsync(connection, null, identity, cancellationToken);
        (long keep, long candidate, long undecided) = await GetDecisionCountsAsync(connection, null, identity, cancellationToken);
        long members = await GetRegisteredMemberCountAsync(connection, null, identity, cancellationToken);
        return new ReviewDecisionCounts(members, keep, candidate, undecided);
    }

    public async Task<ReviewGroupPersistenceState> ConfirmGroupReviewAsync(
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        await ValidateRegisteredGroupCurrentAsync(connection, transaction, identity, cancellationToken);
        var counts = await GetDecisionCountsAsync(connection, transaction, identity, cancellationToken);
        if (counts.Keep < 1 || counts.Candidate < 1)
            throw new InvalidOperationException("Para confirmar un grupo apto para el plan, marca explícitamente al menos un archivo para conservar y otro para enviar a papelera.");
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "UPDATE ReviewGroupStates SET IsReviewConfirmed=1, LastModifiedAtUtc=$at WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;";
            AddGroupParameters(command, identity);
            command.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        ReviewGroupPersistenceState updated = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el estado confirmado.");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<ReviewGroupPersistenceState> SetReviewedWithoutCleanupAsync(
        DuplicateGroupIdentity identity,
        bool reviewedWithoutCleanup,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        if (reviewedWithoutCleanup)
        {
            await UpdateDecisionAsync(connection, transaction, identity, null, DuplicateFileDecision.Undecided,
                exceptFileId: null, onlyIfCurrent: DuplicateFileDecision.CandidateForTrash, cancellationToken: cancellationToken);
        }

        (long keepCount, long candidateCount, long undecidedCount) = await GetDecisionCountsAsync(connection, transaction, identity, cancellationToken);
        DuplicateGroupReviewStatus status = GetReviewStatus(reviewedWithoutCleanup, keepCount, candidateCount, undecidedCount);
        await UpdateGroupReviewStatusAsync(connection, transaction, identity, status, reviewedWithoutCleanup, cancellationToken);
        ReviewGroupPersistenceState updated = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el estado actualizado.");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    public async Task<ReviewGroupPersistenceState> SetNotesAsync(
        DuplicateGroupIdentity identity,
        string? notes,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureComparison(connection);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await EnsureGroupStateAsync(connection, transaction, identity, cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ReviewGroupStates SET Notes=$notes, LastModifiedAtUtc=$at
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$notes", (object?)notes ?? DBNull.Value);
        command.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
        ReviewGroupPersistenceState updated = await ReadGroupStateAsync(connection, transaction, identity, cancellationToken)
            ?? throw new InvalidOperationException("No se pudo recuperar el estado actualizado.");
        await transaction.CommitAsync(cancellationToken);
        return updated;
    }

    private static async Task EnsureGroupStateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await EnsureActiveInventoryAsync(connection, transaction, identity.Inventory, cancellationToken);
        long actualMembers = await CountCurrentGroupMembersAsync(connection, transaction, identity, cancellationToken);
        if (actualMembers < 2) throw new InvalidOperationException("El grupo ya no es un duplicado exacto del inventario confirmado.");

        await using (SqliteCommand insertGroup = connection.CreateCommand())
        {
            insertGroup.Transaction = transaction;
            insertGroup.CommandText = """
                INSERT INTO ReviewGroupStates
                    (AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum, MemberCount, ReviewStatus,
                     ReviewedWithoutCleanup, Notes, IsSelected, IsReviewConfirmed, LastModifiedAtUtc)
                VALUES ($accountKey, $scopeKey, $scanId, $sizeBytes, $checksum, $memberCount, $pending, 0, NULL, 0, 0, $at)
                ON CONFLICT(AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum) DO NOTHING;
                """;
            AddGroupParameters(insertGroup, identity);
            insertGroup.Parameters.AddWithValue("$memberCount", actualMembers);
            insertGroup.Parameters.AddWithValue("$pending", (int)DuplicateGroupReviewStatus.Pending);
            insertGroup.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
            await insertGroup.ExecuteNonQueryAsync(cancellationToken);
        }

        long registeredMembers = await GetRegisteredMemberCountAsync(connection, transaction, identity, cancellationToken);
        if (registeredMembers != actualMembers)
            throw new InvalidOperationException("El número de miembros del grupo cambió; se debe invalidar el inventario.");

        await using (SqliteCommand seed = connection.CreateCommand())
        {
            seed.Transaction = transaction;
            seed.CommandText = $"""
                INSERT INTO ReviewFileDecisions(AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum, FileId, Decision)
                SELECT $accountKey, $scopeKey, $scanId, $sizeBytes, $checksum, f.FileId, $undecided
                FROM DriveFiles f
                {CompletedSessionJoin}
                WHERE {CompletedFilesPredicate} AND {ComparableFilePredicate}
                  AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
                  AND f.SizeBytes=$sizeBytes
                  AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=$checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE
                ON CONFLICT(AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum, FileId) DO NOTHING;
                """;
            AddGroupParameters(seed, identity);
            seed.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
            seed.Parameters.AddWithValue("$undecided", (int)DuplicateFileDecision.Undecided);
            await seed.ExecuteNonQueryAsync(cancellationToken);
        }

        var decisionCounts = await CountDecisionsAsync(connection, transaction, identity, cancellationToken);
        long decisionCount = decisionCounts.Count;
        if (decisionCount != actualMembers)
            throw new InvalidOperationException("Las decisiones persistidas no cubren exactamente todos los miembros del grupo.");
    }

    private static async Task EnsureRegisteredGroupAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*) FROM ReviewGroupStates
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        if ((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
            throw new InvalidOperationException("El grupo aún no se ha registrado para revisión en este inventario.");
    }

    private static async Task ValidateRegisteredGroupCurrentAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await EnsureRegisteredGroupAsync(connection, transaction, identity, cancellationToken);
        long expectedMembers = await GetRegisteredMemberCountAsync(connection, transaction, identity, cancellationToken);
        long actualMembers = await CountCurrentGroupMembersAsync(connection, transaction, identity, cancellationToken);
        var decisionCounts = await CountDecisionsAsync(connection, transaction, identity, cancellationToken);
        long decisionCount = decisionCounts.Count;
        if (expectedMembers < 2 || actualMembers != expectedMembers || decisionCount != expectedMembers)
            throw new InvalidOperationException("El grupo o sus decisiones ya no coinciden íntegramente con el inventario; se debe invalidar y repetir el escaneo.");
    }

    private static async Task EnsureActiveInventoryAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM ReviewInventories r
            JOIN ScanSessions s ON s.ScanId=r.ScanId AND s.AccountKey=r.AccountKey AND s.ScopeKey=r.ScopeKey
                AND s.ScanType='Full' AND s.Status='Completed'
            WHERE r.AccountKey=$accountKey AND r.ScopeKey=$scopeKey AND r.ScanId=$scanId AND r.IsObsolete=0
              AND s.ScanId=(
                  SELECT newest.ScanId FROM ScanSessions newest
                  WHERE newest.AccountKey=$accountKey AND newest.ScopeKey=$scopeKey
                    AND newest.ScanType='Full' AND newest.Status='Completed'
                  ORDER BY COALESCE(newest.CompletedAtUtc, newest.UpdatedAtUtc) DESC,
                           newest.StartedAtUtc DESC, newest.ScanId DESC LIMIT 1
              );
            """;
        AddInventoryParameters(command, inventory);
        if ((long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L) != 1)
            throw new InvalidOperationException("Las decisiones están bloqueadas: el inventario no está registrado, completo, vigente o pertenece a otra cuenta/alcance.");
    }

    private static async Task EnsureLatestCompletedFullScanAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT ScanId FROM ScanSessions
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanType='Full' AND Status='Completed'
            ORDER BY COALESCE(CompletedAtUtc, UpdatedAtUtc) DESC, StartedAtUtc DESC, ScanId DESC LIMIT 1;
            """;
        AddInventoryParameters(command, inventory);
        string? latest = (string?)await command.ExecuteScalarAsync(cancellationToken);
        if (!string.Equals(latest, inventory.ScanId, StringComparison.Ordinal))
            throw new InvalidOperationException("Solo se pueden registrar decisiones para el escaneo Full completado más reciente del alcance.");
    }

    private static async Task<bool?> GetInventoryObsoleteFlagAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT IsObsolete FROM ReviewInventories WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId;";
        AddInventoryParameters(command, inventory);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0;
    }

    private static async Task<long> CountCurrentGroupMembersAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            SELECT COUNT(*) FROM DriveFiles f
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate} AND {ComparableFilePredicate}
              AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
              AND f.SizeBytes=$sizeBytes
              AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=$checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE;
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<long> GetRegisteredMemberCountAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MemberCount FROM ReviewGroupStates
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? 0L : Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static async Task<(long Count, long Candidates)> CountDecisionsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*), SUM(CASE WHEN Decision=$candidate THEN 1 ELSE 0 END)
            FROM ReviewFileDecisions
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$candidate", (int)DuplicateFileDecision.CandidateForTrash);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0);
        return (reader.GetInt64(0), reader.IsDBNull(1) ? 0 : reader.GetInt64(1));
    }

    private static async Task<(long Keep, long Candidate, long Undecided)> GetDecisionCountsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT SUM(CASE WHEN Decision=$keep THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Decision=$candidate THEN 1 ELSE 0 END),
                   SUM(CASE WHEN Decision=$undecided THEN 1 ELSE 0 END)
            FROM ReviewFileDecisions
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$keep", (int)DuplicateFileDecision.Keep);
        command.Parameters.AddWithValue("$candidate", (int)DuplicateFileDecision.CandidateForTrash);
        command.Parameters.AddWithValue("$undecided", (int)DuplicateFileDecision.Undecided);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return (0, 0, 0);
        return (reader.IsDBNull(0) ? 0 : reader.GetInt64(0),
            reader.IsDBNull(1) ? 0 : reader.GetInt64(1),
            reader.IsDBNull(2) ? 0 : reader.GetInt64(2));
    }

    private static async Task<PagedCleanupPlanSummary> ReadCleanupPlanSummaryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        ScanInventoryIdentity inventory,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT g.SizeBytes, g.NormalizedChecksum, g.MemberCount,
                   (SELECT COUNT(*) FROM ReviewFileDecisions d WHERE d.AccountKey=g.AccountKey AND d.ScopeKey=g.ScopeKey
                       AND d.ScanId=g.ScanId AND d.SizeBytes=g.SizeBytes AND d.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY),
                   (SELECT COUNT(*) FROM DriveFiles f WHERE f.LastSeenScanId=g.ScanId AND f.AccountKey=g.AccountKey AND f.ScopeKey=g.ScopeKey
                       AND f.IsTrashed=0 AND f.IsRemoved=0 AND f.SizeBytes=g.SizeBytes AND f.Md5Checksum IS NOT NULL
                       AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
                       AND substr(COALESCE(f.MimeType,''),1,length($workspacePrefix))<>$workspacePrefix COLLATE BINARY
                       AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=g.NormalizedChecksum COLLATE DOTNET_ORDINAL_IGNORE_CASE),
                   (SELECT COUNT(*) FROM ReviewFileDecisions d JOIN DriveFiles f ON f.FileId=d.FileId
                       WHERE d.AccountKey=g.AccountKey AND d.ScopeKey=g.ScopeKey AND d.ScanId=g.ScanId
                         AND d.SizeBytes=g.SizeBytes AND d.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY
                         AND f.AccountKey=g.AccountKey AND f.ScopeKey=g.ScopeKey AND f.LastSeenScanId=g.ScanId
                         AND f.IsTrashed=0 AND f.IsRemoved=0 AND f.SizeBytes=g.SizeBytes AND f.Md5Checksum IS NOT NULL
                         AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
                         AND substr(COALESCE(f.MimeType,''),1,length($workspacePrefix))<>$workspacePrefix COLLATE BINARY
                         AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=g.NormalizedChecksum COLLATE DOTNET_ORDINAL_IGNORE_CASE),
                   (SELECT COUNT(*) FROM ReviewFileDecisions k WHERE k.AccountKey=g.AccountKey AND k.ScopeKey=g.ScopeKey
                       AND k.ScanId=g.ScanId AND k.SizeBytes=g.SizeBytes AND k.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY AND k.Decision=$keep),
                   (SELECT COUNT(*) FROM ReviewFileDecisions c WHERE c.AccountKey=g.AccountKey AND c.ScopeKey=g.ScopeKey
                       AND c.ScanId=g.ScanId AND c.SizeBytes=g.SizeBytes AND c.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY AND c.Decision=$candidate),
                   (SELECT COUNT(*) FROM ReviewFileDecisions u WHERE u.AccountKey=g.AccountKey AND u.ScopeKey=g.ScopeKey
                       AND u.ScanId=g.ScanId AND u.SizeBytes=g.SizeBytes AND u.NormalizedChecksum=g.NormalizedChecksum COLLATE BINARY AND u.Decision=$undecided)
            FROM ReviewGroupStates g
            WHERE g.AccountKey=$accountKey AND g.ScopeKey=$scopeKey AND g.ScanId=$scanId AND g.IsReviewConfirmed=1
            ORDER BY g.SizeBytes DESC, g.NormalizedChecksum COLLATE BINARY;
            """;
        AddInventoryParameters(command, inventory);
        command.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        command.Parameters.AddWithValue("$keep", (int)DuplicateFileDecision.Keep);
        command.Parameters.AddWithValue("$candidate", (int)DuplicateFileDecision.CandidateForTrash);
        command.Parameters.AddWithValue("$undecided", (int)DuplicateFileDecision.Undecided);

        long groups = 0;
        long candidates = 0;
        long entries = 0;
        BigInteger bytes = BigInteger.Zero;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long size = reader.GetInt64(0);
            string checksum = reader.GetString(1);
            long expectedMembers = reader.GetInt64(2);
            long decisionCount = reader.GetInt64(3);
            long currentMembers = reader.GetInt64(4);
            long currentDecisionMembers = reader.GetInt64(5);
            long keepCount = reader.GetInt64(6);
            long candidateCount = reader.GetInt64(7);
            long undecidedCount = reader.GetInt64(8);
            if (expectedMembers < 2 || decisionCount != expectedMembers || currentMembers != expectedMembers ||
                currentDecisionMembers != expectedMembers || keepCount < 1 || candidateCount < 1 ||
                keepCount + candidateCount + undecidedCount != expectedMembers)
            {
                throw new InvalidOperationException($"El grupo confirmado (tamaño {size}, MD5 {checksum}) tiene membresía o decisiones inconsistentes; se bloqueó el plan completo.");
            }

            groups++;
            candidates += candidateCount;
            entries += keepCount + candidateCount;
            bytes += new BigInteger(size) * candidateCount;
        }

        return new PagedCleanupPlanSummary(inventory, groups, candidates, entries, bytes);
    }

    private static async Task<DuplicateFileDecision?> GetDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DuplicateGroupIdentity identity,
        string fileId,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT Decision FROM ReviewFileDecisions
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY AND FileId=$fileId;
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$fileId", fileId);
        object? value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : (DuplicateFileDecision)Convert.ToInt32(value, CultureInfo.InvariantCulture);
    }

    private static async Task UpdateDecisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DuplicateGroupIdentity identity,
        string? fileId,
        DuplicateFileDecision decision,
        string? exceptFileId,
        DuplicateFileDecision? onlyIfCurrent,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            UPDATE ReviewFileDecisions SET Decision=$decision
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY
              AND ($fileId IS NULL OR FileId=$fileId)
              AND ($exceptFileId IS NULL OR FileId<>$exceptFileId)
              AND ($onlyIfCurrent IS NULL OR Decision=$onlyIfCurrent);
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$decision", (int)decision);
        command.Parameters.AddWithValue("$fileId", (object?)fileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$exceptFileId", (object?)exceptFileId ?? DBNull.Value);
        command.Parameters.AddWithValue("$onlyIfCurrent", (object?)(onlyIfCurrent is null ? null : (int)onlyIfCurrent.Value) ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task UpdateGroupReviewStatusAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DuplicateGroupIdentity identity,
        DuplicateGroupReviewStatus status,
        bool reviewedWithoutCleanup,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE ReviewGroupStates
            SET ReviewStatus=$status, ReviewedWithoutCleanup=$reviewed, IsReviewConfirmed=0, LastModifiedAtUtc=$at
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        command.Parameters.AddWithValue("$status", (int)status);
        command.Parameters.AddWithValue("$reviewed", reviewedWithoutCleanup ? 1 : 0);
        command.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static DuplicateGroupReviewStatus GetReviewStatus(bool reviewed, long keep, long candidate, long undecided) =>
        reviewed ? DuplicateGroupReviewStatus.ReviewedWithoutCleanup
        : keep == 0 && candidate == 0 ? DuplicateGroupReviewStatus.Pending
        : keep == 1 && candidate > 0 && undecided == 0 ? DuplicateGroupReviewStatus.ReadyForCleanup
        : DuplicateGroupReviewStatus.PartiallyReviewed;

    private static async Task<ReviewGroupPersistenceState?> ReadGroupStateAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT MemberCount, ReviewStatus, ReviewedWithoutCleanup, Notes, IsSelected, IsReviewConfirmed, LastModifiedAtUtc
            FROM ReviewGroupStates
            WHERE AccountKey=$accountKey AND ScopeKey=$scopeKey AND ScanId=$scanId
              AND SizeBytes=$sizeBytes AND NormalizedChecksum=$checksum COLLATE BINARY;
            """;
        AddGroupParameters(command, identity);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return new ReviewGroupPersistenceState(
            identity,
            reader.GetInt64(0),
            (DuplicateGroupReviewStatus)reader.GetInt32(1),
            reader.GetInt64(2) != 0,
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.GetInt64(4) != 0,
            reader.GetInt64(5) != 0,
            SqlitePersistenceFormat.FromUtcText(reader.GetString(6))!.Value);
    }

    private static void ConfigureComparison(SqliteConnection connection)
    {
        connection.CreateCollation("DOTNET_ORDINAL_IGNORE_CASE", static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left, right));
        connection.CreateFunction<string?, int>("DOTNET_IS_NULL_OR_WHITESPACE", static value =>
            string.IsNullOrWhiteSpace(value) ? 1 : 0, isDeterministic: true);
    }

    private static void ValidatePage(ScanInventoryIdentity inventory, int offset, int pageSize)
    {
        ValidateInventory(inventory);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > MaxPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"El tamaño de página debe estar entre 1 y {MaxPageSize}.");
    }

    private static void ValidateInventory(ScanInventoryIdentity inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.AccountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.ScopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.ScanId);
    }

    private static void AddInventoryParameters(SqliteCommand command, ScanInventoryIdentity inventory)
    {
        command.Parameters.AddWithValue("$accountKey", inventory.AccountKey);
        command.Parameters.AddWithValue("$scopeKey", inventory.ScopeKey);
        command.Parameters.AddWithValue("$scanId", inventory.ScanId);
    }

    private static void AddGroupParameters(SqliteCommand command, DuplicateGroupIdentity identity)
    {
        AddInventoryParameters(command, identity.Inventory);
        command.Parameters.AddWithValue("$sizeBytes", identity.SizeBytes);
        command.Parameters.AddWithValue("$checksum", identity.NormalizedChecksum);
    }

    private static void AddPageParameters(SqliteCommand command, int offset, int pageSize)
    {
        command.Parameters.AddWithValue("$offset", offset);
        command.Parameters.AddWithValue("$pageSize", pageSize);
    }

    private const string CompletedSessionJoin = "JOIN ScanSessions s ON s.ScanId=$scanId AND s.ScanType='Full' AND s.Status='Completed' AND s.AccountKey=$accountKey AND s.ScopeKey=$scopeKey";
    private const string CompletedFilesPredicate = "f.LastSeenScanId=$scanId AND f.AccountKey=$accountKey AND f.ScopeKey=$scopeKey AND f.IsTrashed=0 AND f.IsRemoved=0";
    private const string ComparableFilePredicate = "f.SizeBytes IS NOT NULL AND f.SizeBytes>=0 AND f.Md5Checksum IS NOT NULL AND substr(COALESCE(f.MimeType,''),1,length($workspacePrefix))<>$workspacePrefix COLLATE BINARY";
}
