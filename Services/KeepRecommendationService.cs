using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

public sealed class KeepRecommendationService
{
    private static readonly string[] TemporaryPathTerms =
    [
        "copia", "copy", "backup", "respaldo", "temp", "temporal",
        "nueva carpeta", "descargas", "downloads"
    ];

    public KeepRecommendation GetRecommendation(DuplicateGroupReview review)
    {
        DriveFileInfo recommended = review.Group.Files
            .OrderByDescending(file => file.IsStarred == true)
            .ThenByDescending(file => file.IsShared == true)
            .ThenByDescending(file => file.OwnerNames.Count)
            .ThenBy(file => LooksTemporary(file.Path))
            .ThenBy(file => file.CreatedTime ?? file.ModifiedTime ?? DateTimeOffset.MaxValue)
            .ThenBy(file => file.Name.Length)
            .ThenBy(file => file.Path, StringComparer.CurrentCultureIgnoreCase)
            .First();

        string reason = recommended.IsStarred == true
            ? "Recomendado conservar: archivo destacado."
            : recommended.IsShared == true
                ? "Recomendado conservar: archivo compartido."
                : recommended.OwnerNames.Count > 1
                    ? "Recomendado conservar: tiene m\u00E1s informaci\u00F3n de propietarios."
                    : !LooksTemporary(recommended.Path)
                        ? "Recomendado conservar: no parece estar en una carpeta temporal."
                        : recommended.CreatedTime.HasValue || recommended.ModifiedTime.HasValue
                            ? "Recomendado conservar: archivo m\u00E1s antiguo disponible."
                            : "Recomendado conservar: primera ruta disponible.";

        return new KeepRecommendation(recommended.Id, reason);
    }

    private static bool LooksTemporary(string path)
    {
        return TemporaryPathTerms.Any(term =>
            path.Contains(term, StringComparison.OrdinalIgnoreCase));
    }
}
