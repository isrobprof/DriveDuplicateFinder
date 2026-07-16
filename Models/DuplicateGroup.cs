namespace DriveDuplicateFinder.Models;

public sealed class DuplicateGroup
{
    public required int GroupNumber { get; init; }

    public required string Md5Checksum { get; init; }

    public long FileSize { get; init; }

    public required IReadOnlyList<DriveFileInfo> Files { get; init; }

    public long RecoverableBytes => FileSize * Math.Max(0, Files.Count - 1);
}
