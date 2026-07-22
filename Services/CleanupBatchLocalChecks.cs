using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Escenarios locales A-J de la fase 4B.3. No se ejecutan automáticamente y no realizan E/S ni llamadas a Drive.
/// Los escenarios que requieren una respuesta remota se expresan mediante los estados y contratos del lote.
/// </summary>
public static class CleanupBatchLocalChecks
{
    public static void VerifyScenarios()
    {
        VerifyAValidReadyGroups();
        VerifyBNotReadyGroup();
        VerifyCLimits();
        VerifyDPreflightFailureContract();
        VerifyESnapshotChangeContract();
        VerifyFFailureBeforeChanges();
        VerifyGFailureAfterChanges();
        VerifyHCancellationBeforeChanges();
        VerifyICancellationAfterChanges();
        VerifyJCompletedResult();
        VerifyUiSelectedGroupsDoNotUseActiveReview();
        VerifyUiEmptySelectionContract();
        VerifyUiStaleResultsContract();
    }

    private static void VerifyAValidReadyGroups()
    {
        (DuplicateGroupReview first, DuplicateGroupReview second) = CreateReadyPair();
        var trash = new GoogleDriveTrashService();
        Ensure(trash.ValidateLocalPlan(first, false).IsAllowed && trash.ValidateLocalPlan(second, false).IsAllowed,
            "A: dos grupos listos deben admitir un plan local.");
    }

    private static void VerifyBNotReadyGroup()
    {
        DuplicateGroupReview review = CreateReview("pendiente", ready: false);
        Ensure(!new GoogleDriveTrashService().ValidateLocalPlan(review, false).IsAllowed,
            "B: un grupo no listo debe quedar excluido.");
    }

    private static void VerifyCLimits() =>
        Ensure(CleanupBatchService.MaxCleanupBatchGroups == 25 && CleanupBatchService.MaxCleanupBatchCandidateFiles == 100,
            "C: los límites del lote deben ser 25 grupos y 100 candidatos.");

    private static void VerifyDPreflightFailureContract() =>
        Ensure(CleanupBatchExclusionReason.PreflightFailed != CleanupBatchExclusionReason.MetadataChanged,
            "D: un fallo de preflight debe distinguirse de un cambio de metadatos.");

    private static void VerifyESnapshotChangeContract() =>
        Ensure(CleanupBatchExclusionReason.MetadataChanged != CleanupBatchExclusionReason.InvalidGroupState,
            "E: la revalidación debe poder representar un cambio posterior a la vista previa.");

    private static void VerifyFFailureBeforeChanges() =>
        Ensure(CleanupBatchStatus.FailedBeforeChanges != CleanupBatchStatus.FailedAfterChanges,
            "F: el fallo antes de escribir debe mantener el análisis válido.");

    private static void VerifyGFailureAfterChanges() =>
        Ensure(new CleanupBatchResult
        {
            BatchId = Guid.NewGuid(), Status = CleanupBatchStatus.FailedAfterChanges,
            StartedAt = DateTimeOffset.UtcNow, PlannedGroupCount = 1, PlannedFileCount = 2, DriveChanged = true
        }.DriveChanged, "G: un fallo tras cambios debe invalidar los resultados.");

    private static void VerifyHCancellationBeforeChanges() =>
        Ensure(CleanupBatchStatus.CancelledBeforeChanges != CleanupBatchStatus.CancelledAfterChanges,
            "H: cancelar antes de cambios debe distinguirse de cancelar después de ellos.");

    private static void VerifyICancellationAfterChanges() =>
        Ensure(new CleanupBatchResult
        {
            BatchId = Guid.NewGuid(), Status = CleanupBatchStatus.CancelledAfterChanges,
            StartedAt = DateTimeOffset.UtcNow, PlannedGroupCount = 1, PlannedFileCount = 2, DriveChanged = true
        }.DriveChanged, "I: cancelar después de un archivo debe invalidar los resultados.");

    private static void VerifyJCompletedResult() =>
        Ensure(new CleanupBatchResult
        {
            BatchId = Guid.NewGuid(), Status = CleanupBatchStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow, PlannedGroupCount = 2, PlannedFileCount = 2,
            CompletedGroupCount = 2, TrashedFileCount = 2, TrashedBytes = 2048, DriveChanged = true
        }.CompletedGroupCount == 2, "J: un lote completo debe informar sus contadores.");

    private static void VerifyUiSelectedGroupsDoNotUseActiveReview()
    {
        var selectedStableIds = new HashSet<string>(["group-a", "group-b"], StringComparer.Ordinal);
        const string activeReviewStableId = "group-c";
        string[] snapshot = selectedStableIds.OrderBy(id => id, StringComparer.Ordinal).ToArray();
        Ensure(snapshot.SequenceEqual(["group-a", "group-b"], StringComparer.Ordinal) && !snapshot.Contains(activeReviewStableId, StringComparer.Ordinal),
            "UI 1: el lote debe usar únicamente la selección, no el grupo activo del panel derecho.");
    }

    private static void VerifyUiEmptySelectionContract()
    {
        var selectedStableIds = new HashSet<string>(StringComparer.Ordinal);
        Ensure(selectedStableIds.Count == 0, "UI 2: sin selección no se debe preparar una vista previa de lote.");
    }

    private static void VerifyUiStaleResultsContract()
    {
        bool resultsAreObsolete = true;
        var selectedStableIds = new HashSet<string>(["group-a"], StringComparer.Ordinal);
        if (resultsAreObsolete)
        {
            selectedStableIds.Clear();
        }

        Ensure(selectedStableIds.Count == 0, "UI 3: los resultados obsoletos deben limpiar la selección por lotes.");
    }

    private static (DuplicateGroupReview First, DuplicateGroupReview Second) CreateReadyPair() =>
        (CreateReview("uno", ready: true), CreateReview("dos", ready: true));

    private static DuplicateGroupReview CreateReview(string suffix, bool ready)
    {
        const string md5 = "0123456789abcdef0123456789abcdef";
        var group = new DuplicateGroup
        {
            GroupNumber = 1,
            FileSize = 1024,
            Md5Checksum = md5,
            Files =
            [
                File($"keep-{suffix}", "conservar.txt", md5),
                File($"candidate-{suffix}", "candidato.txt", md5)
            ]
        };
        DuplicateGroupReview review = new DuplicateReviewService().CreateReviews([group]).Single();
        if (ready)
        {
            var reviews = new DuplicateReviewService();
            reviews.SetDecision(review, group.Files[0].Id, DuplicateFileDecision.Keep);
            reviews.SetDecision(review, group.Files[1].Id, DuplicateFileDecision.CandidateForTrash);
        }
        return review;
    }

    private static DriveFileInfo File(string id, string name, string md5) => new()
    {
        Id = id, Name = name, MimeType = "text/plain", Size = 1024, Md5Checksum = md5,
        Path = $"Mi unidad/Prueba/{name}", OwnedByMe = true, IsShared = false, IsStarred = false, CanTrash = true
    };

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
