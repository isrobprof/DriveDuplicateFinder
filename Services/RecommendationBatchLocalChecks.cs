using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Escenarios locales de lote. No se ejecutan automáticamente ni realizan E/S o llamadas a Drive.
/// </summary>
public static class RecommendationBatchLocalChecks
{
    public static void VerifyScenarios()
    {
        VerifySimpleBatch();
        VerifyPartiallyEligibleGroup();
        VerifyExcludedGroup();
        VerifyDecisionReplacements();
        VerifyLimit();
        VerifyPreviewRevalidation();
        VerifyUndo();
        VerifyUndoInvalidation();
        VerifyRollback();
    }

    private static void VerifySimpleBatch()
    {
        var data = CreateData(3);
        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        Ensure(preview.ApplicableGroupCount == 3 && preview.ExcludedGroupCount == 0 && preview.KeepFileCount == 3,
            "El escenario A debe preparar tres grupos seguros.");
    }

    private static void VerifyPartiallyEligibleGroup()
    {
        var data = CreateData(1, sharedLastFile: true);
        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        Ensure(preview.ApplicableGroupCount == 1 && preview.SkippedFileCount == 1 &&
               preview.ApplicableGroups[0].IndividualPreview.ExpectedStatus == DuplicateGroupReviewStatus.PartiallyReviewed,
            "El escenario B debe mantener sin decidir el archivo compartido.");
    }

    private static void VerifyExcludedGroup()
    {
        var data = CreateData(1);
        data.Recommendations.Clear();
        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        Ensure(preview.ExcludedGroupCount == 1 && preview.ApplicableGroupCount == 0 &&
               data.Reviews.Values.Single().DecisionsByFileId.Values.All(value => value == DuplicateFileDecision.Undecided),
            "El escenario C debe excluir el grupo sin modificar decisiones.");
    }

    private static void VerifyDecisionReplacements()
    {
        var data = CreateData(2);
        foreach (DuplicateGroupReview review in data.Reviews.Values)
        {
            review.DecisionsByFileId[review.Group.Files[0].Id] = DuplicateFileDecision.Keep;
        }

        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        Ensure(preview.GroupsReplacingExistingDecisions == 2, "El escenario D debe requerir confirmación reforzada para dos grupos.");
    }

    private static void VerifyLimit()
    {
        var data = CreateData(1);
        try
        {
            data.Service.CreatePreview(Enumerable.Range(0, 101).Select(index => $"missing-{index}"), data.Reviews, data.Recommendations, false);
            throw new InvalidOperationException("El escenario E no bloqueó un lote de 101 grupos.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("máximo", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static void VerifyPreviewRevalidation()
    {
        var data = CreateData(1);
        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        DuplicateGroupReview review = data.Reviews.Values.Single();
        data.ReviewService.SetDecision(review, review.Group.Files[0].Id, DuplicateFileDecision.Keep);
        try
        {
            data.Service.ApplyConfirmedPreview(preview, data.Reviews, data.Recommendations, false, true);
            throw new InvalidOperationException("El escenario F no detectó el cambio posterior a la vista previa.");
        }
        catch (InvalidOperationException exception) when (exception.Message.Contains("cambió", StringComparison.OrdinalIgnoreCase))
        {
        }
    }

    private static void VerifyUndo()
    {
        var data = CreateData(3);
        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        RecommendationBatchApplyResult result = data.Service.ApplyConfirmedPreview(preview, data.Reviews, data.Recommendations, false, true);
        data.Service.RestoreSnapshots(result.UndoSnapshot.Groups, data.Reviews);
        Ensure(data.Reviews.Values.All(review => review.DecisionsByFileId.Values.All(value => value == DuplicateFileDecision.Undecided)),
            "El escenario G debe restaurar exactamente los tres grupos.");
    }

    private static void VerifyUndoInvalidation()
    {
        var data = CreateData(1);
        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        RecommendationBatchApplyResult result = data.Service.ApplyConfirmedPreview(preview, data.Reviews, data.Recommendations, false, true);
        var session = new RecommendationBatchUndoSession();
        session.Store(result.UndoSnapshot);
        Ensure(session.InvalidateIfIncludes(data.Reviews.Keys.Single()) && !session.HasSnapshot,
            "El escenario H debe invalidar el deshacer del lote.");
    }

    private static void VerifyRollback()
    {
        var data = CreateData(2);
        RecommendationBatchPreview preview = data.Service.CreatePreview(data.Reviews.Keys, data.Reviews, data.Recommendations, false);
        RecommendationBatchApplyResult result = data.Service.ApplyConfirmedPreview(preview, data.Reviews, data.Recommendations, false, true);
        data.Service.RestoreSnapshots(result.UndoSnapshot.Groups, data.Reviews);
        Ensure(data.Reviews.Values.All(review => review.DecisionsByFileId.Values.All(value => value == DuplicateFileDecision.Undecided)),
            "El escenario I debe poder revertir completamente el estado en memoria.");
    }

    private static BatchTestData CreateData(int count, bool sharedLastFile = false)
    {
        var reviewService = new DuplicateReviewService();
        var applicationService = new RecommendationApplicationService();
        var reviews = new Dictionary<string, DuplicateGroupReview>(StringComparer.Ordinal);
        var recommendations = new Dictionary<string, KeepRecommendation>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            const string md5 = "0123456789abcdef0123456789abcdef";
            DriveFileInfo keep = File($"keep-{index}", "conservar.txt", md5, false);
            DriveFileInfo candidate = File($"candidate-{index}", "candidato.txt", md5, false);
            IReadOnlyList<DriveFileInfo> files = sharedLastFile && index == count - 1
                ? [keep, candidate, File($"shared-{index}", "compartido.txt", md5, true)]
                : [keep, candidate];
            var group = new DuplicateGroup { GroupNumber = index + 1, FileSize = 1024, Md5Checksum = md5, Files = files };
            var review = new DuplicateGroupReview { StableId = $"batch-{index}", Group = group };
            foreach (DriveFileInfo file in files) review.DecisionsByFileId[file.Id] = DuplicateFileDecision.Undecided;
            reviews.Add(review.StableId, review);
            recommendations.Add(review.StableId, new KeepRecommendation(keep.Id, "Prueba local."));
        }

        return new BatchTestData(new RecommendationBatchService(applicationService, reviewService), reviewService, reviews, recommendations);
    }

    private static DriveFileInfo File(string id, string name, string md5, bool shared) => new()
    {
        Id = id, Name = name, MimeType = "text/plain", Size = 1024, Md5Checksum = md5,
        Path = $"Mi unidad/Prueba/{name}", IsShared = shared, IsStarred = false, OwnedByMe = true, CanTrash = true
    };

    private static void Ensure(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record BatchTestData(
        RecommendationBatchService Service,
        DuplicateReviewService ReviewService,
        Dictionary<string, DuplicateGroupReview> Reviews,
        Dictionary<string, KeepRecommendation> Recommendations);
}
