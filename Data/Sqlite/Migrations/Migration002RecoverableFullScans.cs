using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Migrations;

/// <summary>Adds account-scoped metadata required to safely resume full scans.</summary>
public sealed class Migration002RecoverableFullScans : ISqliteMigration
{
    public int Version => 2;
    public string Name => "Recoverable full scan identity and metadata";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            ALTER TABLE ScanSessions ADD COLUMN AccountKey TEXT NOT NULL DEFAULT '';
            ALTER TABLE ScanSessions ADD COLUMN ScopeKey TEXT NOT NULL DEFAULT '';
            ALTER TABLE ScanSessions ADD COLUMN RootFolderId TEXT NULL;
            ALTER TABLE DriveFiles ADD COLUMN AccountKey TEXT NOT NULL DEFAULT '';
            ALTER TABLE DriveFiles ADD COLUMN ScopeKey TEXT NOT NULL DEFAULT '';
            ALTER TABLE DriveFiles ADD COLUMN Version INTEGER NULL;
            ALTER TABLE DriveFiles ADD COLUMN OwnerNamesJson TEXT NOT NULL DEFAULT '[]';

            CREATE INDEX IX_ScanSessions_Account_Scope_Status
                ON ScanSessions(AccountKey, ScopeKey, ScanType, Status, UpdatedAtUtc);
            CREATE INDEX IX_DriveFiles_Account_Scope_LastSeen
                ON DriveFiles(AccountKey, ScopeKey, LastSeenScanId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
