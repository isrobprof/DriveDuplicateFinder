using DriveDuplicateFinder.Models.Persistence;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Repositories;

public sealed class ScanCheckpointRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;
    public ScanCheckpointRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task UpsertAsync(ScanCheckpointRecord record, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken); await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await UpsertAsync(connection, transaction, record, cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    internal static async Task UpsertAsync(SqliteConnection connection, SqliteTransaction transaction, ScanCheckpointRecord record, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO ScanCheckpoints (ScanId, NextPageToken, ChangePageToken, NewStartPageToken, LastCommittedPageNumber, LastCommittedItemCount, LastCommittedAtUtc) VALUES ($id,$next,$change,$newStart,$page,$items,$at) ON CONFLICT(ScanId) DO UPDATE SET NextPageToken=excluded.NextPageToken, ChangePageToken=excluded.ChangePageToken, NewStartPageToken=excluded.NewStartPageToken, LastCommittedPageNumber=excluded.LastCommittedPageNumber, LastCommittedItemCount=excluded.LastCommittedItemCount, LastCommittedAtUtc=excluded.LastCommittedAtUtc;";
        command.Parameters.AddWithValue("$id", record.ScanId); command.Parameters.AddWithValue("$next", (object?)record.NextPageToken ?? DBNull.Value); command.Parameters.AddWithValue("$change", (object?)record.ChangePageToken ?? DBNull.Value); command.Parameters.AddWithValue("$newStart", (object?)record.NewStartPageToken ?? DBNull.Value); command.Parameters.AddWithValue("$page", record.LastCommittedPageNumber); command.Parameters.AddWithValue("$items", record.LastCommittedItemCount); command.Parameters.AddWithValue("$at", SqlitePersistenceFormat.ToUtcText(record.LastCommittedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ScanCheckpointRecord?> GetByScanIdAsync(string scanId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken); await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT ScanId, NextPageToken, ChangePageToken, NewStartPageToken, LastCommittedPageNumber, LastCommittedItemCount, LastCommittedAtUtc FROM ScanCheckpoints WHERE ScanId=$id;"; command.Parameters.AddWithValue("$id", scanId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new ScanCheckpointRecord { ScanId=reader.GetString(0), NextPageToken=Text(reader,1), ChangePageToken=Text(reader,2), NewStartPageToken=Text(reader,3), LastCommittedPageNumber=reader.GetInt64(4), LastCommittedItemCount=reader.GetInt64(5), LastCommittedAtUtc=SqlitePersistenceFormat.FromUtcText(reader.GetString(6))!.Value } : null;
    }

    public async Task DeleteAsync(string scanId, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken); await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken); await using SqliteCommand command = connection.CreateCommand(); command.Transaction=transaction; command.CommandText="DELETE FROM ScanCheckpoints WHERE ScanId=$id;"; command.Parameters.AddWithValue("$id", scanId); await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
