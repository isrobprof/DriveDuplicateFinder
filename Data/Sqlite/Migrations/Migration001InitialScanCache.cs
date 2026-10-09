using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite.Migrations;

public sealed class Migration001InitialScanCache : ISqliteMigration
{
    public int Version => 1;
    public string Name => "Initial scan cache schema";

    public async Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            CREATE TABLE DriveFiles
            (
                FileId TEXT NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                NormalizedName TEXT NULL,
                Extension TEXT NULL,
                MimeType TEXT NULL,
                SizeBytes INTEGER NULL,
                Md5Checksum TEXT NULL,
                ModifiedTimeUtc TEXT NULL,
                CreatedTimeUtc TEXT NULL,
                ParentIdsJson TEXT NULL,
                DriveId TEXT NULL,
                OwnedByMe INTEGER NULL,
                CanTrash INTEGER NULL,
                IsShared INTEGER NULL,
                IsStarred INTEGER NULL,
                IsTrashed INTEGER NOT NULL DEFAULT 0,
                IsRemoved INTEGER NOT NULL DEFAULT 0,
                LastSeenScanId TEXT NULL,
                LastChangedAtUtc TEXT NULL,
                CachedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ScanSessions
            (
                ScanId TEXT NOT NULL PRIMARY KEY,
                ScanType TEXT NOT NULL,
                Status TEXT NOT NULL,
                StartedAtUtc TEXT NOT NULL,
                UpdatedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                CancelledAtUtc TEXT NULL,
                FailedAtUtc TEXT NULL,
                LastError TEXT NULL,
                ProcessedItemCount INTEGER NOT NULL DEFAULT 0,
                ProcessedPageCount INTEGER NOT NULL DEFAULT 0,
                DiscoveredItemCount INTEGER NOT NULL DEFAULT 0,
                UpdatedItemCount INTEGER NOT NULL DEFAULT 0,
                RemovedItemCount INTEGER NOT NULL DEFAULT 0
            );

            CREATE TABLE ScanCheckpoints
            (
                ScanId TEXT NOT NULL PRIMARY KEY,
                NextPageToken TEXT NULL,
                ChangePageToken TEXT NULL,
                NewStartPageToken TEXT NULL,
                LastCommittedPageNumber INTEGER NOT NULL DEFAULT 0,
                LastCommittedItemCount INTEGER NOT NULL DEFAULT 0,
                LastCommittedAtUtc TEXT NOT NULL,
                FOREIGN KEY (ScanId) REFERENCES ScanSessions(ScanId) ON DELETE CASCADE
            );

            CREATE TABLE DriveChangeState
            (
                ScopeKey TEXT NOT NULL PRIMARY KEY,
                StartPageToken TEXT NULL,
                LastProcessedPageToken TEXT NULL,
                NewStartPageToken TEXT NULL,
                UpdatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ExactDuplicateGroups
            (
                GroupId TEXT NOT NULL PRIMARY KEY,
                StableKey TEXT NOT NULL UNIQUE,
                SizeBytes INTEGER NOT NULL,
                Md5Checksum TEXT NOT NULL,
                MemberCount INTEGER NOT NULL,
                CalculatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE ExactDuplicateMembers
            (
                GroupId TEXT NOT NULL,
                FileId TEXT NOT NULL,
                Position INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (GroupId, FileId),
                FOREIGN KEY (GroupId) REFERENCES ExactDuplicateGroups(GroupId) ON DELETE CASCADE,
                FOREIGN KEY (FileId) REFERENCES DriveFiles(FileId) ON DELETE CASCADE
            );

            CREATE TABLE PossibleDuplicateGroups
            (
                GroupId TEXT NOT NULL PRIMARY KEY,
                StableKey TEXT NOT NULL UNIQUE,
                ConfidenceLevel TEXT NOT NULL,
                MatchReason TEXT NOT NULL,
                MemberCount INTEGER NOT NULL,
                CalculatedAtUtc TEXT NOT NULL
            );

            CREATE TABLE PossibleDuplicateMembers
            (
                GroupId TEXT NOT NULL,
                FileId TEXT NOT NULL,
                Position INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (GroupId, FileId),
                FOREIGN KEY (GroupId) REFERENCES PossibleDuplicateGroups(GroupId) ON DELETE CASCADE,
                FOREIGN KEY (FileId) REFERENCES DriveFiles(FileId) ON DELETE CASCADE
            );

            CREATE INDEX IX_DriveFiles_Size_Md5 ON DriveFiles(SizeBytes, Md5Checksum);
            CREATE INDEX IX_DriveFiles_NormalizedName_Size ON DriveFiles(NormalizedName, SizeBytes);
            CREATE INDEX IX_DriveFiles_Size_ModifiedTime ON DriveFiles(SizeBytes, ModifiedTimeUtc);
            CREATE INDEX IX_DriveFiles_LastSeenScanId ON DriveFiles(LastSeenScanId);
            CREATE INDEX IX_DriveFiles_IsTrashed_IsRemoved ON DriveFiles(IsTrashed, IsRemoved);
            CREATE INDEX IX_ScanSessions_Status ON ScanSessions(Status);
            CREATE INDEX IX_ExactDuplicateMembers_FileId ON ExactDuplicateMembers(FileId);
            CREATE INDEX IX_PossibleDuplicateMembers_FileId ON PossibleDuplicateMembers(FileId);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
