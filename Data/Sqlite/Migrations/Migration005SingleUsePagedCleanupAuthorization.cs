using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Migrations;

/// <summary>Creates a single durable authorization slot for the future first paged cleanup attempt.</summary>
public sealed class Migration005SingleUsePagedCleanupAuthorization : ISqliteMigration
{
    public int Version => 5;
    public string Name => "Single-use paged cleanup authorization";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE PagedCleanupTrialAuthorization
            (
                Slot INTEGER NOT NULL PRIMARY KEY CHECK (Slot = 1),
                State INTEGER NOT NULL CHECK (State BETWEEN 0 AND 2),
                PermitId TEXT NULL CHECK (PermitId IS NULL OR length(PermitId) > 0),
                AccountKey TEXT NULL CHECK (AccountKey IS NULL OR length(AccountKey) > 0),
                ScopeKey TEXT NULL CHECK (ScopeKey IS NULL OR length(ScopeKey) > 0),
                ScanId TEXT NULL CHECK (ScanId IS NULL OR length(ScanId) > 0),
                GroupId TEXT NULL CHECK (GroupId IS NULL OR length(GroupId) > 0),
                GroupSizeBytes INTEGER NULL CHECK (GroupSizeBytes IS NULL OR GroupSizeBytes >= 0),
                NormalizedChecksum TEXT NULL CHECK (NormalizedChecksum IS NULL OR length(NormalizedChecksum) > 0),
                CandidateFileId TEXT NULL CHECK (CandidateFileId IS NULL OR length(CandidateFileId) > 0),
                CandidateSizeBytes INTEGER NULL CHECK (CandidateSizeBytes IS NULL OR CandidateSizeBytes BETWEEN 0 AND 10000000),
                ConfirmedAtUtc TEXT NULL CHECK (ConfirmedAtUtc IS NULL OR length(ConfirmedAtUtc) > 0),
                KeepSetSha256 TEXT NULL CHECK (KeepSetSha256 IS NULL OR length(KeepSetSha256) = 64),
                IssuedAtUtc TEXT NULL CHECK (IssuedAtUtc IS NULL OR length(IssuedAtUtc) > 0),
                ConsumedAtUtc TEXT NULL,
                UNIQUE (PermitId),
                CHECK
                (
                    (State = 0 AND PermitId IS NULL AND AccountKey IS NULL AND ScopeKey IS NULL AND ScanId IS NULL
                        AND GroupId IS NULL AND GroupSizeBytes IS NULL AND NormalizedChecksum IS NULL
                        AND CandidateFileId IS NULL AND CandidateSizeBytes IS NULL AND ConfirmedAtUtc IS NULL
                        AND KeepSetSha256 IS NULL AND IssuedAtUtc IS NULL AND ConsumedAtUtc IS NULL)
                    OR
                    (State = 1 AND PermitId IS NOT NULL AND AccountKey IS NOT NULL AND ScopeKey IS NOT NULL AND ScanId IS NOT NULL
                        AND GroupId IS NOT NULL AND GroupSizeBytes IS NOT NULL AND NormalizedChecksum IS NOT NULL
                        AND CandidateFileId IS NOT NULL AND CandidateSizeBytes IS NOT NULL AND ConfirmedAtUtc IS NOT NULL
                        AND CandidateSizeBytes=GroupSizeBytes AND length(KeepSetSha256)=64
                        AND KeepSetSha256 IS NOT NULL AND IssuedAtUtc IS NOT NULL AND ConsumedAtUtc IS NULL)
                    OR
                    (State = 2 AND PermitId IS NOT NULL AND AccountKey IS NOT NULL AND ScopeKey IS NOT NULL AND ScanId IS NOT NULL
                        AND GroupId IS NOT NULL AND GroupSizeBytes IS NOT NULL AND NormalizedChecksum IS NOT NULL
                        AND CandidateFileId IS NOT NULL AND CandidateSizeBytes IS NOT NULL AND ConfirmedAtUtc IS NOT NULL
                        AND CandidateSizeBytes=GroupSizeBytes AND length(KeepSetSha256)=64
                        AND KeepSetSha256 IS NOT NULL AND IssuedAtUtc IS NOT NULL AND ConsumedAtUtc IS NOT NULL)
                )
            );

            CREATE TRIGGER TR_PagedCleanupTrialAuthorization_NoReissue
            BEFORE UPDATE ON PagedCleanupTrialAuthorization
            WHEN NOT (OLD.State=0 AND NEW.State=1)
              AND NOT (OLD.State=1 AND NEW.State=2
                AND OLD.PermitId IS NEW.PermitId AND OLD.AccountKey IS NEW.AccountKey
                AND OLD.ScopeKey IS NEW.ScopeKey AND OLD.ScanId IS NEW.ScanId AND OLD.GroupId IS NEW.GroupId
                AND OLD.GroupSizeBytes IS NEW.GroupSizeBytes AND OLD.NormalizedChecksum IS NEW.NormalizedChecksum
                AND OLD.CandidateFileId IS NEW.CandidateFileId AND OLD.CandidateSizeBytes IS NEW.CandidateSizeBytes
                AND OLD.ConfirmedAtUtc IS NEW.ConfirmedAtUtc AND OLD.KeepSetSha256 IS NEW.KeepSetSha256
                AND OLD.IssuedAtUtc IS NEW.IssuedAtUtc AND OLD.ConsumedAtUtc IS NULL AND NEW.ConsumedAtUtc IS NOT NULL)
            BEGIN
                SELECT RAISE(ABORT, 'Paged cleanup trial authorization cannot be reset or changed');
            END;

            CREATE TRIGGER TR_PagedCleanupTrialAuthorization_NoDelete
            BEFORE DELETE ON PagedCleanupTrialAuthorization
            BEGIN
                SELECT RAISE(ABORT, 'Paged cleanup trial authorization cannot be deleted');
            END;

            INSERT INTO PagedCleanupTrialAuthorization(Slot, State) VALUES (1, 0);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
