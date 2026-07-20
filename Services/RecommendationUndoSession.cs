using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Mantiene los snapshots de deshacer únicamente mientras la aplicación permanece abierta.
/// </summary>
public sealed class RecommendationUndoSession
{
    private readonly Dictionary<string, RecommendationApplicationSnapshot> _snapshots = new(StringComparer.Ordinal);

    public void Store(RecommendationApplicationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshots[snapshot.GroupStableId] = snapshot;
    }

    public bool HasSnapshot(string stableGroupId) => _snapshots.ContainsKey(stableGroupId);

    public bool TryGet(string stableGroupId, out RecommendationApplicationSnapshot? snapshot) =>
        _snapshots.TryGetValue(stableGroupId, out snapshot);

    public void Invalidate(string stableGroupId) => _snapshots.Remove(stableGroupId);

    public void Clear() => _snapshots.Clear();
}

/// <summary>
/// Escenarios locales reutilizables de la fase 4B.1. No realizan E/S ni llamadas a Google Drive.
/// </summary>
public static class RecommendationApplicationLocalChecks
{
    public static void VerifyScenarios()
    {
        VerifyScenarioA();
        VerifyScenarioB();
        VerifyScenarioC();
        VerifyScenarioD();
        VerifyScenarioE();
    }

    private static void VerifyScenarioA()
    {
        (DuplicateGroupReview review, KeepRecommendation recommendation) = CreateReview("a", sharedThirdFile: false);
        var service = new RecommendationApplicationService();
        ApplyRecommendationPreview preview = service.CreatePreview(review, recommendation);
        service.ApplyConfirmedPreview(review, preview, new DuplicateReviewService(), isConfirmed: true);
        Ensure(review.Status == DuplicateGroupReviewStatus.ReadyForCleanup &&
               review.DecisionsByFileId[recommendation.FileId] == DuplicateFileDecision.Keep &&
               review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash) == 1,
            "El escenario A debe dejar un archivo para conservar y otro candidato.");
    }

    private static void VerifyScenarioB()
    {
        (DuplicateGroupReview review, KeepRecommendation recommendation) = CreateReview("b", sharedThirdFile: true);
        var service = new RecommendationApplicationService();
        ApplyRecommendationPreview preview = service.CreatePreview(review, recommendation);
        global::DriveDuplicateFinder.ApplyRecommendationPreviewForm.VerifyLocalPreviewConstruction(preview);
        service.ApplyConfirmedPreview(review, preview, new DuplicateReviewService(), isConfirmed: true);
        Ensure(review.Status == DuplicateGroupReviewStatus.PartiallyReviewed &&
               preview.SkippedFiles.Count == 1 &&
               review.DecisionsByFileId[preview.SkippedFiles[0].File.Id] == DuplicateFileDecision.Undecided,
            "El escenario B debe mantener sin decidir el archivo compartido.");
    }

    private static void VerifyScenarioC()
    {
        (DuplicateGroupReview review, KeepRecommendation recommendation) = CreateReview("c", sharedThirdFile: false);
        review.DecisionsByFileId[recommendation.FileId] = DuplicateFileDecision.Keep;
        var service = new RecommendationApplicationService();
        ApplyRecommendationPreview preview = service.CreatePreview(review, recommendation);
        Ensure(preview.ReplacesExistingDecisions, "El escenario C debe advertir sobre decisiones previas.");
        try
        {
            service.ApplyConfirmedPreview(review, preview, new DuplicateReviewService(), isConfirmed: false);
            throw new InvalidOperationException("El escenario C no bloqueó la aplicación sin confirmación.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("sin confirmación", StringComparison.Ordinal))
        {
        }
    }

    private static void VerifyScenarioD()
    {
        (DuplicateGroupReview review, KeepRecommendation recommendation) = CreateReview("d", sharedThirdFile: false);
        review.DecisionsByFileId[recommendation.FileId] = DuplicateFileDecision.Keep;
        review.Notes = "Nota local";
        review.LastModifiedUtc = DateTimeOffset.UnixEpoch;
        var reviewService = new DuplicateReviewService();
        var service = new RecommendationApplicationService();
        ApplyRecommendationPreview preview = service.CreatePreview(review, recommendation);
        RecommendationApplicationResult result = service.ApplyConfirmedPreview(review, preview, reviewService, isConfirmed: true);
        service.RestoreSnapshot(review, result.PreviousState, reviewService);
        Ensure(review.DecisionsByFileId[recommendation.FileId] == DuplicateFileDecision.Keep &&
               review.Notes == "Nota local" && review.LastModifiedUtc == DateTimeOffset.UnixEpoch,
            "El escenario D debe restaurar el estado anterior exactamente.");
    }

    private static void VerifyScenarioE()
    {
        (DuplicateGroupReview review, KeepRecommendation recommendation) = CreateReview("e", sharedThirdFile: false);
        var service = new RecommendationApplicationService();
        RecommendationApplicationResult result = service.ApplyConfirmedPreview(
            review,
            service.CreatePreview(review, recommendation),
            new DuplicateReviewService(),
            isConfirmed: true);
        var undoSession = new RecommendationUndoSession();
        undoSession.Store(result.PreviousState);
        undoSession.Invalidate(review.StableId);
        Ensure(!undoSession.HasSnapshot(review.StableId), "El escenario E debe invalidar el deshacer tras una decisión manual.");
    }

    private static (DuplicateGroupReview Review, KeepRecommendation Recommendation) CreateReview(string suffix, bool sharedThirdFile)
    {
        const string md5 = "0123456789abcdef0123456789abcdef";
        var keep = CreateFile($"keep-{suffix}", "conservar.txt", md5, isShared: false);
        var candidate = CreateFile($"candidate-{suffix}", "candidato.txt", md5, isShared: false);
        var files = sharedThirdFile
            ? new[] { keep, candidate, CreateFile($"shared-{suffix}", "compartido.txt", md5, isShared: true) }
            : new[] { keep, candidate };
        var group = new DuplicateGroup { GroupNumber = 1, FileSize = 1024, Md5Checksum = md5, Files = files };
        var review = new DuplicateGroupReview
        {
            StableId = DuplicateReviewService.CreateStableGroupId(group.FileSize, group.Md5Checksum),
            Group = group
        };
        foreach (DriveFileInfo file in files)
        {
            review.DecisionsByFileId[file.Id] = DuplicateFileDecision.Undecided;
        }

        return (review, new KeepRecommendation(keep.Id, "Prueba local."));
    }

    private static DriveFileInfo CreateFile(string id, string name, string md5, bool isShared) => new()
    {
        Id = id,
        Name = name,
        MimeType = "text/plain",
        Size = 1024,
        Md5Checksum = md5,
        Path = $"Mi unidad/Prueba/{name}",
        IsShared = isShared,
        IsStarred = false,
        OwnedByMe = true,
        CanTrash = true
    };

    private static void Ensure(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
