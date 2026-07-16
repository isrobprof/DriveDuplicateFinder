namespace DriveDuplicateFinder.Models;

public sealed record DriveScanProgress(
    string Status,
    int ProcessedItems,
    int? TotalItems = null)
{
    public int? Percentage => TotalItems is > 0
        ? (int)Math.Clamp(ProcessedItems * 100L / TotalItems.Value, 0, 100)
        : null;
}
