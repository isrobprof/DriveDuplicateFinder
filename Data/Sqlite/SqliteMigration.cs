using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite;

public interface ISqliteMigration
{
    int Version { get; }
    string Name { get; }
    Task ApplyAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken);
}
