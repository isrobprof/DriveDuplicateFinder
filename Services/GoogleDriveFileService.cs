using Google.Apis.Drive.v3;
using GoogleFile = Google.Apis.Drive.v3.Data.File;
using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

public sealed class GoogleDriveFileService
{
    private const string FolderMimeType = "application/vnd.google-apps.folder";
    private const string GoogleWorkspaceMimeTypePrefix = "application/vnd.google-apps.";
    private const string RequestedFields =
        "nextPageToken,files(id,name,mimeType,size,md5Checksum,modifiedTime,parents,driveId,trashed)";
    private const string MyDriveUrl = "https://drive.google.com/drive/my-drive";

    public async Task<DriveScanResult> GetComparableFilesAsync(
        DriveService driveService,
        IProgress<DriveScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(driveService);

        RootDirectoryInfo rootDirectories = await GetRootDirectoriesAsync(
            driveService,
            cancellationToken);
        var folders = new Dictionary<string, FolderInfo>(StringComparer.Ordinal);
        var comparableFiles = new List<DriveFileInfo>();
        int itemsExamined = 0;
        int filesWithoutMd5Ignored = 0;
        string? pageToken = null;

        do
        {
            cancellationToken.ThrowIfCancellationRequested();

            var request = driveService.Files.List();
            request.Q = "trashed = false";
            request.PageSize = 1000;
            request.PageToken = pageToken;
            request.Corpora = "user";
            request.IncludeItemsFromAllDrives = true;
            request.SupportsAllDrives = true;
            request.Fields = RequestedFields;

            var response = await request.ExecuteAsync(cancellationToken);

            foreach (GoogleFile file in response.Files ?? [])
            {
                cancellationToken.ThrowIfCancellationRequested();
                itemsExamined++;

                if (file.Trashed == true)
                {
                    continue;
                }

                string name = string.IsNullOrWhiteSpace(file.Name)
                    ? "Archivo sin nombre"
                    : file.Name;
                string mimeType = file.MimeType ?? string.Empty;
                IReadOnlyList<string> parentIds = file.Parents?.ToArray() ?? [];

                if (string.Equals(mimeType, FolderMimeType, StringComparison.Ordinal))
                {
                    if (!string.IsNullOrWhiteSpace(file.Id))
                    {
                        folders[file.Id] = new FolderInfo(file.Id, name, parentIds);
                    }

                    continue;
                }

                if (mimeType.StartsWith(GoogleWorkspaceMimeTypePrefix, StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(file.Md5Checksum))
                {
                    filesWithoutMd5Ignored++;
                    continue;
                }

                if (file.Size is not long size || size < 0 || string.IsNullOrWhiteSpace(file.Id))
                {
                    continue;
                }

                comparableFiles.Add(new DriveFileInfo
                {
                    Id = file.Id,
                    Name = name,
                    MimeType = mimeType,
                    Size = size,
                    Md5Checksum = file.Md5Checksum,
                    ModifiedTime = file.ModifiedTimeDateTimeOffset,
                    ParentIds = parentIds,
                    SharedDriveId = file.DriveId
                });
            }

            pageToken = response.NextPageToken;
            progress?.Report(new DriveScanProgress(
                $"Leyendo metadatos: {itemsExamined:N0} elementos.",
                itemsExamined));
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        PopulatePaths(
            comparableFiles,
            folders,
            rootDirectories.NamesById,
            rootDirectories.MyDriveRootId,
            cancellationToken);

        return new DriveScanResult
        {
            ItemsExamined = itemsExamined,
            FilesWithoutMd5Ignored = filesWithoutMd5Ignored,
            ComparableFiles = comparableFiles
        };
    }

    private static async Task<RootDirectoryInfo> GetRootDirectoriesAsync(
        DriveService driveService,
        CancellationToken cancellationToken)
    {
        var namesById = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["root"] = "Mi unidad"
        };

        string? myDriveRootId = null;
        try
        {
            var rootRequest = driveService.Files.Get("root");
            rootRequest.Fields = "id";
            GoogleFile myDriveRoot = await rootRequest.ExecuteAsync(cancellationToken);
            myDriveRootId = myDriveRoot.Id;

            if (!string.IsNullOrWhiteSpace(myDriveRootId))
            {
                namesById[myDriveRootId] = "Mi unidad";
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // La ruta puede resolverse parcialmente aunque la consulta auxiliar falle.
        }

        return new RootDirectoryInfo(myDriveRootId, namesById);
    }

    private static void PopulatePaths(
        IEnumerable<DriveFileInfo> files,
        IReadOnlyDictionary<string, FolderInfo> folders,
        IReadOnlyDictionary<string, string> rootNamesById,
        string? myDriveRootId,
        CancellationToken cancellationToken)
    {
        var folderPathCache = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (DriveFileInfo file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            foreach (string parentId in file.ParentIds)
            {
                string? parentPath = ResolveFolderPath(
                    parentId,
                    folders,
                    rootNamesById,
                    file.SharedDriveId,
                    folderPathCache,
                    new HashSet<string>(StringComparer.Ordinal));

                if (string.IsNullOrWhiteSpace(parentPath))
                {
                    continue;
                }

                file.Path = $"{parentPath}/{file.Name}";
                file.ParentFolderId = parentId;
                file.ParentFolderUrl = BuildParentFolderUrl(parentId, myDriveRootId);
                break;
            }

            if (string.IsNullOrWhiteSpace(file.Path))
            {
                string rootName = string.IsNullOrWhiteSpace(file.SharedDriveId)
                    ? "Ubicaci\u00F3n desconocida"
                    : "Unidad compartida";
                file.Path = $"{rootName}/{file.Name}";
            }
        }
    }

    private static string? ResolveFolderPath(
        string folderId,
        IReadOnlyDictionary<string, FolderInfo> folders,
        IReadOnlyDictionary<string, string> rootNamesById,
        string? sharedDriveId,
        IDictionary<string, string> folderPathCache,
        ISet<string> visitedFolderIds)
    {
        if (rootNamesById.TryGetValue(folderId, out string? rootName))
        {
            return rootName;
        }

        if (!string.IsNullOrWhiteSpace(sharedDriveId) &&
            string.Equals(folderId, sharedDriveId, StringComparison.Ordinal))
        {
            return "Unidad compartida";
        }

        if (folderPathCache.TryGetValue(folderId, out string? cachedPath))
        {
            return cachedPath;
        }

        if (!visitedFolderIds.Add(folderId) || !folders.TryGetValue(folderId, out FolderInfo? folder))
        {
            return null;
        }

        string? parentPath = folder.ParentIds
            .Select(parentId => ResolveFolderPath(
                parentId,
                folders,
                rootNamesById,
                sharedDriveId,
                folderPathCache,
                visitedFolderIds))
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));

        visitedFolderIds.Remove(folderId);

        string path = string.IsNullOrWhiteSpace(parentPath)
            ? $"Ubicaci\u00F3n desconocida/{folder.Name}"
            : $"{parentPath}/{folder.Name}";
        folderPathCache[folderId] = path;
        return path;
    }

    private static string? BuildParentFolderUrl(string parentFolderId, string? myDriveRootId)
    {
        if (string.IsNullOrWhiteSpace(parentFolderId))
        {
            return null;
        }

        if (string.Equals(parentFolderId, "root", StringComparison.Ordinal) ||
            string.Equals(parentFolderId, myDriveRootId, StringComparison.Ordinal))
        {
            return MyDriveUrl;
        }

        return $"https://drive.google.com/drive/folders/{Uri.EscapeDataString(parentFolderId)}";
    }

    private sealed record FolderInfo(
        string Id,
        string Name,
        IReadOnlyList<string> ParentIds);

    private sealed record RootDirectoryInfo(
        string? MyDriveRootId,
        IReadOnlyDictionary<string, string> NamesById);
}
