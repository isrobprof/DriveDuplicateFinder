using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Migrations;

/// <summary>Persists an explicit user confirmation separately from merely marking files to keep.</summary>
public sealed class Migration004ExplicitReviewConfirmation : ISqliteMigration
{
    public int Version => 4;
    public string Name => "Explicit paged review confirmation";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "ALTER TABLE ReviewGroupStates ADD COLUMN IsReviewConfirmed INTEGER NOT NULL DEFAULT 0 CHECK (IsReviewConfirmed IN (0, 1));";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
