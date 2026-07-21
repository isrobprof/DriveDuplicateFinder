using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Prepara y aplica lotes locales a partir de las mismas propuestas individuales validadas.
/// </summary>
public sealed class RecommendationBatchService
{
    public const int MaxRecommendationBatchGroups = 100;

    private readonly RecommendationApplicationService _applicationService;
    private readonly DuplicateReviewService _reviewService;

    public RecommendationBatchService(
        RecommendationApplicationService applicationService,
        DuplicateReviewService reviewService)
    {
        _applicationService = applicationService ?? throw new ArgumentNullException(nameof(applicationService));
        _reviewService = reviewService ?? throw new ArgumentNullException(nameof(reviewService));
    }

    public RecommendationBatchPreview CreatePreview(
        IEnumerable<string> selectedStableIds,
        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId,
        IReadOnlyDictionary<string, KeepRecommendation> recommendationsByStableId,
        bool resultsAreObsolete)
    {
        ArgumentNullException.ThrowIfNull(selectedStableIds);
        ArgumentNullException.ThrowIfNull(reviewsByStableId);
        ArgumentNullException.ThrowIfNull(recommendationsByStableId);

        string[] selectedIds = selectedStableIds.Distinct(StringComparer.Ordinal).ToArray();
        if (selectedIds.Length > MaxRecommendationBatchGroups)
        {
            throw new InvalidOperationException($"El máximo permitido por lote es {MaxRecommendationBatchGroups} grupos.");
        }
        var applicable = new List<RecommendationBatchGroupPreview>();
        var excluded = new List<RecommendationBatchExcludedGroup>();
        foreach (string stableId in selectedIds)
        {
            if (!reviewsByStableId.TryGetValue(stableId, out DuplicateGroupReview? review))
            {
                excluded.Add(Excluded(stableId, "Grupo no disponible", RecommendationBatchExclusionReason.GroupNotFound, "El grupo ya no existe en los resultados actuales."));
                continue;
            }

            string displayName = GetDisplayName(review);
            if (resultsAreObsolete)
            {
                excluded.Add(Excluded(stableId, displayName, RecommendationBatchExclusionReason.ResultsObsolete, "Los resultados están obsoletos. Debes repetir la búsqueda."));
                continue;
            }

            if (review.Group.Files.Count < 2)
            {
                excluded.Add(Excluded(stableId, displayName, RecommendationBatchExclusionReason.TooFewFiles, "El grupo debe contener al menos dos archivos."));
                continue;
            }

            if (review.ReviewedWithoutCleanup)
            {
                excluded.Add(Excluded(stableId, displayName, RecommendationBatchExclusionReason.ReviewedWithoutCleanup, "El grupo está marcado como revisado sin limpieza."));
                continue;
            }

            if (!recommendationsByStableId.TryGetValue(stableId, out KeepRecommendation? recommendation))
            {
                excluded.Add(Excluded(stableId, displayName, RecommendationBatchExclusionReason.MissingRecommendation, "No existe una recomendación válida para este grupo."));
                continue;
            }

            try
            {
                ApplyRecommendationPreview individual = _applicationService.CreatePreview(review, recommendation);
                applicable.Add(new RecommendationBatchGroupPreview
                {
                    GroupStableId = stableId,
                    DisplayName = displayName,
                    IndividualPreview = individual,
                    StateAtPreview = _applicationService.CaptureSnapshot(review),
                    ExpectedStatusText = individual.ExpectedStatus == DuplicateGroupReviewStatus.ReadyForCleanup
                        ? "Listo para limpieza"
                        : "Parcialmente revisado"
                });
            }
            catch (InvalidOperationException exception)
            {
                excluded.Add(Excluded(stableId, displayName, RecommendationBatchExclusionReason.InvalidRecommendation, exception.Message));
            }
        }

        return new RecommendationBatchPreview
        {
            ApplicableGroups = applicable,
            ExcludedGroups = excluded,
            SelectedGroupCount = selectedIds.Length
        };
    }

    public RecommendationBatchApplyResult ApplyConfirmedPreview(
        RecommendationBatchPreview preview,
        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId,
        IReadOnlyDictionary<string, KeepRecommendation> recommendationsByStableId,
        bool resultsAreObsolete,
        bool isConfirmed,
        Action<int, int>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (!isConfirmed)
        {
            throw new InvalidOperationException("El lote no se puede aplicar sin confirmación del usuario.");
        }

        Revalidate(preview, reviewsByStableId, recommendationsByStableId, resultsAreObsolete);
        var states = preview.ApplicableGroups.Select(group => group.StateAtPreview).ToArray();
        try
        {
            int processed = 0;
            progress?.Invoke(processed, preview.ApplicableGroupCount);
            foreach (RecommendationBatchGroupPreview group in preview.ApplicableGroups)
            {
                _applicationService.ApplyConfirmedPreview(
                    reviewsByStableId[group.GroupStableId],
                    group.IndividualPreview,
                    _reviewService,
                    isConfirmed: true);
                progress?.Invoke(++processed, preview.ApplicableGroupCount);
            }
        }
        catch
        {
            RestoreSnapshots(states, reviewsByStableId);
            throw;
        }

        return new RecommendationBatchApplyResult
        {
            Preview = preview,
            UndoSnapshot = new RecommendationBatchUndoSnapshot
            {
                BatchId = Guid.NewGuid(),
                AppliedAtUtc = DateTimeOffset.UtcNow,
                Groups = states
            }
        };
    }

    public void RestoreSnapshots(
        IEnumerable<RecommendationApplicationSnapshot> snapshots,
        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId)
    {
        foreach (RecommendationApplicationSnapshot snapshot in snapshots)
        {
            if (!reviewsByStableId.TryGetValue(snapshot.GroupStableId, out DuplicateGroupReview? review))
            {
                throw new InvalidOperationException("No se pudo restaurar un grupo del lote porque ya no está disponible.");
            }

            _applicationService.RestoreSnapshot(review, snapshot, _reviewService);
        }
    }

    private void Revalidate(
        RecommendationBatchPreview preview,
        IReadOnlyDictionary<string, DuplicateGroupReview> reviewsByStableId,
        IReadOnlyDictionary<string, KeepRecommendation> recommendationsByStableId,
        bool resultsAreObsolete)
    {
        if (resultsAreObsolete)
        {
            throw new InvalidOperationException("La información de uno o varios grupos cambió mientras revisabas la vista previa. Vuelve a generar el lote.");
        }

        foreach (RecommendationBatchGroupPreview group in preview.ApplicableGroups)
        {
            if (!reviewsByStableId.TryGetValue(group.GroupStableId, out DuplicateGroupReview? review) ||
                !recommendationsByStableId.TryGetValue(group.GroupStableId, out KeepRecommendation? recommendation) ||
                !SameSnapshot(review, group.StateAtPreview) ||
                !SameRecommendation(recommendation, group.IndividualPreview.Recommendation))
            {
                throw new InvalidOperationException("La información de uno o varios grupos cambió mientras revisabas la vista previa. Vuelve a generar el lote.");
            }

            ApplyRecommendationPreview refreshed = _applicationService.CreatePreview(review, recommendation);
            if (!SameProposal(refreshed, group.IndividualPreview))
            {
                throw new InvalidOperationException("La información de uno o varios grupos cambió mientras revisabas la vista previa. Vuelve a generar el lote.");
            }
        }
    }

    private static bool SameSnapshot(DuplicateGroupReview review, RecommendationApplicationSnapshot snapshot) =>
        review.ReviewedWithoutCleanup == snapshot.ReviewedWithoutCleanup &&
        review.Status == snapshot.Status &&
        review.Notes == snapshot.Notes &&
        review.LastModifiedUtc == snapshot.LastModifiedUtc &&
        review.DecisionsByFileId.Count == snapshot.DecisionsByFileId.Count &&
        review.DecisionsByFileId.All(pair => snapshot.DecisionsByFileId.TryGetValue(pair.Key, out DuplicateFileDecision decision) && decision == pair.Value);

    private static bool SameRecommendation(KeepRecommendation first, KeepRecommendation second) =>
        string.Equals(first.FileId, second.FileId, StringComparison.Ordinal) &&
        string.Equals(first.Reason, second.Reason, StringComparison.Ordinal);

    private static bool SameProposal(ApplyRecommendationPreview first, ApplyRecommendationPreview second) =>
        string.Equals(first.GroupStableId, second.GroupStableId, StringComparison.Ordinal) &&
        SameRecommendation(first.Recommendation, second.Recommendation) &&
        first.ReplacesExistingDecisions == second.ReplacesExistingDecisions &&
        first.ExpectedStatus == second.ExpectedStatus &&
        first.CandidateFiles.Select(file => file.Id).SequenceEqual(second.CandidateFiles.Select(file => file.Id)) &&
        first.SkippedFiles.Select(file => file.File.Id).SequenceEqual(second.SkippedFiles.Select(file => file.File.Id));

    private static RecommendationBatchExcludedGroup Excluded(string stableId, string displayName, RecommendationBatchExclusionReason reason, string detail) => new()
    {
        GroupStableId = stableId,
        DisplayName = displayName,
        Reason = reason,
        Detail = detail
    };

    private static string GetDisplayName(DuplicateGroupReview review) =>
        $"Grupo {review.Group.GroupNumber}: {review.Group.Files.FirstOrDefault()?.Name ?? "sin archivos"}";
}
