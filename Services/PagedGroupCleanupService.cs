using DriveDuplicateFinder.Data.Sqlite.Repositories;
using DriveDuplicateFinder.Models;
using DriveDuplicateFinder.Models.Persistence;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Coordinates paged cleanup preparation and execution. Normal Form1 cleanup actions remain
/// disconnected; the isolated demo can exercise the coordinator only with its in-memory transport.
/// </summary>
public sealed class PagedGroupCleanupService
{
    private const bool RealPagedCleanupExecutionEnabled = false;
    private const bool SingleCandidateTrialExecutionEnabled = false;
    public const long SingleCandidateTrialMaximumBytes = 10_000_000;
    private readonly Guid _owner = Guid.NewGuid();
    private readonly RecoverableFullScanService _scan;
    private readonly PersistedCleanupGroupReviewAdapter _adapter;
    private readonly ReviewStateRepository _reviews;
    private readonly GoogleDriveTrashService _trash;
    private readonly CleanupHistoryService _history;

    /// <summary>Hard, code-controlled gate. No UI setting or command-line argument can enable writes.</summary>
    public static bool IsRealExecutionEnabled => RealPagedCleanupExecutionEnabled;

    /// <summary>Code-only, disabled-by-default exception for one explicitly confirmed small candidate.</summary>
    public static bool IsSingleCandidateTrialExecutionEnabled => SingleCandidateTrialExecutionEnabled;

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

    /// <summary>
    /// Loads and validates the complete confirmed group from SQLite for a non-executable UI preview.
    /// This method performs no OAuth, Drive reads, history writes, or Drive writes.
    /// </summary>
    public async Task<PagedGroupCleanupLocalPreview> PrepareLocalPreviewAsync(
        DuplicateGroupIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(identity);
        cancellationToken.ThrowIfCancellationRequested();
        PersistedCleanupGroupReview localReview = await _adapter.BuildAsync(identity, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        DriveFileInfo[] members = localReview.Review.Group.Files.ToArray();
        if (members.LongLength != localReview.MemberCount ||
            localReview.Review.DecisionsByFileId.Count != localReview.MemberCount ||
            members.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != members.Length)
            throw new InvalidOperationException("SQLite no devolvió una revisión completa y consistente del grupo.");

        var entries = new List<PagedGroupCleanupLocalPreviewEntry>();
        long keepCount = 0;
        long candidateCount = 0;
        long undecidedCount = 0;
        foreach (DriveFileInfo member in members)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!localReview.Review.DecisionsByFileId.TryGetValue(member.Id, out DuplicateFileDecision decision))
                throw new InvalidOperationException("Falta una decisión persistida para un miembro del grupo.");
            switch (decision)
            {
                case DuplicateFileDecision.Keep:
                    keepCount++;
                    entries.Add(new PagedGroupCleanupLocalPreviewEntry(member, decision));
                    break;
                case DuplicateFileDecision.CandidateForTrash:
                    candidateCount++;
                    entries.Add(new PagedGroupCleanupLocalPreviewEntry(member, decision));
                    break;
                case DuplicateFileDecision.Undecided:
                    undecidedCount++;
                    break;
                default:
                    throw new InvalidOperationException("El grupo contiene una decisión local desconocida.");
            }
        }

        if (keepCount != localReview.KeepCount || candidateCount != localReview.CandidateCount ||
            undecidedCount != localReview.UndecidedCount || keepCount == 0 || candidateCount == 0)
            throw new InvalidOperationException("Las decisiones cambiaron o el grupo ya no es apto; vuelve a confirmarlo.");

        PagedGroupCleanupLocalPreviewEntry[] ordered = entries
            .OrderBy(entry => entry.Decision)
            .ThenBy(entry => entry.File.Id, StringComparer.Ordinal)
            .ToArray();
        return new PagedGroupCleanupLocalPreview(
            localReview.Identity,
            localReview.ConfirmedAtUtc,
            localReview.MemberCount,
            keepCount,
            candidateCount,
            undecidedCount,
            new System.Numerics.BigInteger(identity.SizeBytes) * candidateCount,
            Array.AsReadOnly(ordered));
    }

    public async Task<PagedGroupCleanupPlan> PreparePlanAsync(
        CleanupDriveSession cleanupSession,
        PagedGroupPreflightResult readOnlyPreflight,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default,
        bool singleCandidateTrial = false)
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

        if (singleCandidateTrial)
            EnsureSingleCandidateTrialIsValid(readOnlyPreflight.LocalReview, readOnlyPreflight.Preflight,
                requireAllKeepSnapshots: false);

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
        if (singleCandidateTrial)
            EnsureSingleCandidateTrialIsValid(current, fresh);
        return new PagedGroupCleanupPlan(_owner, current, fresh, singleCandidateTrial);
    }

    /// <summary>Creates a confirmation token only when the displayed Keep and Candidate IDs match exactly.</summary>
    public PagedGroupCleanupConfirmation ConfirmPlan(
        PagedGroupCleanupPlan plan,
        IEnumerable<string> confirmedKeepIds,
        IEnumerable<string> confirmedCandidateIds)
    {
        ValidateOwner(plan);
        if (plan.IsSingleCandidateTrial)
            EnsureSingleCandidateTrialIsValid(plan.LocalReview, plan.Preflight);
        string[] keeps = confirmedKeepIds?.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentNullException(nameof(confirmedKeepIds));
        string[] candidates = confirmedCandidateIds?.Distinct(StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray()
            ?? throw new ArgumentNullException(nameof(confirmedCandidateIds));
        if (!keeps.SequenceEqual(plan.KeepFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal) ||
            !candidates.SequenceEqual(plan.CandidateFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal), StringComparer.Ordinal))
            throw new InvalidOperationException("La confirmación no coincide exactamente con los archivos mostrados en el plan.");
        return new PagedGroupCleanupConfirmation(_owner, plan, keeps, candidates, plan.IsSingleCandidateTrial);
    }

    /// <summary>
    /// Executes only a single-group confirmation token. Real authenticated sessions are blocked by default;
    /// only explicitly marked in-memory simulation sessions may exercise the coordinator while the gate is closed.
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
        if (!cleanupSession.IsOfflineSimulation)
        {
            if (confirmation.IsSingleCandidateTrial)
            {
                if (!SingleCandidateTrialExecutionEnabled)
                    throw new PagedGroupCleanupExecutionDisabledException();
            }
            else if (!RealPagedCleanupExecutionEnabled)
            {
                throw new PagedGroupCleanupExecutionDisabledException();
            }
        }

        if (confirmation.IsSingleCandidateTrial)
            EnsureSingleCandidateTrialIsValid(plan.LocalReview, plan.Preflight);
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

        if (confirmation.IsSingleCandidateTrial)
            EnsureSingleCandidateTrialIsValid(finalLocal, lastPreflight);
        if (confirmation.IsSingleCandidateTrial && lastPreflight.CandidateFiles.Count != 1)
            throw new InvalidOperationException("La modalidad de prueba exige exactamente un candidato en el último control previo a escritura.");

        PagedCleanupTrialAuthorization? trialAuthorization = null;
        if (confirmation.IsSingleCandidateTrial)
        {
            CleanupFileSnapshot candidate = lastPreflight.CandidateFiles.Single();
            PagedCleanupTrialAuthorization expectedAuthorization = CreateTrialAuthorizationBinding(
                finalLocal, candidate.Id, candidate.Size ?? throw new InvalidOperationException(
                    "El tamaño del candidato debe seguir siendo conocido al vincular la autorización persistente."));
            trialAuthorization = await _reviews.GetAvailablePagedCleanupTrialAuthorizationAsync(expectedAuthorization, cancellationToken);
            if (trialAuthorization is null)
                throw new InvalidOperationException("No existe una autorización persistente de prueba vigente para esta cuenta, sesión, grupo y candidato.");
        }

        // One OAuth cleanup session may make at most one write attempt in the limited trial flow.
        if (confirmation.IsSingleCandidateTrial && !cleanupSession.TryBeginSingleCandidateTrialAttempt())
            throw new InvalidOperationException("La autorización de prueba de un candidato ya fue consumida; inicia una nueva revisión y autorización.");

        CleanupHistoryHandle history = await _history.CreatePendingAsync(finalLocal.Review, cancellationToken);
        await _reviews.InvalidateInventoryIfGroupSnapshotMatchesAsync(
            identity, finalLocal.Review.DecisionsByFileId, finalLocal.ConfirmedAtUtc, cancellationToken);

        // Repeat the immutable plan bound at the last point before delegating to Files.Update.
        if (confirmation.IsSingleCandidateTrial)
        {
            EnsureSingleCandidateTrialIsValid(finalLocal, lastPreflight);
            if (lastPreflight.CandidateFiles.Count != 1)
                throw new InvalidOperationException("La modalidad de prueba exige un único candidato antes de enviar a la papelera.");

            cancellationToken.ThrowIfCancellationRequested();
            if (trialAuthorization is null ||
                !await _reviews.TryConsumePagedCleanupTrialAuthorizationAsync(trialAuthorization, CancellationToken.None))
                throw new InvalidOperationException("La autorización persistente ya fue consumida, cambió o no pudo vincularse exactamente; no se ejecutó ninguna escritura.");
        }

        // This is the existing Files.Update(Trashed=true) path. It receives only the
        // fresh preflight's exact explicit-candidate snapshots; Keep/Undecided are absent.
        // The Google API HTTP handler otherwise permits retries. A lost PATCH response is
        // ambiguous, so this capability must make a single transport attempt only.
        cleanupSession.DriveService.HttpClient.MessageHandler.NumTries = 1;
        return await _trash.ExecuteAsync(cleanupSession.DriveService, lastPreflight, _history, history,
            progress, cancellationToken, stopAfterCurrentFileRequested);
    }

    internal static PagedCleanupTrialAuthorization CreateTrialAuthorizationBinding(
        PersistedCleanupGroupReview review,
        string candidateFileId,
        long candidateSizeBytes,
        string permitId = "")
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidateFileId);
        DuplicateGroupIdentity identity = review.Identity;
        if (review.MemberCount > PersistedCleanupGroupReviewAdapter.MaximumMembersPerGroup ||
            review.Review.DecisionsByFileId.Count != review.MemberCount ||
            review.Review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Keep) < 1 ||
            review.Review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash) != 1 ||
            review.Review.DecisionsByFileId.Values.Any(value => value is not DuplicateFileDecision.Keep and
                not DuplicateFileDecision.CandidateForTrash and not DuplicateFileDecision.Undecided) ||
            !review.Review.DecisionsByFileId.TryGetValue(candidateFileId, out DuplicateFileDecision decision) ||
            decision != DuplicateFileDecision.CandidateForTrash || candidateSizeBytes != identity.SizeBytes ||
            candidateSizeBytes > SingleCandidateTrialMaximumBytes)
            throw new InvalidOperationException("La autorización requiere una revisión confirmada con un Keep y exactamente un candidato pequeño.");

        string[] keepIds = review.Review.DecisionsByFileId.Where(pair => pair.Value == DuplicateFileDecision.Keep)
            .Select(pair => pair.Key).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        return new PagedCleanupTrialAuthorization(permitId, identity.Inventory.AccountKey, identity.Inventory.ScopeKey,
            identity.Inventory.ScanId, review.Review.StableId, identity.SizeBytes, identity.NormalizedChecksum,
            candidateFileId, candidateSizeBytes, review.ConfirmedAtUtc,
            PagedCleanupTrialAuthorization.ComputeKeepSetSha256(keepIds));
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

    /// <summary>Shared trial policy used before OAuth, during plan preparation, and again before writing.</summary>
    public static string? GetSingleCandidateTrialBlockReason(
        PersistedCleanupGroupReview localReview,
        CleanupPreflightResult preflight,
        bool requireAllKeepSnapshots = true)
    {
        ArgumentNullException.ThrowIfNull(localReview);
        ArgumentNullException.ThrowIfNull(preflight);
        DuplicateGroupReview review = localReview.Review;
        if (preflight.WasCancelled || !preflight.IsSuccessful)
            return "El preflight no está aprobado y completo.";
        if (review.Status is not DuplicateGroupReviewStatus.ReadyForCleanup and not DuplicateGroupReviewStatus.PartiallyReviewed ||
            review.ReviewedWithoutCleanup || localReview.ConfirmedAtUtc == default)
            return "El grupo no tiene una confirmación vigente.";
        if (review.Group.FileSize != localReview.Identity.SizeBytes ||
            !string.Equals(review.Group.Md5Checksum, localReview.Identity.NormalizedChecksum, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(review.StableId, preflight.Review.StableId, StringComparison.Ordinal) ||
            preflight.Review.Group.FileSize != review.Group.FileSize ||
            !string.Equals(preflight.Review.Group.Md5Checksum, review.Group.Md5Checksum, StringComparison.OrdinalIgnoreCase))
            return "La identidad del grupo no coincide con el preflight.";

        DriveFileInfo[] members = review.Group.Files.ToArray();
        if (members.Length != localReview.MemberCount ||
            members.Select(file => file.Id).Distinct(StringComparer.Ordinal).Count() != members.Length ||
            review.DecisionsByFileId.Count != members.Length ||
            members.Any(file => !review.DecisionsByFileId.ContainsKey(file.Id)) ||
            review.DecisionsByFileId.Values.Any(decision => decision is not DuplicateFileDecision.Keep and
                not DuplicateFileDecision.CandidateForTrash and not DuplicateFileDecision.Undecided))
            return "Las decisiones persistidas no cubren exactamente la membresía del grupo.";

        string[] keepIds = members.Where(file => review.DecisionsByFileId[file.Id] == DuplicateFileDecision.Keep)
            .Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        string[] candidateIds = members.Where(file => review.DecisionsByFileId[file.Id] == DuplicateFileDecision.CandidateForTrash)
            .Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        if (keepIds.Length == 0)
            return "Debe existir al menos un Keep explícito.";
        if (candidateIds.Length != 1)
            return "La prueba limitada exige exactamente un candidato explícito en un único grupo.";

        IEnumerable<CleanupFileSnapshot> remoteKeeps = preflight.KeepFiles.Count > 0
            ? preflight.KeepFiles
            : preflight.KeepFile is null ? Array.Empty<CleanupFileSnapshot>() : [preflight.KeepFile];
        string[] remoteKeepIds = remoteKeeps.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        string[] remoteCandidateIds = preflight.CandidateFiles.Select(file => file.Id).OrderBy(id => id, StringComparer.Ordinal).ToArray();
        bool keepSnapshotsMatch = requireAllKeepSnapshots
            ? keepIds.SequenceEqual(remoteKeepIds, StringComparer.Ordinal)
            : remoteKeepIds.Length > 0 && remoteKeepIds.All(id => keepIds.Contains(id, StringComparer.Ordinal));
        if (!keepSnapshotsMatch ||
            !candidateIds.SequenceEqual(remoteCandidateIds, StringComparer.Ordinal) ||
            keepIds.Intersect(candidateIds, StringComparer.Ordinal).Any())
            return "El preflight no coincide con todos los Keep y el único candidato explícito.";

        DriveFileInfo localCandidate = members.Single(file => file.Id == candidateIds[0]);
        CleanupFileSnapshot remoteCandidate = preflight.CandidateFiles.Single();
        if (localCandidate.Size < 0 || localCandidate.Size != localReview.Identity.SizeBytes ||
            localCandidate.Size > SingleCandidateTrialMaximumBytes ||
            remoteCandidate.Size is null || remoteCandidate.Size < 0 ||
            remoteCandidate.Size > SingleCandidateTrialMaximumBytes || remoteCandidate.Size != localCandidate.Size)
            return $"El tamaño del candidato debe conocerse, coincidir con el inventario y no superar {SingleCandidateTrialMaximumBytes:N0} bytes.";

        return null;
    }

    private static void EnsureSingleCandidateTrialIsValid(
        PersistedCleanupGroupReview localReview,
        CleanupPreflightResult preflight,
        bool requireAllKeepSnapshots = true)
    {
        string? reason = GetSingleCandidateTrialBlockReason(localReview, preflight, requireAllKeepSnapshots);
        if (reason is not null) throw new InvalidOperationException("Prueba limitada bloqueada: " + reason);
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

public sealed class PagedGroupCleanupExecutionDisabledException : InvalidOperationException
{
    public PagedGroupCleanupExecutionDisabledException()
        : base("La ejecución real de limpieza paginada está deshabilitada en esta versión. No se inició ninguna escritura.")
    {
    }
}

/// <summary>One explicitly decided member shown in a local-only preview.</summary>
public sealed record PagedGroupCleanupLocalPreviewEntry(DriveFileInfo File, DuplicateFileDecision Decision);

/// <summary>
/// Immutable local snapshot for display only. It is intentionally not accepted by ConfirmPlan
/// or ExecuteConfirmedAsync; remote preflight remains pending and execution is disabled.
/// </summary>
public sealed record PagedGroupCleanupLocalPreview(
    DuplicateGroupIdentity Identity,
    DateTimeOffset ConfirmedAtUtc,
    long MemberCount,
    long KeepCount,
    long CandidateCount,
    long UndecidedExcludedCount,
    System.Numerics.BigInteger PotentiallyRecoverableBytes,
    IReadOnlyList<PagedGroupCleanupLocalPreviewEntry> Entries)
{
    public bool RemotePreflightPending => true;
    public bool ExecutionEnabled => false;
}

public sealed class PagedGroupCleanupPlan
{
    internal PagedGroupCleanupPlan(
        Guid owner,
        PersistedCleanupGroupReview localReview,
        CleanupPreflightResult preflight,
        bool singleCandidateTrial = false)
    {
        Owner = owner;
        LocalReview = localReview;
        Preflight = preflight;
        IsSingleCandidateTrial = singleCandidateTrial;
        KeepFiles = Array.AsReadOnly(preflight.KeepFiles.ToArray());
        CandidateFiles = Array.AsReadOnly(preflight.CandidateFiles.ToArray());
    }

    internal Guid Owner { get; }
    internal PersistedCleanupGroupReview LocalReview { get; }
    internal CleanupPreflightResult Preflight { get; }
    public DuplicateGroupIdentity Identity => LocalReview.Identity;
    public IReadOnlyList<CleanupFileSnapshot> KeepFiles { get; }
    public IReadOnlyList<CleanupFileSnapshot> CandidateFiles { get; }
    public bool IsSingleCandidateTrial { get; }
}

public sealed class PagedGroupCleanupConfirmation
{
    private int _consumed;
    internal PagedGroupCleanupConfirmation(
        Guid owner,
        PagedGroupCleanupPlan plan,
        string[] keepIds,
        string[] candidateIds,
        bool singleCandidateTrial = false)
    { Owner = owner; Plan = plan; KeepIds = keepIds; CandidateIds = candidateIds; IsSingleCandidateTrial = singleCandidateTrial; }
    internal Guid Owner { get; }
    internal PagedGroupCleanupPlan Plan { get; }
    internal IReadOnlyList<string> KeepIds { get; }
    internal IReadOnlyList<string> CandidateIds { get; }
    internal bool IsSingleCandidateTrial { get; }
    internal bool TryConsume() => Interlocked.Exchange(ref _consumed, 1) == 0;
}
