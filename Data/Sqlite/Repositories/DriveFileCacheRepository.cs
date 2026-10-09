using DriveDuplicateFinder.Models.Persistence;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Repositories;

public sealed class DriveFileCacheRepository
{
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
            INSERT INTO DriveFiles (FileId, Name, NormalizedName, Extension, MimeType, SizeBytes, Md5Checksum, ModifiedTimeUtc, CreatedTimeUtc, ParentIdsJson, DriveId, OwnedByMe, CanTrash, IsShared, IsStarred, IsTrashed, IsRemoved, LastSeenScanId, LastChangedAtUtc, CachedAtUtc)
            VALUES ($fileId, $name, $normalizedName, $extension, $mimeType, $sizeBytes, $md5Checksum, $modifiedTimeUtc, $createdTimeUtc, $parentIdsJson, $driveId, $ownedByMe, $canTrash, $isShared, $isStarred, $isTrashed, $isRemoved, $lastSeenScanId, $lastChangedAtUtc, $cachedAtUtc)
            ON CONFLICT(FileId) DO UPDATE SET
                Name = excluded.Name, NormalizedName = excluded.NormalizedName, Extension = excluded.Extension, MimeType = excluded.MimeType,
                SizeBytes = excluded.SizeBytes, Md5Checksum = excluded.Md5Checksum, ModifiedTimeUtc = excluded.ModifiedTimeUtc,
                CreatedTimeUtc = excluded.CreatedTimeUtc, ParentIdsJson = excluded.ParentIdsJson, DriveId = excluded.DriveId,
                OwnedByMe = excluded.OwnedByMe, CanTrash = excluded.CanTrash, IsShared = excluded.IsShared, IsStarred = excluded.IsStarred,
                IsTrashed = excluded.IsTrashed, IsRemoved = excluded.IsRemoved, LastSeenScanId = excluded.LastSeenScanId,
                LastChangedAtUtc = excluded.LastChangedAtUtc, CachedAtUtc = excluded.CachedAtUtc;
            """;
        Bind(command, record);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DriveFileCacheRecord?> GetByIdAsync(string fileId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT FileId, Name, NormalizedName, Extension, MimeType, SizeBytes, Md5Checksum, ModifiedTimeUtc, CreatedTimeUtc, ParentIdsJson, DriveId, OwnedByMe, CanTrash, IsShared, IsStarred, IsTrashed, IsRemoved, LastSeenScanId, LastChangedAtUtc, CachedAtUtc FROM DriveFiles WHERE FileId = $fileId;";
        command.Parameters.AddWithValue("$fileId", fileId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
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
    }

    private static DriveFileCacheRecord Read(SqliteDataReader reader) => new()
    {
        FileId = reader.GetString(0), Name = reader.GetString(1), NormalizedName = Text(reader, 2), Extension = Text(reader, 3), MimeType = Text(reader, 4),
        SizeBytes = reader.IsDBNull(5) ? null : reader.GetInt64(5), Md5Checksum = Text(reader, 6), ModifiedTimeUtc = SqlitePersistenceFormat.FromUtcText(Text(reader, 7)),
        CreatedTimeUtc = SqlitePersistenceFormat.FromUtcText(Text(reader, 8)), ParentIds = SqlitePersistenceFormat.FromParentIdsJson(Text(reader, 9)), DriveId = Text(reader, 10),
        OwnedByMe = ToBoolean(reader, 11), CanTrash = ToBoolean(reader, 12), IsShared = ToBoolean(reader, 13), IsStarred = ToBoolean(reader, 14),
        IsTrashed = reader.GetInt64(15) != 0, IsRemoved = reader.GetInt64(16) != 0, LastSeenScanId = Text(reader, 17),
        LastChangedAtUtc = SqlitePersistenceFormat.FromUtcText(Text(reader, 18)), CachedAtUtc = SqlitePersistenceFormat.FromUtcText(reader.GetString(19))!.Value
    };

    private static void Add(SqliteCommand command, string name, object? value) => command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    private static string? ToText(DateTimeOffset? value) => value is null ? null : SqlitePersistenceFormat.ToUtcText(value.Value);
    private static long? ToInteger(bool? value) => value is null ? null : value.Value ? 1 : 0;
    private static bool? ToBoolean(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetInt64(ordinal) != 0;
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
