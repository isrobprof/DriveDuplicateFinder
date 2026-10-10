using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder.Services;

public sealed record PagedGroupPreflightResult(
    PersistedCleanupGroupReview LocalReview,
    CleanupPreflightResult Preflight,
    bool RemoteFileReadsStarted,
    bool IsStale)
{
    public bool ExecutionEnabled => false;
}

/// <summary>Coordinates one read-only remote preflight for a current, persisted review group.</summary>
public sealed class PagedGroupPreflightService
{
    private readonly RecoverableFullScanService _scanService;
    private readonly PersistedCleanupGroupReviewAdapter _adapter;
    private readonly GoogleDriveTrashService _trashService;

    public PagedGroupPreflightService(
        RecoverableFullScanService scanService,
        PersistedCleanupGroupReviewAdapter adapter,
        GoogleDriveTrashService trashService)
    {
        _scanService = scanService ?? throw new ArgumentNullException(nameof(scanService));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _trashService = trashService ?? throw new ArgumentNullException(nameof(trashService));
    }

    public async Task<PagedGroupPreflightResult> CheckOneGroupAsync(
        DriveService readOnlyDriveService,
        DuplicateGroupIdentity identity,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(readOnlyDriveService);
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();

        FullScanPreparation currentIdentity = await _scanService.PrepareAsync(readOnlyDriveService, cancellationToken);
        EnsureAccountAndScope(currentIdentity, identity.Inventory);
        PersistedCleanupGroupReview localReview = await _adapter.BuildAsync(identity, cancellationToken);
        DuplicateGroupReview probeReview = localReview.CreateReadOnlyPreflightReview();
        CleanupPlanValidationResult localValidation = _trashService.ValidateLocalPlan(probeReview, resultsAreObsolete: false);

        bool remoteFileReadsStarted = localValidation.IsAllowed;
        // Reuse the existing service contract. When local validation fails, it returns before Files.Get.
        CleanupPreflightResult preflight = await _trashService.PreflightAsync(
            readOnlyDriveService, probeReview, resultsAreObsolete: false,
            progress: progress, cancellationToken: cancellationToken);

        if (preflight.WasCancelled || cancellationToken.IsCancellationRequested)
            return new PagedGroupPreflightResult(localReview, preflight, remoteFileReadsStarted, IsStale: false);

        FullScanPreparation afterRemoteRead = await _scanService.PrepareAsync(readOnlyDriveService, cancellationToken);
        EnsureAccountAndScope(afterRemoteRead, identity.Inventory);
        try
        {
            PersistedCleanupGroupReview currentReview = await _adapter.BuildAsync(identity, cancellationToken);
            bool sameLocalRevision = localReview.HasSameDecisionSnapshot(currentReview);
            if (!sameLocalRevision)
                MarkStale(preflight);
            return new PagedGroupPreflightResult(localReview, preflight, remoteFileReadsStarted, IsStale: !sameLocalRevision);
        }
        catch (InvalidOperationException)
        {
            MarkStale(preflight);
            return new PagedGroupPreflightResult(localReview, preflight, remoteFileReadsStarted, IsStale: true);
        }
    }

    private static void MarkStale(CleanupPreflightResult preflight)
    {
        preflight.IsSuccessful = false;
        preflight.FailureKind = CleanupPreflightFailureKind.RemoteFailure;
        preflight.ValidationMessages.Add("El resultado se descartó porque el grupo, la confirmación o las decisiones cambiaron durante el preflight.");
    }

    private static void EnsureAccountAndScope(FullScanPreparation current, ScanInventoryIdentity inventory)
    {
        if (!string.Equals(current.AccountKey, inventory.AccountKey, StringComparison.Ordinal) ||
            !string.Equals(current.ScopeKey, inventory.ScopeKey, StringComparison.Ordinal) ||
            !string.Equals(current.ScopeKey, RecoverableFullScanService.ScopeKeyValue, StringComparison.Ordinal))
            throw new InvalidOperationException("La conexión actual no corresponde a la cuenta y alcance del inventario seleccionado.");
    }
}
