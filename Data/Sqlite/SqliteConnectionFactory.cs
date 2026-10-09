using Microsoft.Data.Sqlite;
using DriveDuplicateFinder.Services;

namespace DriveDuplicateFinder.Data.Sqlite;

public sealed class SqliteConnectionFactory
{
    private static readonly Lazy<bool> ProviderInitialized = new(() =>
    {
        SQLitePCL.Batteries_V2.Init();
        return true;
    });

    private readonly LocalDataPathService _paths;

    public SqliteConnectionFactory(LocalDataPathService paths)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
    }

    public async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        _ = ProviderInitialized.Value;
        Directory.CreateDirectory(_paths.BaseDataDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString());

        try
        {
            await connection.OpenAsync(cancellationToken);
            await ConfigureAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static async Task ConfigureAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        // WAL persiste en la base; foreign_keys, synchronous y busy_timeout se establecen por conexión.
        await ExecutePragmaAsync(connection, "PRAGMA journal_mode = WAL;", cancellationToken);
        await ExecutePragmaAsync(connection, "PRAGMA foreign_keys = ON;", cancellationToken);
        await ExecutePragmaAsync(connection, "PRAGMA synchronous = FULL;", cancellationToken);
        await ExecutePragmaAsync(connection, "PRAGMA busy_timeout = 10000;", cancellationToken);
    }

    private static async Task ExecutePragmaAsync(SqliteConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
