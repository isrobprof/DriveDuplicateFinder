using System.Text.Json;
using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

public sealed class CleanupHistoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public string HistoryDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DriveDuplicateFinder",
        "cleanup-history");

    public async Task<CleanupHistoryHandle> CreatePendingAsync(
        DuplicateGroupReview review,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(review);

        var record = new CleanupOperationRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            StartedAtUtc = DateTimeOffset.UtcNow,
            StableGroupId = review.StableId,
            FileSize = review.Group.FileSize,
            Md5Checksum = review.Group.Md5Checksum
        };

        foreach (DriveFileInfo file in review.Group.Files)
        {
            if (review.DecisionsByFileId.TryGetValue(file.Id, out DuplicateFileDecision decision) &&
                decision == DuplicateFileDecision.Keep)
            {
                record.KeepFile = CreateSnapshot(file);
            }
            else if (review.DecisionsByFileId.TryGetValue(file.Id, out decision) &&
                     decision == DuplicateFileDecision.CandidateForTrash)
            {
                record.CandidateFiles.Add(CreateSnapshot(file));
            }
        }

        string filename = $"cleanup-{record.StartedAtUtc:yyyyMMdd-HHmmss}-{record.OperationId}.json";
        string path = Path.Combine(HistoryDirectory, filename);
        var handle = new CleanupHistoryHandle(path, record);
        await WriteAtomicallyAsync(path, record, overwrite: false, cancellationToken);
        return handle;
    }

    public Task SaveAsync(CleanupHistoryHandle handle, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        return WriteAtomicallyAsync(handle.Path, handle.Record, overwrite: true, cancellationToken);
    }

    private static CleanupFileSnapshot CreateSnapshot(DriveFileInfo file) => new()
    {
        Id = file.Id,
        Name = file.Name,
        MimeType = file.MimeType,
        Size = file.Size,
        Md5Checksum = file.Md5Checksum,
        ModifiedTime = file.ModifiedTime,
        ParentIds = file.ParentIds.ToArray(),
        OwnedByMe = file.OwnedByMe,
        IsShared = file.IsShared,
        IsStarred = file.IsStarred,
        DriveId = file.DriveId,
        Version = file.Version,
        CanTrash = file.CanTrash,
        Path = file.Path
    };

    private static async Task WriteAtomicallyAsync(
        string destinationPath,
        CleanupOperationRecord record,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string? directory = Path.GetDirectoryName(destinationPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new InvalidOperationException("No se pudo determinar la carpeta del historial de limpieza.");
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
                await JsonSerializer.SerializeAsync(stream, record, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            File.Move(temporaryPath, destinationPath, overwrite);
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

public sealed record CleanupHistoryHandle(string Path, CleanupOperationRecord Record);
