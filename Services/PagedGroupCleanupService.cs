using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Prepares a one-group cleanup capability for a future explicit confirmation flow.
/// It is deliberately not referenced by Form1: the paged UI remains read-only in this phase.
/// </summary>
public sealed class PagedGroupCleanupService
{
    private readonly Guid _owner = Guid.NewGuid();
    private readonly RecoverableFullScanService _scan;
    private readonly PersistedCleanupGroupReviewAdapter _adapter;
    private readonly ReviewStateRepository _reviews;
    private readonly GoogleDriveTrashService _trash;
    private readonly CleanupHistoryService _history;

    public PagedGroupCleanupService(
        RecoverableFullScanService scan,
        PersistedCleanupGroupReviewAdapter adapter,
        ReviewStateRepository reviews,
        GoogleDriveTrashService trash,
        CleanupHistoryService history)
    {
        _scan = scan ?? throw new ArgumentNullException(nameof(scan));
        _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        _reviews = reviews ?? throw new ArgumentNullException(nameof(reviews));
        _trash = trash ?? throw new ArgumentNullException(nameof(trash));
        _history = history ?? throw new ArgumentNullException(nameof(history));
    }

    public async Task<PagedGroupCleanupPlan> PreparePlanAsync(
        CleanupDriveSession cleanupSession,
        PagedGroupPreflightResult readOnlyPreflight,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cleanupSession);
        ArgumentNullException.ThrowIfNull(readOnlyPreflight);
        if (readOnlyPreflight.IsStale || !readOnlyPreflight.Preflight.IsSuccessful ||
            !string.Equals(readOnlyPreflight.LocalReview.Review.StableId, readOnlyPreflight.Preflight.Review.StableId, StringComparison.Ordinal) ||
            readOnlyPreflight.LocalReview.Review.Group.FileSize != readOnlyPreflight.Preflight.Review.Group.FileSize ||
            !string.Equals(readOnlyPreflight.LocalReview.Review.Group.Md5Checksum,
                readOnlyPreflight.Preflight.Review.Group.Md5Checksum, StringComparison.OrdinalIgnoreCase) ||
            !readOnlyPreflight.Preflight.CandidateFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(readOnlyPreflight.LocalReview.Review.DecisionsByFileId
                    .Where(pair => pair.Value == DuplicateFileDecision.CandidateForTrash)
                    .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("Se requiere el preflight aprobado y vigente del mismo grupo.");

        DuplicateGroupIdentity identity = readOnlyPreflight.LocalReview.Identity;
        await EnsureIdentityAsync(cleanupSession.DriveService, identity.Inventory, cancellationToken);
        PersistedCleanupGroupReview current = await _adapter.BuildAsync(identity, cancellationToken);
        EnsureSameSnapshot(readOnlyPreflight.LocalReview, current);

        CleanupPreflightResult fresh = await _trash.PreflightPersistedPlanAsync(
            cleanupSession.DriveService, current.CreatePersistedPreflightReview(), cancellationToken);
        if (!fresh.IsSuccessful || fresh.WasCancelled)
            throw new InvalidOperationException("El preflight con la autorización de limpieza no fue aprobado: " +
                string.Join(" ", fresh.ValidationMessages));

        await EnsureIdentityAsync(cleanupSession.DriveService, identity.Inventory, cancellationToken);
        PersistedCleanupGroupReview afterRead = await _adapter.BuildAsync(identity, cancellationToken);
        EnsureSameSnapshot(current, afterRead);
        VerifyExactRemoteSets(current, fresh);
        VerifyPriorRemoteState(readOnlyPreflight.Preflight, fresh);
        return new PagedGroupCleanupPlan(_owner, current, fresh);
    }

    /// <summary>Creates a confirmation token only when the displayed Keep and Candidate IDs match exactly.</summary>
    public PagedGroupCleanupConfirmation ConfirmPlan(
        PagedGroupCleanupPlan plan,
        IEnumerable<string> confirmedKeepIds,
        IEnumerable<string> confirmedCandidateIds)
    {
        ValidateOwner(plan);
        string[] keeps = confirmedKeepIds?.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentNullException(nameof(confirmedKeepIds));
        string[] candidates = confirmedCandidateIds?.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentNullException(nameof(confirmedCandidateIds));
        if (!keeps.SequenceEqual(plan.KeepFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal) ||
            !candidates.SequenceEqual(plan.CandidateFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("La confirmación no coincide exactamente con los archivos mostrados en el plan.");
        return new PagedGroupCleanupConfirmation(_owner, plan, keeps, candidates);
    }

    /// <summary>
    /// Executes only a single-group confirmation token. No current UI calls this method.
    /// The local snapshot is atomically checked and invalidated before the existing trash service is invoked.
    /// </summary>
    public async Task<CleanupOperationResult> ExecuteConfirmedAsync(
        CleanupDriveSession cleanupSession,
        PagedGroupCleanupConfirmation confirmation,
        IProgress<(int Current, int Total, string Name)>? progress = null,
        CancellationToken cancellationToken = default,
        Func<bool>? stopAfterCurrentFileRequested = null)
    {
        ArgumentNullException.ThrowIfNull(cleanupSession);
        ArgumentNullException.ThrowIfNull(confirmation);
        if (confirmation.Owner != _owner || !confirmation.TryConsume())
            throw new InvalidOperationException("La confirmación no pertenece a este servicio o ya fue utilizada.");

        PagedGroupCleanupPlan plan = confirmation.Plan;
        if (!confirmation.KeepIds.OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(plan.KeepFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal) ||
            !confirmation.CandidateIds.OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(plan.CandidateFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("La confirmación ya no coincide con el plan exacto presentado.");
        DuplicateGroupIdentity identity = plan.LocalReview.Identity;
        await EnsureIdentityAsync(cleanupSession.DriveService, identity.Inventory, cancellationToken);
        PersistedCleanupGroupReview current = await _adapter.BuildAsync(identity, cancellationToken);
        EnsureSameSnapshot(plan.LocalReview, current);

        CleanupPreflightResult lastPreflight = await _trash.PreflightPersistedPlanAsync(
            cleanupSession.DriveService, current.CreatePersistedPreflightReview(), cancellationToken);
        if (!lastPreflight.IsSuccessful || lastPreflight.WasCancelled)
            throw new InvalidOperationException("El preflight final no fue aprobado; no se ejecutó ninguna escritura.");

        await EnsureIdentityAsync(cleanupSession.DriveService, identity.Inventory, cancellationToken);
        PersistedCleanupGroupReview finalLocal = await _adapter.BuildAsync(identity, cancellationToken);
        EnsureSameSnapshot(plan.LocalReview, finalLocal);
        VerifyExactRemoteSets(current, lastPreflight);
        if (!SameRemoteSnapshots(plan.Preflight, lastPreflight))
            throw new InvalidOperationException("Los metadatos remotos cambiaron desde la confirmación; no se ejecutó ninguna escritura.");

        CleanupHistoryHandle history = await _history.CreatePendingAsync(finalLocal.Review, cancellationToken);
        await _reviews.InvalidateInventoryIfGroupSnapshotMatchesAsync(
            identity, finalLocal.Review.DecisionsByFileId, finalLocal.ConfirmedAtUtc, cancellationToken);

        // This is the existing Files.Update(Trashed=true) path. It receives only the
        // fresh preflight's exact explicit-candidate snapshots; Keep/Undecided are absent.
        // The Google API HTTP handler otherwise permits retries. A lost PATCH response is
        // ambiguous, so this capability must make a single transport attempt only.
        cleanupSession.DriveService.HttpClient.MessageHandler.NumTries = 1;
        return await _trash.ExecuteAsync(cleanupSession.DriveService, lastPreflight, _history, history,
            progress, cancellationToken, stopAfterCurrentFileRequested);
    }

    private async Task EnsureIdentityAsync(DriveService drive, ScanInventoryIdentity expected, CancellationToken token)
    {
        FullScanPreparation current = await _scan.PrepareAsync(drive, token);
        if (!string.Equals(current.AccountKey, expected.AccountKey, StringComparison.Ordinal) ||
            !string.Equals(current.ScopeKey, expected.ScopeKey, StringComparison.Ordinal) ||
            !string.Equals(current.ScopeKey, RecoverableFullScanService.ScopeKeyValue, StringComparison.Ordinal))
            throw new InvalidOperationException("La sesión de limpieza corresponde a otra cuenta o el inventario no usa el alcance Full esperado.");
    }

    private static void EnsureSameSnapshot(PersistedCleanupGroupReview expected, PersistedCleanupGroupReview actual)
    {
        if (!expected.HasSameDecisionSnapshot(actual))
            throw new InvalidOperationException("El grupo, su confirmación o sus decisiones cambiaron; hay que preparar un plan nuevo.");
    }

    private static void VerifyExactRemoteSets(PersistedCleanupGroupReview local, CleanupPreflightResult preflight)
    {
        string[] expectedKeeps = local.Review.DecisionsByFileId.Where(pair => pair.Value == DuplicateFileDecision.Keep)
            .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        string[] expectedCandidates = local.Review.DecisionsByFileId.Where(pair => pair.Value == DuplicateFileDecision.CandidateForTrash)
            .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (!preflight.KeepFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(expectedKeeps, StringComparer.Ordinal) ||
            !preflight.CandidateFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(expectedCandidates, StringComparer.Ordinal) ||
            preflight.KeepFiles.Any(file => expectedCandidates.Contains(file.Id, StringComparer.Ordinal)) ||
            preflight.CandidateFiles.Any(file => !expectedCandidates.Contains(file.Id, StringComparer.Ordinal)))
            throw new InvalidOperationException("El preflight no coincide exactamente con Keeps y candidatos explícitos del grupo.");
    }

    private static bool SameRemoteSnapshots(CleanupPreflightResult first, CleanupPreflightResult second) =>
        SameSnapshots(first.KeepFiles, second.KeepFiles) && SameSnapshots(first.CandidateFiles, second.CandidateFiles);

    private static void VerifyPriorRemoteState(CleanupPreflightResult prior, CleanupPreflightResult fresh)
    {
        if (prior.KeepFile is null)
            throw new InvalidOperationException("El preflight previo no contiene el Keep comprobado.");
        CleanupFileSnapshot? currentKeep = fresh.KeepFiles.SingleOrDefault(file => file.Id == prior.KeepFile.Id);
        if (currentKeep is null || !SameSnapshots([prior.KeepFile], [currentKeep]) ||
            !SameSnapshots(prior.CandidateFiles, fresh.CandidateFiles))
            throw new InvalidOperationException("Los metadatos remotos cambiaron desde el preflight previo; vuelve a comprobar el grupo.");
    }

    private static bool SameSnapshots(IReadOnlyList<CleanupFileSnapshot> first, IReadOnlyList<CleanupFileSnapshot> second)
    {
        if (first.Count != second.Count) return false;
        var right = second.ToDictionary(file => file.Id, StringComparer.Ordinal);
        return first.All(left => right.TryGetValue(left.Id, out CleanupFileSnapshot? current) &&
            left.Name == current.Name && left.MimeType == current.MimeType && left.Size == current.Size &&
            string.Equals(left.Md5Checksum, current.Md5Checksum, StringComparison.OrdinalIgnoreCase) &&
            left.ModifiedTime == current.ModifiedTime && left.Version == current.Version &&
            left.Trashed == current.Trashed && left.OwnedByMe == current.OwnedByMe &&
            left.IsShared == current.IsShared && left.IsStarred == current.IsStarred &&
            left.CanTrash == current.CanTrash && left.DriveId == current.DriveId &&
            new HashSet<string>(left.ParentIds, StringComparer.Ordinal).SetEquals(current.ParentIds));
    }

    private void ValidateOwner(PagedGroupCleanupPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.Owner != _owner) throw new InvalidOperationException("El plan pertenece a otra sesión de servicio.");
    }
}

public sealed class PagedGroupCleanupPlan
{
    internal PagedGroupCleanupPlan(Guid owner, PersistedCleanupGroupReview localReview, CleanupPreflightResult preflight)
    {
        Owner = owner;
        LocalReview = localReview;
        Preflight = preflight;
        KeepFiles = Array.AsReadOnly(preflight.KeepFiles.ToArray());
        CandidateFiles = Array.AsReadOnly(preflight.CandidateFiles.ToArray());
    }

    internal Guid Owner { get; }
    internal PersistedCleanupGroupReview LocalReview { get; }
    internal CleanupPreflightResult Preflight { get; }
    public DuplicateGroupIdentity Identity => LocalReview.Identity;
    public IReadOnlyList<CleanupFileSnapshot> KeepFiles { get; }
    public IReadOnlyList<CleanupFileSnapshot> CandidateFiles { get; }
}

public sealed class PagedGroupCleanupConfirmation
{
    private int _consumed;
    internal PagedGroupCleanupConfirmation(Guid owner, PagedGroupCleanupPlan plan, string[] keepIds, string[] candidateIds)
    { Owner = owner; Plan = plan; KeepIds = keepIds; CandidateIds = candidateIds; }
    internal Guid Owner { get; }
    internal PagedGroupCleanupPlan Plan { get; }
    internal IReadOnlyList<string> KeepIds { get; }
    internal IReadOnlyList<string> CandidateIds { get; }
    internal bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
}
