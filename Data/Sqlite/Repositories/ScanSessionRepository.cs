using DriveDuplicateFinder.Models.Persistence;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Repositories;

public sealed class ScanSessionRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;
    public ScanSessionRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task CreateAsync(ScanSessionRecord record, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await InsertAsync(connection, transaction, record, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task InsertAsync(SqliteConnection connection, SqliteTransaction transaction, ScanSessionRecord record, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO ScanSessions (ScanId, ScanType, Status, StartedAtUtc, UpdatedAtUtc, CompletedAtUtc, CancelledAtUtc, FailedAtUtc, LastError, ProcessedItemCount, ProcessedPageCount, DiscoveredItemCount, UpdatedItemCount, RemovedItemCount) VALUES ($id,$type,$status,$started,$updated,$completed,$cancelled,$failed,$error,$items,$pages,$discovered,$updatedItems,$removed);";
        Bind(command, record); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ScanSessionRecord?> GetByIdAsync(string scanId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT ScanId, ScanType, Status, StartedAtUtc, UpdatedAtUtc, CompletedAtUtc, CancelledAtUtc, FailedAtUtc, LastError, ProcessedItemCount, ProcessedPageCount, DiscoveredItemCount, UpdatedItemCount, RemovedItemCount FROM ScanSessions WHERE ScanId=$id;"; command.Parameters.AddWithValue("$id", scanId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task<ScanSessionRecord?> GetLatestIncompleteAsync(CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand(); command.CommandText = "SELECT ScanId, ScanType, Status, StartedAtUtc, UpdatedAtUtc, CompletedAtUtc, CancelledAtUtc, FailedAtUtc, LastError, ProcessedItemCount, ProcessedPageCount, DiscoveredItemCount, UpdatedItemCount, RemovedItemCount FROM ScanSessions WHERE Status IN ('Pending','Running','Paused','Failed') ORDER BY UpdatedAtUtc DESC LIMIT 1;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken); return await reader.ReadAsync(cancellationToken) ? Read(reader) : null;
    }

    public async Task UpdateStatusAsync(string scanId, ScanStatus status, DateTimeOffset updatedAtUtc, string? lastError = null, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken); await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpdateStatusAsync(connection, transaction, scanId, status, updatedAtUtc, lastError, cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task UpdateProgressAsync(SqliteConnection connection, SqliteTransaction transaction, ScanSessionRecord record, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE ScanSessions SET Status=$status, UpdatedAtUtc=$updated, ProcessedItemCount=$items, ProcessedPageCount=$pages, DiscoveredItemCount=$discovered, UpdatedItemCount=$updatedItems, RemovedItemCount=$removed WHERE ScanId=$id;";
        command.Parameters.AddWithValue("$id", record.ScanId); command.Parameters.AddWithValue("$status", record.Status.ToString()); command.Parameters.AddWithValue("$updated", SqlitePersistenceFormat.ToUtcText(record.UpdatedAtUtc));
        command.Parameters.AddWithValue("$items", record.ProcessedItemCount); command.Parameters.AddWithValue("$pages", record.ProcessedPageCount); command.Parameters.AddWithValue("$discovered", record.DiscoveredItemCount); command.Parameters.AddWithValue("$updatedItems", record.UpdatedItemCount); command.Parameters.AddWithValue("$removed", record.RemovedItemCount);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("La sesión de escaneo no existe.");
    }

    private static async Task UpdateStatusAsync(SqliteConnection connection, SqliteTransaction transaction, string scanId, ScanStatus status, DateTimeOffset updatedAtUtc, string? lastError, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "UPDATE ScanSessions SET Status=$status, UpdatedAtUtc=$updated, LastError=$error, CompletedAtUtc=$completed, CancelledAtUtc=$cancelled, FailedAtUtc=$failed WHERE ScanId=$id;";
        command.Parameters.AddWithValue("$id", scanId); command.Parameters.AddWithValue("$status", status.ToString()); command.Parameters.AddWithValue("$updated", SqlitePersistenceFormat.ToUtcText(updatedAtUtc)); command.Parameters.AddWithValue("$error", (object?)lastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$completed", status == ScanStatus.Completed ? SqlitePersistenceFormat.ToUtcText(updatedAtUtc) : DBNull.Value); command.Parameters.AddWithValue("$cancelled", status == ScanStatus.Cancelled ? SqlitePersistenceFormat.ToUtcText(updatedAtUtc) : DBNull.Value); command.Parameters.AddWithValue("$failed", status == ScanStatus.Failed ? SqlitePersistenceFormat.ToUtcText(updatedAtUtc) : DBNull.Value);
        if (await command.ExecuteNonQueryAsync(cancellationToken) != 1) throw new InvalidOperationException("La sesión de escaneo no existe.");
    }

    private static void Bind(SqliteCommand command, ScanSessionRecord record)
    {
        command.Parameters.AddWithValue("$id", record.ScanId); command.Parameters.AddWithValue("$type", record.ScanType.ToString()); command.Parameters.AddWithValue("$status", record.Status.ToString()); command.Parameters.AddWithValue("$started", SqlitePersistenceFormat.ToUtcText(record.StartedAtUtc)); command.Parameters.AddWithValue("$updated", SqlitePersistenceFormat.ToUtcText(record.UpdatedAtUtc));
        command.Parameters.AddWithValue("$completed", (object?)Text(record.CompletedAtUtc) ?? DBNull.Value); command.Parameters.AddWithValue("$cancelled", (object?)Text(record.CancelledAtUtc) ?? DBNull.Value); command.Parameters.AddWithValue("$failed", (object?)Text(record.FailedAtUtc) ?? DBNull.Value); command.Parameters.AddWithValue("$error", (object?)record.LastError ?? DBNull.Value);
        command.Parameters.AddWithValue("$items", record.ProcessedItemCount); command.Parameters.AddWithValue("$pages", record.ProcessedPageCount); command.Parameters.AddWithValue("$discovered", record.DiscoveredItemCount); command.Parameters.AddWithValue("$updatedItems", record.UpdatedItemCount); command.Parameters.AddWithValue("$removed", record.RemovedItemCount);
    }
    private static string? Text(DateTimeOffset? value) => value is null ? null : SqlitePersistenceFormat.ToUtcText(value.Value);
    private static ScanSessionRecord Read(SqliteDataReader reader) => new() { ScanId=reader.GetString(0), ScanType=Enum.Parse<ScanType>(reader.GetString(1)), Status=Enum.Parse<ScanStatus>(reader.GetString(2)), StartedAtUtc=SqlitePersistenceFormat.FromUtcText(reader.GetString(3))!.Value, UpdatedAtUtc=SqlitePersistenceFormat.FromUtcText(reader.GetString(4))!.Value, CompletedAtUtc=SqlitePersistenceFormat.FromUtcText(reader.IsDBNull(5)?null:reader.GetString(5)), CancelledAtUtc=SqlitePersistenceFormat.FromUtcText(reader.IsDBNull(6)?null:reader.GetString(6)), FailedAtUtc=SqlitePersistenceFormat.FromUtcText(reader.IsDBNull(7)?null:reader.GetString(7)), LastError=reader.IsDBNull(8)?null:reader.GetString(8), ProcessedItemCount=reader.GetInt64(9), ProcessedPageCount=reader.GetInt64(10), DiscoveredItemCount=reader.GetInt64(11), UpdatedItemCount=reader.GetInt64(12), RemovedItemCount=reader.GetInt64(13) };
}
