using DriveDuplicateFinder.Data.Sqlite;
using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models.Persistence;
using Microsoft.Data.Sqlite;

namespace DriveDuplicateFinder.Services;

public sealed class ScanPagePersistenceRequest
{
    public required ScanSessionRecord Session { get; init; }
    public required ScanCheckpointRecord Checkpoint { get; init; }
    public IReadOnlyCollection<DriveFileCacheRecord> Files { get; init; } = Array.Empty<DriveFileCacheRecord>();

    // Punto interno para comprobaciones deterministas: no lo consume la aplicación.
    internal Func<CancellationToken, Task>? BeforeCommitAsync { get; init; }
}

/// <summary>Unidad atómica preparada para una futura página de escaneo; no consulta Google Drive.</summary>
public sealed class ScanPagePersistenceService
{
    private readonly SqliteConnectionFactory _connectionFactory;
    public ScanPagePersistenceService(SqliteConnectionFactory connectionFactory) => _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));

    public async Task PersistPageAsync(ScanPagePersistenceRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!string.Equals(request.Session.ScanId, request.Checkpoint.ScanId, StringComparison.Ordinal)) throw new ArgumentException("La sesión y el checkpoint no coinciden.", nameof(request));

        await using SqliteConnection connection = await _connectionFactory.OpenAsync(cancellationToken);
        await using SqliteTransaction transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        try
        {
            foreach (DriveFileCacheRecord file in request.Files)
            {
                await DriveFileCacheRepository.UpsertAsync(connection, transaction, file, cancellationToken);
            }
            await ScanSessionRepository.UpdateProgressAsync(connection, transaction, request.Session, cancellationToken);
            await ScanCheckpointRepository.UpsertAsync(connection, transaction, request.Checkpoint, cancellationToken);
            if (request.BeforeCommitAsync is not null)
            {
                await request.BeforeCommitAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        catch (Exception exception)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw new InvalidOperationException($"No se pudo confirmar la página {request.Checkpoint.LastCommittedPageNumber} del análisis.", exception);
        }
    }
}
