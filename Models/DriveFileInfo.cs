namespace DriveDuplicateFinder.Models;

public sealed class DriveFileInfo
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    public required string MimeType { get; init; }

    public long Size { get; init; }

    public required string Md5Checksum { get; init; }

    public DateTimeOffset? ModifiedTime { get; init; }

    public IReadOnlyList<string> ParentIds { get; init; } = Array.Empty<string>();

    public string? SharedDriveId { get; init; }

    public string Path { get; set; } = string.Empty;

    public string? ParentFolderId { get; set; }

    public string? ParentFolderUrl { get; set; }
}
