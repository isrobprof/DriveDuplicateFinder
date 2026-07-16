namespace DriveDuplicateFinder.Models;

public sealed class DriveScanResult
{
    public required int ItemsExamined { get; init; }

    public required int FilesWithoutMd5Ignored { get; init; }

    public required IReadOnlyList<DriveFileInfo> ComparableFiles { get; init; }
}
