using DriveDuplicateFinder.Models.Persistence;
using Microsoft.Data.Sqlite;
using System.Globalization;
using System.Numerics;

namespace DriveDuplicateFinder.Data.Sqlite.Repositories;

public sealed class DriveFileCacheRepository
{
    private const int MaxDuplicateReadPageSize = 500;
    private const string CompletedSessionJoin = "JOIN ScanSessions s ON s.ScanId=$scanId AND s.ScanType='Full' AND s.Status='Completed' AND s.AccountKey=$accountKey AND s.ScopeKey=$scopeKey";
    private const string CompletedFilesPredicate = "f.LastSeenScanId=$scanId AND f.AccountKey=$accountKey AND f.ScopeKey=$scopeKey AND f.IsTrashed=0 AND f.IsRemoved=0";
    private const string ComparableFilePredicate = "f.SizeBytes IS NOT NULL AND f.SizeBytes>=0 AND f.Md5Checksum IS NOT NULL AND substr(COALESCE(f.MimeType,''),1,length($workspacePrefix))<>$workspacePrefix COLLATE BINARY";
    internal const string SelectRecordColumns = "f.FileId, f.Name, f.NormalizedName, f.Extension, f.MimeType, f.SizeBytes, f.Md5Checksum, f.ModifiedTimeUtc, f.CreatedTimeUtc, f.ParentIdsJson, f.DriveId, f.OwnedByMe, f.CanTrash, f.IsShared, f.IsStarred, f.IsTrashed, f.IsRemoved, f.LastSeenScanId, f.LastChangedAtUtc, f.CachedAtUtc, f.AccountKey, f.ScopeKey, f.Version, f.OwnerNamesJson";
    private const string WorkspaceMimeTypePrefix = "application/vnd.google-apps.";
    private const string FolderMimeType = "application/vnd.google-apps.folder";

    private readonly SqliteConnectionFactory _connectionFactory;

    public DriveFileCacheRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task UpsertAsync(DriveFileCacheRecord record, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertAsync(connection, transaction, record, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, DriveFileCacheRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO DriveFiles (FileId, Name, NormalizedName, Extension, MimeType, SizeBytes, Md5Checksum, ModifiedTimeUtc, CreatedTimeUtc, ParentIdsJson, DriveId, OwnedByMe, CanTrash, IsShared, IsStarred, IsTrashed, IsRemoved, LastSeenScanId, LastChangedAtUtc, CachedAtUtc, AccountKey, ScopeKey, Version, OwnerNamesJson)
            VALUES ($fileId, $name, $normalizedName, $extension, $mimeType, $sizeBytes, $md5Checksum, $modifiedTimeUtc, $createdTimeUtc, $parentIdsJson, $driveId, $ownedByMe, $canTrash, $isShared, $isStarred, $isTrashed, 0, $lastSeenScanId, $lastChangedAtUtc, $cachedAtUtc, $accountKey, $scopeKey, $version, $ownerNamesJson)
            ON CONFLICT(FileId) DO UPDATE SET
                Name = excluded.Name, NormalizedName = excluded.NormalizedName, Extension = excluded.Extension, MimeType = excluded.MimeType,
                SizeBytes = excluded.SizeBytes, Md5Checksum = excluded.Md5Checksum, ModifiedTimeUtc = excluded.ModifiedTimeUtc,
                CreatedTimeUtc = excluded.CreatedTimeUtc, ParentIdsJson = excluded.ParentIdsJson, DriveId = excluded.DriveId,
                OwnedByMe = excluded.OwnedByMe, CanTrash = excluded.CanTrash, IsShared = excluded.IsShared, IsStarred = excluded.IsStarred,
                IsTrashed = excluded.IsTrashed, IsRemoved = 0, LastSeenScanId = excluded.LastSeenScanId,
                LastChangedAtUtc = excluded.LastChangedAtUtc, CachedAtUtc = excluded.CachedAtUtc,
                AccountKey = excluded.AccountKey, ScopeKey = excluded.ScopeKey, Version = excluded.Version,
                OwnerNamesJson = excluded.OwnerNamesJson;
            """;
        Bind(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DriveFileCacheRecord?> GetByIdAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT FileId, Name, NormalizedName, Extension, MimeType, SizeBytes, Md5Checksum, ModifiedTimeUtc, CreatedTimeUtc, ParentIdsJson, DriveId, OwnedByMe, CanTrash, IsShared, IsStarred, IsTrashed, IsRemoved, LastSeenScanId, LastChangedAtUtc, CachedAtUtc, AccountKey, ScopeKey, Version, OwnerNamesJson FROM DriveFiles WHERE FileId = $fileId;";
        command.Parameters.AddWithValue("$fileId", fileId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<DriveFileCacheRecord>> GetForCompletedScanAsync(
        string scanId,
        string accountKey,
        string scopeKey,
        CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT FileId, Name, NormalizedName, Extension, MimeType, SizeBytes, Md5Checksum,
                   ModifiedTimeUtc, CreatedTimeUtc, ParentIdsJson, DriveId, OwnedByMe, CanTrash,
                   IsShared, IsStarred, IsTrashed, IsRemoved, LastSeenScanId, LastChangedAtUtc,
                   CachedAtUtc, AccountKey, ScopeKey, Version, OwnerNamesJson
            FROM DriveFiles
            WHERE LastSeenScanId = $scanId AND AccountKey = $accountKey AND ScopeKey = $scopeKey
                  AND IsTrashed = 0 AND IsRemoved = 0
                  AND EXISTS (SELECT 1 FROM ScanSessions
                              WHERE ScanId = $scanId AND ScanType = 'Full' AND Status = 'Completed'
                                    AND AccountKey = $accountKey AND ScopeKey = $scopeKey);
            """;
        command.Parameters.AddWithValue("$scanId", scanId);
        command.Parameters.AddWithValue("$accountKey", accountKey);
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var records = new List<DriveFileCacheRecord>();
        while (await reader.ReadAsync(cancellationToken)) records.Add(Read(reader));
        return records;
    }

    internal static async Task<IReadOnlyList<ExactDuplicateGroupKey>> GetExactDuplicateGroupKeysAsync(
        SqliteConnection connection,
        string scanId,
        string accountKey,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT f.SizeBytes, f.Md5Checksum, COUNT(*)
            FROM DriveFiles f
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate} AND {ComparableFilePredicate}
            GROUP BY f.SizeBytes, f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE
            HAVING COUNT(*) > 1;
            """;
        AddCompletedScanParameters(command, scanId, accountKey, scopeKey);
        command.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var groups = new List<ExactDuplicateGroupKey>();
        while (await reader.ReadAsync(cancellationToken))
        {
            string checksum = reader.GetString(1);
            if (string.IsNullOrWhiteSpace(checksum))
            {
                continue;
            }

            groups.Add(new ExactDuplicateGroupKey(reader.GetInt64(0), checksum, reader.GetInt64(2)));
        }

        return groups;
    }

    /// <summary>Reads a bounded page of duplicate-group summaries from one completed Full inventory.</summary>
    public async Task<DuplicateGroupSummaryPage> GetDuplicateGroupSummariesPageAsync(
        ScanInventoryIdentity inventory,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ValidatePageRequest(inventory, offset, pageSize);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureExactDuplicateQuerySemantics(connection);

        await using SqliteCommand countCommand = connection.CreateCommand();
        countCommand.CommandText = GroupedExactDuplicatesCte + " SELECT COUNT(*) FROM DuplicateGroups;";
        AddCompletedScanParameters(countCommand, inventory.ScanId, inventory.AccountKey, inventory.ScopeKey);
        countCommand.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        long total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using SqliteCommand pageCommand = connection.CreateCommand();
        pageCommand.CommandText = GroupedExactDuplicatesCte + """
            SELECT FileSize, Md5Checksum, MemberCount, RepresentativeFileId, RepresentativeName
            FROM DuplicateGroups
            ORDER BY DOTNET_RECOVERABLE_BYTES_SORT_KEY(FileSize, MemberCount) COLLATE DOTNET_RECOVERABLE_BYTES_DESC,
                     FileSize DESC, Md5Checksum COLLATE BINARY ASC, RepresentativeFileId COLLATE BINARY ASC
            LIMIT $pageSize OFFSET $offset;
            """;
        AddCompletedScanParameters(pageCommand, inventory.ScanId, inventory.AccountKey, inventory.ScopeKey);
        pageCommand.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        pageCommand.Parameters.AddWithValue("$pageSize", pageSize);
        pageCommand.Parameters.AddWithValue("$offset", offset);

        var items = new List<DuplicateGroupSummary>(pageSize);
        await using SqliteDataReader reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            long sizeBytes = reader.GetInt64(0);
            string checksum = reader.GetString(1);
            long memberCount = reader.GetInt64(2);
            items.Add(new DuplicateGroupSummary(
                new DuplicateGroupIdentity(inventory, sizeBytes, checksum),
                memberCount,
                new BigInteger(sizeBytes) * (memberCount - 1),
                reader.GetString(3),
                reader.GetString(4)));
        }

        return new DuplicateGroupSummaryPage(items.AsReadOnly(), total, offset, pageSize);
    }

    /// <summary>Reads one bounded member page. The result is a page contract, never a DuplicateGroup.</summary>
    public async Task<DuplicateGroupMembersPage> GetDuplicateGroupMembersPageAsync(
        DuplicateGroupIdentity identity,
        int offset,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ValidatePageRequest(identity.Inventory, offset, pageSize);
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        ConfigureExactDuplicateQuerySemantics(connection);

        await using SqliteCommand countCommand = connection.CreateCommand();
        countCommand.CommandText = $"""
            SELECT COUNT(*)
            FROM DriveFiles f
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate} AND {ComparableFilePredicate}
                  AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
                  AND f.SizeBytes=$sizeBytes
                  AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=$checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE;
            """;
        AddCompletedScanParameters(countCommand, identity.Inventory.ScanId, identity.Inventory.AccountKey, identity.Inventory.ScopeKey);
        countCommand.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        countCommand.Parameters.AddWithValue("$sizeBytes", identity.SizeBytes);
        countCommand.Parameters.AddWithValue("$checksum", identity.NormalizedChecksum);
        long total = (long)(await countCommand.ExecuteScalarAsync(cancellationToken) ?? 0L);

        await using SqliteCommand pageCommand = connection.CreateCommand();
        pageCommand.CommandText = $"""
            SELECT {SelectRecordColumns}
            FROM DriveFiles f
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate} AND {ComparableFilePredicate}
                  AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
                  AND f.SizeBytes=$sizeBytes
                  AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=$checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE
            ORDER BY f.FileId COLLATE BINARY ASC
            LIMIT $pageSize OFFSET $offset;
            """;
        AddCompletedScanParameters(pageCommand, identity.Inventory.ScanId, identity.Inventory.AccountKey, identity.Inventory.ScopeKey);
        pageCommand.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        pageCommand.Parameters.AddWithValue("$sizeBytes", identity.SizeBytes);
        pageCommand.Parameters.AddWithValue("$checksum", identity.NormalizedChecksum);
        pageCommand.Parameters.AddWithValue("$pageSize", pageSize);
        pageCommand.Parameters.AddWithValue("$offset", offset);

        var items = new List<DriveFileCacheRecord>(pageSize);
        await using SqliteDataReader reader = await pageCommand.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(Read(reader));
        }

        return new DuplicateGroupMembersPage(identity, items.AsReadOnly(), total, offset, pageSize);
    }

    private static readonly string GroupedExactDuplicatesCte = $"""
        WITH DuplicateGroups AS
        (
            SELECT f.SizeBytes AS FileSize,
                   MIN(f.Md5Checksum COLLATE BINARY) AS Md5Checksum,
                   COUNT(*) AS MemberCount,
                   MIN(f.FileId COLLATE BINARY) AS RepresentativeFileId,
                   MIN(f.Name COLLATE BINARY) AS RepresentativeName
            FROM DriveFiles f
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate} AND {ComparableFilePredicate}
                  AND DOTNET_IS_NULL_OR_WHITESPACE(f.Md5Checksum)=0
            GROUP BY f.SizeBytes, f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE
            HAVING COUNT(*) > 1
        )
        """;

    private static void ConfigureExactDuplicateQuerySemantics(SqliteConnection connection)
    {
        // SQLite's built-in NOCASE is ASCII-only; use the same .NET comparer as the existing detector.
        connection.CreateCollation("DOTNET_ORDINAL_IGNORE_CASE", static (left, right) =>
            StringComparer.OrdinalIgnoreCase.Compare(left, right));
        connection.CreateFunction<string?, int>("DOTNET_IS_NULL_OR_WHITESPACE", static value =>
            string.IsNullOrWhiteSpace(value) ? 1 : 0, isDeterministic: true);

        // A fixed-width decimal key keeps recoverable-byte ordering exact even when a product exceeds Int64.
        connection.CreateFunction<long, long, string>("DOTNET_RECOVERABLE_BYTES_SORT_KEY", static (size, count) =>
            (new BigInteger(size) * (count - 1)).ToString("D38", CultureInfo.InvariantCulture), isDeterministic: true);
        connection.CreateCollation("DOTNET_RECOVERABLE_BYTES_DESC", static (left, right) =>
            StringComparer.Ordinal.Compare(right, left));
    }

    private static void ValidatePageRequest(ScanInventoryIdentity inventory, int offset, int pageSize)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.AccountKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.ScopeKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(inventory.ScanId);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        if (pageSize is < 1 or > MaxDuplicateReadPageSize)
            throw new ArgumentOutOfRangeException(nameof(pageSize), $"El tamaño de página debe estar entre 1 y {MaxDuplicateReadPageSize}.");
    }

    internal static async Task<Dictionary<int, List<DriveFileCacheRecord>>> GetExactDuplicateMembersAsync(
        SqliteConnection connection,
        string scanId,
        string accountKey,
        string scopeKey,
        IReadOnlyList<ExactDuplicateGroupKey> groups,
        CancellationToken cancellationToken)
    {
        if (groups.Count == 0)
        {
            return new Dictionary<int, List<DriveFileCacheRecord>>();
        }

        var values = new List<string>(groups.Count);
        for (int index = 0; index < groups.Count; index++)
        {
            values.Add($"({index}, $size{index}, $md5{index})");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            WITH RequestedGroups(GroupOrdinal, SizeBytes, Md5Checksum) AS (VALUES {string.Join(",", values)})
            SELECT {SelectRecordColumns}, g.GroupOrdinal
            FROM RequestedGroups g
            JOIN DriveFiles f ON f.SizeBytes=g.SizeBytes
                AND f.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE=g.Md5Checksum COLLATE DOTNET_ORDINAL_IGNORE_CASE
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate} AND {ComparableFilePredicate}
            ORDER BY g.GroupOrdinal, f.FileId;
            """;
        AddCompletedScanParameters(command, scanId, accountKey, scopeKey);
        command.Parameters.AddWithValue("$workspacePrefix", WorkspaceMimeTypePrefix);
        for (int index = 0; index < groups.Count; index++)
        {
            command.Parameters.AddWithValue($"$size{index}", groups[index].FileSize);
            command.Parameters.AddWithValue($"$md5{index}", groups[index].Md5Checksum);
        }

        var members = new Dictionary<int, List<DriveFileCacheRecord>>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            int groupOrdinal = reader.GetInt32(24);
            if (!members.TryGetValue(groupOrdinal, out List<DriveFileCacheRecord>? groupFiles))
            {
                groupFiles = [];
                members.Add(groupOrdinal, groupFiles);
            }

            groupFiles.Add(Read(reader));
        }

        return members;
    }

    internal static async Task<IReadOnlyList<DriveFileCacheRecord>> GetCompletedFoldersByIdsAsync(
        SqliteConnection connection,
        string scanId,
        string accountKey,
        string scopeKey,
        IReadOnlyCollection<string> folderIds,
        CancellationToken cancellationToken)
    {
        if (folderIds.Count == 0)
        {
            return Array.Empty<DriveFileCacheRecord>();
        }

        string[] ids = folderIds.Distinct(StringComparer.Ordinal).ToArray();
        var parameters = new List<string>(ids.Length);
        for (int index = 0; index < ids.Length; index++)
        {
            parameters.Add($"$folder{index}");
        }

        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {SelectRecordColumns}
            FROM DriveFiles f
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate} AND f.MimeType=$folderMimeType
                AND f.FileId IN ({string.Join(",", parameters)});
            """;
        AddCompletedScanParameters(command, scanId, accountKey, scopeKey);
        command.Parameters.AddWithValue("$folderMimeType", FolderMimeType);
        for (int index = 0; index < ids.Length; index++)
        {
            command.Parameters.AddWithValue(parameters[index], ids[index]);
        }

        var folders = new List<DriveFileCacheRecord>(ids.Length);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            folders.Add(Read(reader));
        }

        return folders;
    }

    internal static async Task<CompletedScanFileCounts> GetCompletedScanFileCountsAsync(
        SqliteConnection connection,
        string scanId,
        string accountKey,
        string scopeKey,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT f.MimeType, f.Md5Checksum, f.SizeBytes
            FROM DriveFiles f
            {CompletedSessionJoin}
            WHERE {CompletedFilesPredicate};
            """;
        AddCompletedScanParameters(command, scanId, accountKey, scopeKey);
        long itemsExamined = 0;
        long comparableFiles = 0;
        long filesWithoutMd5Ignored = 0;
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            itemsExamined++;
            string mimeType = reader.IsDBNull(0) ? string.Empty : reader.GetString(0);
            if (string.Equals(mimeType, FolderMimeType, StringComparison.Ordinal))
            {
                continue;
            }

            string? checksum = reader.IsDBNull(1) ? null : reader.GetString(1);
            if (mimeType.StartsWith(WorkspaceMimeTypePrefix, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(checksum))
            {
                filesWithoutMd5Ignored++;
                continue;
            }

            if (!reader.IsDBNull(2) && reader.GetInt64(2) >= 0)
            {
                comparableFiles++;
            }
        }

        return new CompletedScanFileCounts(itemsExamined, comparableFiles, filesWithoutMd5Ignored);
    }

    private static void AddCompletedScanParameters(SqliteCommand command, string scanId, string accountKey, string scopeKey)
    {
        command.Parameters.AddWithValue("$scanId", scanId);
        command.Parameters.AddWithValue("$accountKey", accountKey);
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
    }

    internal static async Task ReconcileMissingAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string scanId,
        string accountKey,
        string scopeKey,
        DateTimeOffset reconciledAtUtc,
        CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE DriveFiles SET IsRemoved = 1, LastChangedAtUtc = $at WHERE AccountKey = $accountKey AND ScopeKey = $scopeKey AND (LastSeenScanId IS NULL OR LastSeenScanId <> $scanId) AND IsRemoved = 0;";
        command.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(reconciledAtUtc));
        command.Parameters.AddWithValue("$accountKey", accountKey);
        command.Parameters.AddWithValue("$scopeKey", scopeKey);
        command.Parameters.AddWithValue("$scanId", scanId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> CountAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM DriveFiles;";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task DeleteAllAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "DELETE FROM DriveFiles;";
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void Bind(SqliteCommand command, DriveFileCacheRecord record)
    {
        Add(command, "$fileId", record.FileId); Add(command, "$name", record.Name); Add(command, "$normalizedName", record.NormalizedName);
        Add(command, "$extension", record.Extension); Add(command, "$mimeType", record.MimeType); Add(command, "$sizeBytes", record.SizeBytes);
        Add(command, "$md5Checksum", record.Md5Checksum); Add(command, "$modifiedTimeUtc", ToText(record.ModifiedTimeUtc)); Add(command, "$createdTimeUtc", ToText(record.CreatedTimeUtc));
        Add(command, "$parentIdsJson", SqlitePersistenceFormat.ToParentIdsJson(record.ParentIds)); Add(command, "$driveId", record.DriveId);
        Add(command, "$ownedByMe", ToInteger(record.OwnedByMe)); Add(command, "$canTrash", ToInteger(record.CanTrash)); Add(command, "$isShared", ToInteger(record.IsShared)); Add(command, "$isStarred", ToInteger(record.IsStarred));
        Add(command, "$isTrashed", record.IsTrashed ? 1 : 0); Add(command, "$isRemoved", record.IsRemoved ? 1 : 0); Add(command, "$lastSeenScanId", record.LastSeenScanId);
        Add(command, "$lastChangedAtUtc", ToText(record.LastChangedAtUtc)); Add(command, "$cachedAtUtc", SqlitePersistenceFormat.ToUtcText(record.CachedAtUtc));
        Add(command, "$accountKey", record.AccountKey); Add(command, "$scopeKey", record.ScopeKey); Add(command, "$version", record.Version);
        Add(command, "$ownerNamesJson", SqlitePersistenceFormat.ToOwnerNamesJson(record.OwnerNames));
    }

    internal static DriveFileCacheRecord Read(SqliteDataReader reader) => new()
    {
        FileId = reader.GetString(0), Name = reader.GetString(1), NormalizedName = Text(reader, 2), Extension = Text(reader, 3), MimeType = Text(reader, 4),
        SizeBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5), Md5Checksum = Text(reader, 6), ModifiedTimeUtc = SqlitePersistenceFormat.FromUtcText(Text(reader, 7)),
        CreatedTimeUtc = SqlitePersistenceFormat.FromUtcText(Text(reader, 8)), ParentIds = SqlitePersistenceFormat.FromParentIdsJson(Text(reader, 9)), DriveId = Text(reader, 10),
        OwnedByMe = ToBoolean(reader, 11), CanTrash = ToBoolean(reader, 12), IsShared = ToBoolean(reader, 13), IsStarred = ToBoolean(reader, 14),
        IsTrashed = reader.GetInt64(15) != 0, IsRemoved = reader.GetInt64(16) != 0, LastSeenScanId = Text(reader, 17),
        LastChangedAtUtc = SqlitePersistenceFormat.FromUtcText(Text(reader, 18)), CachedAtUtc = SqlitePersistenceFormat.FromUtcText(reader.GetString(19))!.Value,
        AccountKey = reader.GetString(20), ScopeKey = reader.GetString(21), Version = reader.IsDBNull(22) ? null : reader.GetInt64(22),
        OwnerNames = SqlitePersistenceFormat.FromOwnerNamesJson(Text(reader, 23))
    };

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? ToText(DateTimeOffset? value) => value is null ? null : SqlitePersistenceFormat.ToUtcText(value.Value);
    private static long? ToInteger(bool? value) => value is null ? null : value.Value ? 1 : 0;
    private static bool? ToBoolean(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}

internal sealed record ExactDuplicateGroupKey(long FileSize, string Md5Checksum, long MemberCount);

internal sealed record CompletedScanFileCounts(long ItemsExamined, long ComparableFiles, long FilesWithoutMd5Ignored);
