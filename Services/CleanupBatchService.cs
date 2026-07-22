using DriveDuplicateFinder.Models;
using Google.Apis.Drive.v3;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Coordina la limpieza real de varios grupos sin duplicar el preflight ni la escritura de la limpieza individual.
/// </summary>
public sealed class CleanupBatchService
{
    public const int MaxCleanupBatchGroups = 25;
    public const int MaxCleanupBatchCandidateFiles = 100;

    private readonly GoogleDriveTrashService _trashService;

    public CleanupBatchService(GoogleDriveTrashService trashService)
    {
        _trashService = trashService ?? throw new ArgumentNullException(nameof(trashService));
    }

    public async Task<CleanupBatchPreview> CreatePreviewAsync(
        IEnumerable<string> selectedStableIds,
        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId,
        DriveService cleanupDriveService,
        bool resultsAreObsolete,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(selectedStableIds);
        ArgumentNullException.ThrowIfNull(reviewsByStableId);
        ArgumentNullException.ThrowIfNull(cleanupDriveService);

        string[] selectedIds = selectedStableIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => reviewsByStableId.TryGetValue(id, out DuplicateGroupReview? review) ? review.Group.GroupNumber : int.MaxValue)
            .ThenBy(id => id, StringComparer.Ordinal)
            .ToArray();
        var plans = new List<CleanupBatchGroupPlan>();
        var excluded = new List<CleanupBatchExcludedGroup>();

        foreach (string stableId in selectedIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reviewsByStableId.TryGetValue(stableId, out DuplicateGroupReview? review))
            {
                excluded.Add(Excluded(stableId, "Grupo no disponible", CleanupBatchExclusionReason.GroupNotFound, "El grupo ya no existe en los resultados actuales."));
                continue;
            }

            string displayName = DisplayName(review);
            if (resultsAreObsolete)
            {
                excluded.Add(Excluded(stableId, displayName, CleanupBatchExclusionReason.ResultsAreStale, "Los resultados están obsoletos. Debes repetir la búsqueda."));
                continue;
            }

            if (!TryValidateLocal(review, out CleanupBatchExclusionReason reason, out string detail))
            {
                excluded.Add(Excluded(stableId, displayName, reason, detail));
                continue;
            }

            progress?.Report($"Comprobando el grupo {displayName} antes de mostrar la vista previa...");
            CleanupPreflightResult preflight = await _trashService.PreflightAsync(
                cleanupDriveService,
                review,
                resultsAreObsolete: false,
                progress,
                cancellationToken);
            if (!preflight.IsSuccessful || preflight.KeepFile is null || preflight.CandidateFiles.Count == 0)
            {
                string failureDetail = preflight.WasCancelled
                    ? "El preflight se canceló antes de modificar Google Drive."
                    : string.Join(Environment.NewLine, preflight.ValidationMessages.DefaultIfEmpty("El preflight no confirmó la seguridad del grupo."));
                excluded.Add(Excluded(stableId, displayName, CleanupBatchExclusionReason.PreflightFailed, failureDetail));
                continue;
            }

            CleanupFileSnapshot keep = Clone(preflight.KeepFile);
            CleanupFileSnapshot[] candidates = preflight.CandidateFiles.OrderBy(file => file.Id, StringComparer.Ordinal).Select(Clone).ToArray();
            plans.Add(new CleanupBatchGroupPlan
            {
                StableGroupId = stableId,
                DisplayName = displayName,
                KeepFile = keep,
                Candidates = candidates,
                Snapshot = new CleanupGroupSnapshot
                {
                    StableGroupId = stableId,
                    Status = review.Status,
                    ReviewedWithoutCleanup = review.ReviewedWithoutCleanup,
                    LastModifiedUtc = review.LastModifiedUtc,
                    DecisionsByFileId = new Dictionary<string, DuplicateFileDecision>(review.DecisionsByFileId, StringComparer.Ordinal),
                    KeepFile = Clone(keep),
                    CandidateFiles = candidates.Select(Clone).ToArray()
                }
            });
        }

        int candidateCount = plans.Sum(plan => plan.Candidates.Count);
        bool limitsExceeded = selectedIds.Length > MaxCleanupBatchGroups || candidateCount > MaxCleanupBatchCandidateFiles;
        string? limitMessage = limitsExceeded
            ? $"El lote contiene {selectedIds.Length:N0} grupos y {candidateCount:N0} archivos candidatos.{Environment.NewLine}{Environment.NewLine}" +
              $"El máximo permitido es de {MaxCleanupBatchGroups:N0} grupos y {MaxCleanupBatchCandidateFiles:N0} archivos candidatos. Reduce la selección antes de continuar."
            : null;

        return new CleanupBatchPreview
        {
            BatchId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            SelectedGroupCount = selectedIds.Length,
            ApplicableGroups = plans,
            ExcludedGroups = excluded,
            CandidateFileCount = candidateCount,
            CandidateBytes = plans.Sum(plan => plan.Candidates.Sum(file => file.Size ?? 0)),
            LimitsExceeded = limitsExceeded,
            LimitMessage = limitMessage
        };
    }

    public async Task<CleanupBatchResult> ExecuteAsync(
        CleanupBatchPreview preview,
        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId,
        DriveService cleanupDriveService,
        bool resultsAreObsolete,
        CleanupHistoryService historyService,
        CleanupBatchHistoryHandle batchHistory,
        Func<bool> stopAfterCurrentFileRequested,
        IProgress<CleanupBatchProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(reviewsByStableId);
        ArgumentNullException.ThrowIfNull(cleanupDriveService);
        ArgumentNullException.ThrowIfNull(historyService);
        ArgumentNullException.ThrowIfNull(batchHistory);
        ArgumentNullException.ThrowIfNull(stopAfterCurrentFileRequested);

        var result = new CleanupBatchResult
        {
            BatchId = preview.BatchId,
            Status = CleanupBatchStatus.Pending,
            StartedAt = DateTimeOffset.UtcNow,
            PlannedGroupCount = preview.ApplicableGroupCount,
            PlannedFileCount = preview.CandidateFileCount,
            ExcludedGroups = preview.ExcludedGroups
        };

        if (!preview.CanExecute)
        {
            return await CompleteAsync(result, batchHistory, historyService, CleanupBatchStatus.FailedBeforeChanges,
                new CleanupFailureInfo { Stage = "BatchValidation", Message = preview.LimitMessage ?? "No hay grupos aptos para ejecutar el lote." });
        }

        try
        {
            progress?.Report(new CleanupBatchProgress("Revalidando lote", 0, preview.ApplicableGroupCount, 0, preview.CandidateFileCount, null, null, 0, 0));
            await RevalidateAsync(preview, reviewsByStableId, cleanupDriveService, resultsAreObsolete, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return await CompleteAsync(result, batchHistory, historyService, CleanupBatchStatus.CancelledBeforeChanges,
                new CleanupFailureInfo { Stage = "Cancellation", Message = "El lote se canceló antes de modificar Google Drive." });
        }
        catch (Exception)
        {
            return await CompleteAsync(result, batchHistory, historyService, CleanupBatchStatus.FailedBeforeChanges,
                new CleanupFailureInfo { Stage = "BatchValidation", Message = "La información de uno o varios grupos cambió después de generar la vista previa. Vuelve a preparar el lote." });
        }

        int globalFileIndex = 0;
        for (int groupIndex = 0; groupIndex < preview.ApplicableGroups.Count; groupIndex++)
        {
            CleanupBatchGroupPlan plan = preview.ApplicableGroups[groupIndex];
            if (stopAfterCurrentFileRequested())
            {
                MarkUnprocessed(result, preview.ApplicableGroups.Skip(groupIndex));
                return await CompleteAsync(result, batchHistory, historyService,
                    result.TrashedFileCount > 0 ? CleanupBatchStatus.CancelledAfterChanges : CleanupBatchStatus.CancelledBeforeChanges,
                    new CleanupFailureInfo { Stage = "Cancellation", Message = "El usuario pidió detener el lote antes del siguiente archivo.", StableGroupId = plan.StableGroupId });
            }

            if (!reviewsByStableId.TryGetValue(plan.StableGroupId, out DuplicateGroupReview? review))
            {
                result.Groups.Add(new CleanupBatchGroupResult { StableGroupId = plan.StableGroupId, DisplayName = plan.DisplayName, Status = CleanupBatchGroupStatus.FailedBeforeChanges, Detail = "El grupo ya no existe." });
                return await CompleteAsync(result, batchHistory, historyService, CleanupBatchStatus.FailedBeforeChanges,
                    new CleanupFailureInfo { Stage = "GroupPreflight", Message = "Uno de los grupos ya no existe. Vuelve a preparar el lote.", StableGroupId = plan.StableGroupId });
            }

            CleanupHistoryHandle groupHistory;
            try
            {
                groupHistory = await historyService.CreatePendingAsync(review, cancellationToken);
            }
            catch (Exception)
            {
                result.Groups.Add(new CleanupBatchGroupResult { StableGroupId = plan.StableGroupId, DisplayName = plan.DisplayName, Status = CleanupBatchGroupStatus.FailedBeforeChanges, Detail = "No se pudo iniciar el historial obligatorio." });
                return await CompleteAsync(result, batchHistory, historyService, CleanupBatchStatus.FailedBeforeChanges,
                    new CleanupFailureInfo { Stage = "HistoryStart", Message = "No se pudo iniciar el historial obligatorio. Google Drive no se ha modificado.", StableGroupId = plan.StableGroupId });
            }

            CleanupPreflightResult preflight = ToPreflight(review, plan);
            var groupProgress = new Progress<(int Current, int Total, string Name)>(file =>
            {
                progress?.Report(new CleanupBatchProgress(
                    file.Current == 0 ? $"Procesando grupo {groupIndex + 1} de {preview.ApplicableGroupCount}" : "Enviando archivo a la papelera y verificando resultado",
                    groupIndex + 1,
                    preview.ApplicableGroupCount,
                    globalFileIndex + file.Current,
                    preview.CandidateFileCount,
                    file.Name,
                    plan.Candidates.FirstOrDefault(candidate => candidate.Name == file.Name)?.Path,
                    result.TrashedFileCount,
                    result.TrashedBytes));
            });

            try
            {
                // No se pasa un token cancelable durante la escritura: la solicitud actual se verifica antes de detener el lote.
                CleanupOperationResult operation = await _trashService.ExecuteAsync(
                    cleanupDriveService,
                    preflight,
                    historyService,
                    groupHistory,
                    groupProgress,
                    CancellationToken.None,
                    stopAfterCurrentFileRequested);
                globalFileIndex += plan.Candidates.Count;
                CleanupBatchGroupStatus groupStatus = operation.Record.Status switch
                {
                    CleanupOperationStatus.Completed => CleanupBatchGroupStatus.Completed,
                    CleanupOperationStatus.Partial => CleanupBatchGroupStatus.Partial,
                    CleanupOperationStatus.Cancelled => operation.Record.ProcessedFiles > 0
                        ? CleanupBatchGroupStatus.Partial
                        : CleanupBatchGroupStatus.NotProcessed,
                    _ => CleanupBatchGroupStatus.FailedBeforeChanges
                };
                result.Groups.Add(new CleanupBatchGroupResult
                {
                    StableGroupId = plan.StableGroupId,
                    DisplayName = plan.DisplayName,
                    Status = groupStatus,
                    FileResults = operation.Record.FileResults.ToArray(),
                    Detail = operation.Record.SanitizedErrorMessage
                });
                UpdateTotals(result, operation.Record);
                result.DriveChanged |= operation.ShouldInvalidateScan;
                await SaveProgressAsync(result, batchHistory, historyService);

                if (operation.Record.Status != CleanupOperationStatus.Completed)
                {
                    MarkUnprocessed(result, preview.ApplicableGroups.Skip(groupIndex + 1));
                    CleanupBatchStatus stopStatus = operation.Record.Status == CleanupOperationStatus.Cancelled
                        ? (result.DriveChanged ? CleanupBatchStatus.CancelledAfterChanges : CleanupBatchStatus.CancelledBeforeChanges)
                        : (result.DriveChanged ? CleanupBatchStatus.FailedAfterChanges : CleanupBatchStatus.FailedBeforeChanges);
                    return await CompleteAsync(result, batchHistory, historyService, stopStatus,
                        new CleanupFailureInfo { Stage = operation.Record.FileResults.LastOrDefault(result => result.Status == CleanupFileStatus.Failed)?.FailureStage ?? "Files.Update", Message = operation.Record.SanitizedErrorMessage ?? "El lote se detuvo ante el primer error.", StableGroupId = plan.StableGroupId });
                }
            }
            catch (Exception)
            {
                MarkUnprocessed(result, preview.ApplicableGroups.Skip(groupIndex + 1));
                result.DriveChanged = true; // Tras una excepción alrededor de una escritura se considera el estado ambiguo.
                return await CompleteAsync(result, batchHistory, historyService, CleanupBatchStatus.FailedAfterChanges,
                    new CleanupFailureInfo { Stage = "HistoryWrite", Message = "El lote se detuvo tras un error. Revisa el historial y ejecuta un nuevo análisis.", StableGroupId = plan.StableGroupId });
            }
        }

        CleanupBatchStatus status = preview.ExcludedGroupCount > 0 ? CleanupBatchStatus.CompletedWithExclusions : CleanupBatchStatus.Completed;
        return await CompleteAsync(result, batchHistory, historyService, status, failure: null);
    }

    private async Task RevalidateAsync(
        CleanupBatchPreview preview,
        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId,
        DriveService cleanupDriveService,
        bool resultsAreObsolete,
        CancellationToken cancellationToken)
    {
        if (resultsAreObsolete)
        {
            throw new InvalidOperationException("Los resultados están obsoletos.");
        }

        foreach (CleanupBatchGroupPlan plan in preview.ApplicableGroups)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!reviewsByStableId.TryGetValue(plan.StableGroupId, out DuplicateGroupReview? review) ||
                !MatchesLocalSnapshot(review, plan.Snapshot))
            {
                throw new InvalidOperationException("El estado local cambió.");
            }

            CleanupPreflightResult preflight = await _trashService.PreflightAsync(
                cleanupDriveService, review, resultsAreObsolete: false, progress: null, cancellationToken);
            if (!preflight.IsSuccessful || preflight.KeepFile is null ||
                !SameFile(plan.KeepFile, preflight.KeepFile) ||
                !SameFiles(plan.Candidates, preflight.CandidateFiles))
            {
                throw new InvalidOperationException("Los metadatos de Google Drive cambiaron.");
            }
        }
    }

    private bool TryValidateLocal(DuplicateGroupReview review, out CleanupBatchExclusionReason reason, out string detail)
    {
        if (review.ReviewedWithoutCleanup)
        {
            reason = CleanupBatchExclusionReason.ReviewedWithoutCleanup;
            detail = "El grupo está marcado como revisado sin limpieza.";
            return false;
        }

        CleanupPlanValidationResult validation = _trashService.ValidateLocalPlan(review, resultsAreObsolete: false);
        if (validation.IsAllowed)
        {
            reason = default;
            detail = string.Empty;
            return true;
        }

        int keeps = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Keep);
        int candidates = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash);
        reason = review.Status != DuplicateGroupReviewStatus.ReadyForCleanup ? CleanupBatchExclusionReason.NotReadyForCleanup
            : keeps == 0 ? CleanupBatchExclusionReason.NoKeepFile
            : keeps > 1 ? CleanupBatchExclusionReason.MultipleKeepFiles
            : candidates == 0 ? CleanupBatchExclusionReason.NoCandidates
            : CleanupBatchExclusionReason.InvalidGroupState;
        detail = string.Join(Environment.NewLine, validation.Reasons);
        return false;
    }

    private static CleanupPreflightResult ToPreflight(DuplicateGroupReview review, CleanupBatchGroupPlan plan)
    {
        var preflight = new CleanupPreflightResult { Review = review, IsSuccessful = true, KeepFile = Clone(plan.KeepFile) };
        preflight.CandidateFiles.AddRange(plan.Candidates.Select(Clone));
        preflight.ValidationMessages.Add("Preflight global y revalidación del snapshot completados antes de escribir.");
        return preflight;
    }

    private static bool MatchesLocalSnapshot(DuplicateGroupReview review, CleanupGroupSnapshot snapshot) =>
        review.Status == DuplicateGroupReviewStatus.ReadyForCleanup &&
        !review.ReviewedWithoutCleanup &&
        review.LastModifiedUtc == snapshot.LastModifiedUtc &&
        review.DecisionsByFileId.Count == snapshot.DecisionsByFileId.Count &&
        review.DecisionsByFileId.All(pair => snapshot.DecisionsByFileId.TryGetValue(pair.Key, out DuplicateFileDecision decision) && decision == pair.Value);

    private static bool SameFiles(IReadOnlyList<CleanupFileSnapshot> first, IReadOnlyList<CleanupFileSnapshot> second) =>
        first.Count == second.Count && first.Zip(second).All(pair => SameFile(pair.First, pair.Second));

    private static bool SameFile(CleanupFileSnapshot first, CleanupFileSnapshot second) =>
        first.Id == second.Id && first.Name == second.Name && first.MimeType == second.MimeType &&
        first.Size == second.Size && first.Md5Checksum == second.Md5Checksum &&
        first.ModifiedTime == second.ModifiedTime && first.Trashed == second.Trashed &&
        first.ExplicitlyTrashed == second.ExplicitlyTrashed && first.OwnedByMe == second.OwnedByMe &&
        first.IsShared == second.IsShared && first.IsStarred == second.IsStarred && first.DriveId == second.DriveId &&
        first.Version == second.Version && first.CanTrash == second.CanTrash && first.Path == second.Path &&
        first.ParentIds.Count == second.ParentIds.Count && new HashSet<string>(first.ParentIds, StringComparer.Ordinal).SetEquals(second.ParentIds);

    private static CleanupFileSnapshot Clone(CleanupFileSnapshot file) => new()
    {
        Id = file.Id, Name = file.Name, MimeType = file.MimeType, Size = file.Size, Md5Checksum = file.Md5Checksum,
        ModifiedTime = file.ModifiedTime, ParentIds = file.ParentIds.ToArray(), Trashed = file.Trashed,
        ExplicitlyTrashed = file.ExplicitlyTrashed, OwnedByMe = file.OwnedByMe, IsShared = file.IsShared,
        IsStarred = file.IsStarred, DriveId = file.DriveId, Version = file.Version, CanTrash = file.CanTrash, Path = file.Path
    };

    private static void UpdateTotals(CleanupBatchResult result, CleanupOperationRecord record)
    {
        result.CompletedGroupCount += record.Status == CleanupOperationStatus.Completed ? 1 : 0;
        result.PartiallyProcessedGroupCount += record.Status is CleanupOperationStatus.Partial or CleanupOperationStatus.Cancelled ? 1 : 0;
        result.TrashedFileCount += record.FileResults.Count(file => file.Status == CleanupFileStatus.Trashed);
        result.FailedFileCount += record.FileResults.Count(file => file.Status == CleanupFileStatus.Failed);
        result.TrashedBytes += record.BytesSentToTrash;
    }

    private static void MarkUnprocessed(CleanupBatchResult result, IEnumerable<CleanupBatchGroupPlan> plans)
    {
        foreach (CleanupBatchGroupPlan plan in plans)
        {
            if (result.Groups.All(group => group.StableGroupId != plan.StableGroupId))
            {
                result.Groups.Add(new CleanupBatchGroupResult { StableGroupId = plan.StableGroupId, DisplayName = plan.DisplayName, Status = CleanupBatchGroupStatus.NotProcessed, Detail = "No se procesó porque el lote se detuvo." });
            }
        }
        result.UnprocessedGroupCount = result.Groups.Count(group => group.Status == CleanupBatchGroupStatus.NotProcessed);
    }

    private static async Task SaveProgressAsync(CleanupBatchResult result, CleanupBatchHistoryHandle history, CleanupHistoryService historyService)
    {
        history.Record.Result = result;
        await historyService.SaveAsync(history, CancellationToken.None);
    }

    private static async Task<CleanupBatchResult> CompleteAsync(
        CleanupBatchResult result,
        CleanupBatchHistoryHandle history,
        CleanupHistoryService historyService,
        CleanupBatchStatus status,
        CleanupFailureInfo? failure)
    {
        result.Status = status;
        result.Failure = failure;
        result.FinishedAt = DateTimeOffset.UtcNow;
        result.UnprocessedGroupCount = result.Groups.Count(group => group.Status == CleanupBatchGroupStatus.NotProcessed);
        history.Record.Status = status;
        history.Record.FinishedAtUtc = result.FinishedAt;
        history.Record.Result = result;
        try
        {
            await historyService.SaveAsync(history, CancellationToken.None);
        }
        catch (Exception exception)
        {
            result.Status = result.DriveChanged ? CleanupBatchStatus.FailedAfterChanges : CleanupBatchStatus.FailedBeforeChanges;
            result.Failure = new CleanupFailureInfo
            {
                Stage = "HistoryWrite",
                Message = $"No se pudo guardar el historial obligatorio. El lote se detuvo. Tipo: {exception.GetType().Name}."
            };
            result.FinishedAt = DateTimeOffset.UtcNow;
        }
        return result;
    }

    private static CleanupBatchExcludedGroup Excluded(string stableId, string displayName, CleanupBatchExclusionReason reason, string detail) => new()
    {
        StableGroupId = stableId, DisplayName = displayName, Reason = reason, Detail = detail
    };

    private static string DisplayName(DuplicateGroupReview review) =>
        $"Grupo {review.Group.GroupNumber}: {review.Group.Files.FirstOrDefault()?.Name ?? "sin archivos"}";
}
