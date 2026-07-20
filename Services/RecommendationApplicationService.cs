using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

/// <summary>
/// Prepara y aplica exclusivamente decisiones locales para una recomendación ya calculada.
/// </summary>
public sealed class RecommendationApplicationService
{
    public ApplyRecommendationPreview CreatePreview(
        DuplicateGroupReview review,
        KeepRecommendation recommendation)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(recommendation);

        if (review.Group.Files.Count < 2)
        {
            throw new InvalidOperationException("El grupo debe tener al menos dos archivos para aplicar una recomendación.");
        }

        DriveFileInfo? keepFile = review.Group.Files.FirstOrDefault(file =>
            string.Equals(file.Id, recommendation.FileId, StringComparison.Ordinal));
        if (keepFile is null)
        {
            throw new InvalidOperationException("El archivo recomendado ya no pertenece al grupo seleccionado.");
        }

        var candidates = new List<DriveFileInfo>();
        var skipped = new List<RecommendationSkippedFile>();
        foreach (DriveFileInfo file in review.Group.Files)
        {
            if (string.Equals(file.Id, keepFile.Id, StringComparison.Ordinal))
            {
                continue;
            }

            IReadOnlyList<RecommendationSkipReason> reasons = GetCandidateSkipReasons(file, review.Group);
            if (reasons.Count == 0)
            {
                candidates.Add(file);
            }
            else
            {
                skipped.Add(new RecommendationSkippedFile { File = file, Reasons = reasons });
            }
        }

        bool replacesExistingDecisions = review.DecisionsByFileId.Values.Any(decision =>
            decision is DuplicateFileDecision.Keep or DuplicateFileDecision.CandidateForTrash);
        DuplicateGroupReviewStatus expectedStatus = candidates.Count > 0 && skipped.Count == 0
            ? DuplicateGroupReviewStatus.ReadyForCleanup
            : DuplicateGroupReviewStatus.PartiallyReviewed;

        var warnings = new List<string>();
        if (replacesExistingDecisions)
        {
            warnings.Add("Este grupo ya tiene decisiones manuales. Aplicar la recomendación reemplazará las decisiones actuales del grupo.");
        }

        if (skipped.Count > 0)
        {
            warnings.Add($"{skipped.Count} archivo(s) quedarán sin decidir por seguridad.");
        }

        if (candidates.Count == 0)
        {
            warnings.Add("No hay copias elegibles para marcar como candidatas a papelera.");
        }

        return new ApplyRecommendationPreview
        {
            GroupStableId = review.StableId,
            KeepFile = keepFile,
            Recommendation = recommendation,
            CandidateFiles = candidates,
            SkippedFiles = skipped,
            Warnings = warnings,
            CandidateBytes = candidates.Sum(file => file.Size),
            ReplacesExistingDecisions = replacesExistingDecisions,
            ExpectedStatus = expectedStatus
        };
    }

    public RecommendationApplicationResult ApplyConfirmedPreview(
        DuplicateGroupReview review,
        ApplyRecommendationPreview preview,
        DuplicateReviewService reviewService,
        bool isConfirmed)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(preview);
        ArgumentNullException.ThrowIfNull(reviewService);

        if (!isConfirmed)
        {
            throw new InvalidOperationException("La recomendación no se puede aplicar sin confirmación del usuario.");
        }

        ValidatePreviewMatchesReview(review, preview);
        RecommendationApplicationSnapshot snapshot = CaptureSnapshot(review);
        var decisions = review.Group.Files.ToDictionary(
            file => file.Id,
            _ => DuplicateFileDecision.Undecided,
            StringComparer.Ordinal);
        decisions[preview.KeepFile.Id] = DuplicateFileDecision.Keep;
        foreach (DriveFileInfo candidate in preview.CandidateFiles)
        {
            decisions[candidate.Id] = DuplicateFileDecision.CandidateForTrash;
        }

        reviewService.ReplaceDecisions(review, decisions, reviewedWithoutCleanup: false);
        return new RecommendationApplicationResult { PreviousState = snapshot, Preview = preview };
    }

    public RecommendationApplicationSnapshot CaptureSnapshot(DuplicateGroupReview review)
    {
        ArgumentNullException.ThrowIfNull(review);
        return new RecommendationApplicationSnapshot
        {
            GroupStableId = review.StableId,
            DecisionsByFileId = new Dictionary<string, DuplicateFileDecision>(review.DecisionsByFileId, StringComparer.Ordinal),
            Status = review.Status,
            ReviewedWithoutCleanup = review.ReviewedWithoutCleanup,
            Notes = review.Notes,
            LastModifiedUtc = review.LastModifiedUtc
        };
    }

    public void RestoreSnapshot(
        DuplicateGroupReview review,
        RecommendationApplicationSnapshot snapshot,
        DuplicateReviewService reviewService)
    {
        ArgumentNullException.ThrowIfNull(review);
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(reviewService);

        if (!string.Equals(review.StableId, snapshot.GroupStableId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("El estado anterior no pertenece al grupo seleccionado.");
        }

        reviewService.RestoreSnapshot(review, snapshot);
    }

    public static IReadOnlyList<RecommendationSkipReason> GetCandidateSkipReasons(
        DriveFileInfo file,
        DuplicateGroup group)
    {
        ArgumentNullException.ThrowIfNull(file);
        ArgumentNullException.ThrowIfNull(group);

        var reasons = new List<RecommendationSkipReason>();
        if (file.IsShared == true) reasons.Add(RecommendationSkipReason.Shared);
        if (file.IsStarred == true) reasons.Add(RecommendationSkipReason.Starred);
        if (file.OwnedByMe != true) reasons.Add(RecommendationSkipReason.NotOwnedByUser);
        if (file.CanTrash != true) reasons.Add(RecommendationSkipReason.CannotTrash);
        if (!string.IsNullOrWhiteSpace(file.DriveId) || !string.IsNullOrWhiteSpace(file.SharedDriveId)) reasons.Add(RecommendationSkipReason.SharedDrive);
        if (!file.Path.StartsWith("Mi unidad/", StringComparison.Ordinal)) reasons.Add(RecommendationSkipReason.IncompletePath);
        if (string.IsNullOrWhiteSpace(file.Id) || string.IsNullOrWhiteSpace(file.Name) || string.IsNullOrWhiteSpace(file.MimeType) ||
            !string.Equals(file.Md5Checksum, group.Md5Checksum, StringComparison.OrdinalIgnoreCase)) reasons.Add(RecommendationSkipReason.InsufficientMetadata);
        if (string.IsNullOrWhiteSpace(file.Md5Checksum)) reasons.Add(RecommendationSkipReason.MissingMd5);
        if (file.Size < 0 || file.Size != group.FileSize) reasons.Add(RecommendationSkipReason.InvalidSize);
        return reasons;
    }

    public static string GetSkipReasonText(RecommendationSkipReason reason) => reason switch
    {
        RecommendationSkipReason.Shared => "Archivo compartido.",
        RecommendationSkipReason.Starred => "Archivo destacado.",
        RecommendationSkipReason.NotOwnedByUser => "No consta como propiedad del usuario.",
        RecommendationSkipReason.CannotTrash => "Google Drive no permite enviarlo a la papelera.",
        RecommendationSkipReason.SharedDrive => "Pertenece a una unidad compartida.",
        RecommendationSkipReason.IncompletePath => "La ruta no está completamente resuelta.",
        RecommendationSkipReason.InsufficientMetadata => "Faltan metadatos necesarios.",
        RecommendationSkipReason.MissingMd5 => "No tiene MD5.",
        RecommendationSkipReason.InvalidSize => "No tiene tamaño válido.",
        _ => "Advertencia de seguridad no reconocida."
    };

    private static void ValidatePreviewMatchesReview(DuplicateGroupReview review, ApplyRecommendationPreview preview)
    {
        if (!string.Equals(review.StableId, preview.GroupStableId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("La vista previa no pertenece al grupo seleccionado.");
        }

        if (!review.Group.Files.Any(file => string.Equals(file.Id, preview.KeepFile.Id, StringComparison.Ordinal)))
        {
            throw new InvalidOperationException("El archivo recomendado ya no pertenece al grupo seleccionado.");
        }

        if (preview.CandidateFiles.Any(file => string.Equals(file.Id, preview.KeepFile.Id, StringComparison.Ordinal)) ||
            preview.CandidateFiles.Count >= review.Group.Files.Count)
        {
            throw new InvalidOperationException("La propuesta debe conservar al menos una copia.");
        }

        foreach (DriveFileInfo candidate in preview.CandidateFiles)
        {
            if (!review.Group.Files.Any(file => string.Equals(file.Id, candidate.Id, StringComparison.Ordinal)) ||
                GetCandidateSkipReasons(candidate, review.Group).Count != 0)
            {
                throw new InvalidOperationException("La propuesta contiene un candidato que ya no cumple las condiciones de seguridad.");
            }
        }
    }
}
