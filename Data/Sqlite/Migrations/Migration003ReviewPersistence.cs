using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Migrations;

/// <summary>Adds inventory-scoped, individually persisted review decisions and selection state.</summary>
public sealed class Migration003ReviewPersistence : ISqliteMigration
{
    public int Version => 3;
    public string Name => "Inventory-scoped review state";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE ReviewInventories
            (
                AccountKey TEXT NOT NULL,
                ScopeKey TEXT NOT NULL,
                ScanId TEXT NOT NULL,
                IsObsolete INTEGER NOT NULL DEFAULT 0 CHECK (IsObsolete IN (0, 1)),
                RegisteredAtUtc TEXT NOT NULL,
                InvalidatedAtUtc TEXT NULL,
                PRIMARY KEY (AccountKey, ScopeKey, ScanId),
                FOREIGN KEY (ScanId) REFERENCES ScanSessions(ScanId) ON DELETE CASCADE
            );

            CREATE TABLE ReviewGroupStates
            (
                AccountKey TEXT NOT NULL,
                ScopeKey TEXT NOT NULL,
                ScanId TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL CHECK (SizeBytes >= 0),
                NormalizedChecksum TEXT NOT NULL COLLATE BINARY CHECK (length(NormalizedChecksum) > 0),
                MemberCount INTEGER NOT NULL CHECK (MemberCount >= 2),
                ReviewStatus INTEGER NOT NULL CHECK (ReviewStatus BETWEEN 0 AND 3),
                ReviewedWithoutCleanup INTEGER NOT NULL DEFAULT 0 CHECK (ReviewedWithoutCleanup IN (0, 1)),
                Notes TEXT NULL,
                IsSelected INTEGER NOT NULL DEFAULT 0 CHECK (IsSelected IN (0, 1)),
                LastModifiedAtUtc TEXT NOT NULL,
                PRIMARY KEY (AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum),
                FOREIGN KEY (AccountKey, ScopeKey, ScanId)
                    REFERENCES ReviewInventories(AccountKey, ScopeKey, ScanId) ON DELETE CASCADE
            );

            CREATE TABLE ReviewFileDecisions
            (
                AccountKey TEXT NOT NULL,
                ScopeKey TEXT NOT NULL,
                ScanId TEXT NOT NULL,
                SizeBytes INTEGER NOT NULL,
                NormalizedChecksum TEXT NOT NULL COLLATE BINARY,
                FileId TEXT NOT NULL,
                Decision INTEGER NOT NULL CHECK (Decision BETWEEN 0 AND 2),
                PRIMARY KEY (AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum, FileId),
                FOREIGN KEY (AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum)
                    REFERENCES ReviewGroupStates(AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum) ON DELETE CASCADE
            );

            CREATE INDEX IX_ReviewGroupStates_Inventory_Status
                ON ReviewGroupStates(AccountKey, ScopeKey, ScanId, ReviewStatus, SizeBytes, NormalizedChecksum);
            CREATE INDEX IX_ReviewGroupStates_Inventory_Selected
                ON ReviewGroupStates(AccountKey, ScopeKey, ScanId, IsSelected, SizeBytes, NormalizedChecksum);
            CREATE INDEX IX_ReviewFileDecisions_Group_Decision
                ON ReviewFileDecisions(AccountKey, ScopeKey, ScanId, SizeBytes, NormalizedChecksum, Decision);

            CREATE TRIGGER TR_ScanSessions_FullStart_InvalidateReviewInventories
            AFTER INSERT ON ScanSessions
            WHEN NEW.ScanType = 'Full'
            BEGIN
                UPDATE ReviewInventories
                SET IsObsolete = 1,
                    InvalidatedAtUtc = COALESCE(NEW.StartedAtUtc, NEW.UpdatedAtUtc)
                WHERE AccountKey = NEW.AccountKey
                  AND ScopeKey = NEW.ScopeKey
                  AND ScanId <> NEW.ScanId
                  AND IsObsolete = 0;
            END;

            CREATE TRIGGER TR_ScanSessions_Completed_InvalidateReviewInventories
            AFTER UPDATE OF Status ON ScanSessions
            WHEN NEW.ScanType = 'Full' AND NEW.Status = 'Completed'
            BEGIN
                UPDATE ReviewInventories
                SET IsObsolete = 1,
                    InvalidatedAtUtc = COALESCE(NEW.CompletedAtUtc, NEW.UpdatedAtUtc)
                WHERE AccountKey = NEW.AccountKey
                  AND ScopeKey = NEW.ScopeKey
                  AND ScanId <> NEW.ScanId
                  AND IsObsolete = 0;
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
