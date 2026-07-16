namespace DriveDuplicateFinder.Models;

public sealed class DuplicateFileRow
{
    public required int GroupNumber { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string Size { get; init; }

    public required string ModifiedTime { get; init; }

    public required string Md5Checksum { get; init; }

    public string? ParentFolderUrl { get; init; }
}
