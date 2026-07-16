using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

public sealed class DuplicateFinderService
{
    public IReadOnlyList<DuplicateGroup> FindDuplicates(
        IEnumerable<DriveFileInfo> files,
        IProgress<DriveScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);

        DriveFileInfo[] fileList = files.ToArray();
        var groupedFiles = new Dictionary<string, List<DriveFileInfo>>(StringComparer.OrdinalIgnoreCase);

        for (int index = 0; index < fileList.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DriveFileInfo file = fileList[index];

            if (file.Size < 0 || string.IsNullOrWhiteSpace(file.Md5Checksum))
            {
                continue;
            }

            string key = $"{file.Size}:{file.Md5Checksum}";
            if (!groupedFiles.TryGetValue(key, out List<DriveFileInfo>? groupedFileList))
            {
                groupedFileList = [];
                groupedFiles.Add(key, groupedFileList);
            }

            groupedFileList.Add(file);

            if (index % 100 == 0 || index == fileList.Length - 1)
            {
                progress?.Report(new DriveScanProgress(
                    $"Buscando duplicados: {index + 1:N0} de {fileList.Length:N0} archivos.",
                    index + 1,
                    fileList.Length));
            }
        }

        var unorderedGroups = groupedFiles.Values
            .Where(group => group.Count > 1)
            .Select(group => new
            {
                Md5Checksum = group[0].Md5Checksum,
                FileSize = group[0].Size,
                Files = group
                    .OrderBy(file => file.Path, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(file => file.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToArray()
            })
            .OrderByDescending(group => group.FileSize * (long)(group.Files.Length - 1))
            .ToArray();

        return unorderedGroups
            .Select((group, index) => new DuplicateGroup
            {
                GroupNumber = index + 1,
                Md5Checksum = group.Md5Checksum,
                FileSize = group.FileSize,
                Files = group.Files
            })
            .ToArray();
    }
}
