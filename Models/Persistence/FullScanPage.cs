namespace DriveDuplicateFinder.Models.Persistence;

public sealed record FullScanPage(
    IReadOnlyCollection<DriveFileCacheRecord> Files,
    string? NextPageToken,
    long ItemsReturned);

public interface IFullScanPageSource
{
    Task<FullScanPage> ReadPageAsync(string? pageToken, CancellationToken cancellationToken);
}
