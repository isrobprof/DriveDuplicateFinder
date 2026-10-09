using DriveDuplicateFinder.Models.Persistence;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Repositories;

public sealed class DriveChangeStateRepository
{
    private readonly SqliteConnectionFactory _connectionFactory;
    public DriveChangeStateRepository(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task UpsertAsync(DriveChangeStateRecord record, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken); await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "INSERT INTO DriveChangeState (ScopeKey, StartPageToken, LastProcessedPageToken, NewStartPageToken, UpdatedAtUtc) VALUES ($key,$start,$last,$newStart,$updated) ON CONFLICT(ScopeKey) DO UPDATE SET StartPageToken=excluded.StartPageToken, LastProcessedPageToken=excluded.LastProcessedPageToken, NewStartPageToken=excluded.NewStartPageToken, UpdatedAtUtc=excluded.UpdatedAtUtc;";
        command.Parameters.AddWithValue("$key", record.ScopeKey); command.Parameters.AddWithValue("$start", (object?)record.StartPageToken ?? DBNull.Value); command.Parameters.AddWithValue("$last", (object?)record.LastProcessedPageToken ?? DBNull.Value); command.Parameters.AddWithValue("$newStart", (object?)record.NewStartPageToken ?? DBNull.Value); command.Parameters.AddWithValue("$updated", SqlitePersistenceFormat.ToUtcText(record.UpdatedAtUtc));
        await command.ExecuteNonQueryAsync(cancellationToken); await transaction.CommitAsync(cancellationToken);
    }

    public async Task<DriveChangeStateRecord?> GetByScopeKeyAsync(string scopeKey, CancellationToken cancellationToken = default)
    {
        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken); await using SqliteCommand command = connection.CreateCommand(); command.CommandText="SELECT ScopeKey, StartPageToken, LastProcessedPageToken, NewStartPageToken, UpdatedAtUtc FROM DriveChangeState WHERE ScopeKey=$key;"; command.Parameters.AddWithValue("$key", scopeKey);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? new DriveChangeStateRecord { ScopeKey=reader.GetString(0), StartPageToken=Text(reader,1), LastProcessedPageToken=Text(reader,2), NewStartPageToken=Text(reader,3), UpdatedAtUtc=SqlitePersistenceFormat.FromUtcText(reader.GetString(4))!.Value } : null;
    }
    private static string? Text(SqliteDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
}
