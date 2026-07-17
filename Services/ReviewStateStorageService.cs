using System.Text.Json;
using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

public sealed class ReviewStateStorageService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveDuplicateFinder",
        "review-state.json");

    public async Task<ReviewStateLoadResult> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(StatePath))
        {
            return ReviewStateLoadResult.Empty;
        }

        try
        {
            await using FileStream stream = new(StatePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            DuplicateReviewState? state = await JsonSerializer.DeserializeAsync<DuplicateReviewState>(
                stream,
                JsonOptions,
                cancellationToken);

            if (state is null)
            {
                return new ReviewStateLoadResult(null, "El archivo local de revisi\u00F3n est\u00E1 vac\u00EDo y no se modificar\u00E1 autom\u00E1ticamente.", true);
            }

            if (state.FormatVersion != DuplicateReviewState.CurrentFormatVersion)
            {
                return ReviewStateLoadResult.Incompatible;
            }

            return new ReviewStateLoadResult(state, null, false);
        }
        catch (JsonException)
        {
            return new ReviewStateLoadResult(null, "El archivo local de revisi\u00F3n contiene JSON no v\u00E1lido y no se modificar\u00E1 autom\u00E1ticamente.", true);
        }
        catch (IOException ex)
        {
            return new ReviewStateLoadResult(null, $"No se pudo leer el estado local: {ex.Message}", false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReviewStateLoadResult(null, $"No se pudo cargar el estado local: {ex.Message}", false);
        }
    }

    public async Task SaveAsync(
        IEnumerable<DuplicateGroupReview> reviews,
        bool allowOverwriteCorruptState,
        bool existingStateIsCorrupt,
        CancellationToken cancellationToken = default)
    {
        if (existingStateIsCorrupt && !allowOverwriteCorruptState)
        {
            return;
        }

        var state = new DuplicateReviewState
        {
            SavedAtUtc = DateTimeOffset.UtcNow,
            Groups = reviews.Select(review => new StoredDuplicateGroupReview
            {
                StableId = review.StableId,
                FileSize = review.Group.FileSize,
                Md5Checksum = review.Group.Md5Checksum,
                DecisionsByFileId = new Dictionary<string, DuplicateFileDecision>(review.DecisionsByFileId, StringComparer.Ordinal),
                ReviewedWithoutCleanup = review.ReviewedWithoutCleanup,
                Notes = review.Notes,
                LastModifiedUtc = review.LastModifiedUtc
            }).ToList()
        };

        await WriteJsonAtomicallyAsync(StatePath, state, cancellationToken);
    }

    public async Task ExportPlanAsync(
        string destinationPath,
        IEnumerable<CleanupPlanRow> plan,
        CancellationToken cancellationToken = default)
    {
        await WriteJsonAtomicallyAsync(destinationPath, plan.ToArray(), cancellationToken);
    }

    private static async Task WriteJsonAtomicallyAsync<T>(
        string destinationPath,
        T value,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("No se pudo determinar la carpeta de destino.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = Path.Combine(directory, $".{Path.GetFileName(destinationPath)}.{Guid.NewGuid():N}.tmp");

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await JsonSerializer.SerializeAsync(stream, value, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

public sealed record ReviewStateLoadResult(
    DuplicateReviewState? State,
    string? WarningMessage,
    bool IsCorrupt)
{
    public static ReviewStateLoadResult Empty { get; } = new(null, null, false);

    public static ReviewStateLoadResult Incompatible { get; } = new(
        null,
        "El estado local usa una versi\u00F3n no compatible y se ha ignorado.",
        false);
}
