using DriveDuplicateFinder.Data.Sqlite.Migrations;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Data.Sqlite;

public sealed class SqliteDatabaseInitializer
{
    private readonly SqliteConnectionFactory _connectionFactory;
    private readonly IReadOnlyList<ISqliteMigration> _migrations;

    public SqliteDatabaseInitializer(SqliteConnectionFactory connectionFactory, IEnumerable<ISqliteMigration>? migrations = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _migrations = (migrations ?? [new Migration001InitialScanCache()])
            .OrderBy(migration => migration.Version)
            .ToArray();
        if (_migrations.Select(migration => migration.Version).Distinct().Count() != _migrations.Count)
        {
            throw new ArgumentException("Las migraciones SQLite contienen versiones duplicadas.", nameof(migrations));
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
            await EnsureMigrationTableAsync(connection, cancellationToken);
            HashSet<int> appliedVersions = await GetAppliedVersionsAsync(connection, cancellationToken);
            foreach (ISqliteMigration migration in _migrations.Where(migration => !appliedVersions.Contains(migration.Version)))
            {
                await ApplyMigrationAsync(connection, migration, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is SqliteException or InvalidOperationException)
        {
            throw new InvalidOperationException("No se pudo inicializar la base de caché SQLite.", exception);
        }
    }

    private static async Task EnsureMigrationTableAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS SchemaMigrations
            (
                Version INTEGER NOT NULL PRIMARY KEY,
                Name TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<int>> GetAppliedVersionsAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT Version FROM SchemaMigrations;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        var versions = new HashSet<int>();
        while (await reader.ReadAsync(cancellationToken)) versions.Add(reader.GetInt32(0));
        return versions;
    }

    private static async Task ApplyMigrationAsync(SqliteConnection connection, ISqliteMigration migration, CancellationToken cancellationToken)
    {
        try
        {
            await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await migration.ApplyAsync(connection, transaction, cancellationToken);
            await using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO SchemaMigrations (Version, Name, AppliedAtUtc) VALUES ($version, $name, $appliedAtUtc);";
            command.Parameters.AddWithValue("$version", migration.Version);
            command.Parameters.AddWithValue("$name", migration.Name);
            command.Parameters.AddWithValue("$appliedAtUtc", SqlitePersistenceFormat.ToUtcText(DateTimeOffset.UtcNow));
            await command.ExecuteNonQueryAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"No se pudo aplicar la migración {migration.Version}.", exception);
        }
    }
}
