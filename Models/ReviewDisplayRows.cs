namespace DriveDuplicateFinder.Models;

public sealed class ReviewGroupRow
{
    public required string StableGroupId { get; init; }

    public required int GroupNumber { get; init; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string Size { get; init; }

    public required string ModifiedTime { get; init; }

    public required string Md5Checksum { get; init; }

    public required string Status { get; init; }

    public string? ParentFolderUrl { get; init; }
}

public sealed class ReviewFileRow
{
    public required string FileId { get; init; }

    public required DuplicateFileDecision Decision { get; set; }

    public required string Name { get; init; }

    public required string Path { get; init; }

    public required string Size { get; init; }

    public required string CreatedTime { get; init; }

    public required string ModifiedTime { get; init; }

    public required string Owner { get; init; }

    public required string Flags { get; init; }

    public required string Recommendation { get; init; }

    public required string Warnings { get; init; }

    public string? ParentFolderUrl { get; init; }
}

public sealed class CleanupPlanRow
{
    public required int GroupNumber { get; init; }

    public required string KeepFileName { get; init; }

    public required string CandidateFileName { get; init; }

    public required string CandidateFileId { get; init; }

    public required string Path { get; init; }

    public required long Size { get; init; }

    public required string Md5Checksum { get; init; }

    public required string Warnings { get; init; }
}
