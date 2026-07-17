using DriveDuplicateFinder.Models;

namespace DriveDuplicateFinder.Services;

public sealed class DuplicateReviewService
{
    public IReadOnlyList<DuplicateGroupReview> CreateReviews(IEnumerable<DuplicateGroup> groups)
    {
        return groups.Select(group =>
        {
            var review = new DuplicateGroupReview
            {
                StableId = CreateStableGroupId(group.FileSize, group.Md5Checksum),
                Group = group
            };

            foreach (DriveFileInfo file in group.Files)
            {
                review.DecisionsByFileId[file.Id] = DuplicateFileDecision.Undecided;
            }

            UpdateStatus(review);
            return review;
        }).ToArray();
    }

    public static string CreateStableGroupId(long fileSize, string md5Checksum)
    {
        return $"{fileSize}:{md5Checksum.ToUpperInvariant()}";
    }

    public DecisionChangeResult SetDecision(
        DuplicateGroupReview review,
        string fileId,
        DuplicateFileDecision decision)
    {
        ArgumentNullException.ThrowIfNull(review);

        if (!review.DecisionsByFileId.ContainsKey(fileId))
        {
            return DecisionChangeResult.Rejected("El archivo ya no pertenece a este grupo.");
        }

        DuplicateFileDecision currentDecision = review.DecisionsByFileId[fileId];
        if (currentDecision == DuplicateFileDecision.Keep && decision == DuplicateFileDecision.CandidateForTrash)
        {
            return DecisionChangeResult.Rejected("El archivo marcado para conservar no puede ser candidato a papelera.");
        }

        int candidateCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash);
        if (currentDecision == DuplicateFileDecision.Keep && decision == DuplicateFileDecision.Undecided &&
            candidateCount == review.Group.Files.Count - 1)
        {
            return DecisionChangeResult.Rejected("El grupo debe conservar al menos un archivo.");
        }

        if (decision == DuplicateFileDecision.Keep)
        {
            foreach (string otherFileId in review.DecisionsByFileId.Keys.ToArray())
            {
                if (otherFileId != fileId && review.DecisionsByFileId[otherFileId] == DuplicateFileDecision.Keep)
                {
                    review.DecisionsByFileId[otherFileId] = DuplicateFileDecision.Undecided;
                }
            }
        }

        review.DecisionsByFileId[fileId] = decision;
        review.ReviewedWithoutCleanup = false;

        string? automaticallyKeptFileId = null;
        candidateCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash);
        bool hasKeep = review.DecisionsByFileId.Values.Any(value => value == DuplicateFileDecision.Keep);
        if (!hasKeep && candidateCount == review.Group.Files.Count - 1)
        {
            automaticallyKeptFileId = review.DecisionsByFileId
                .First(pair => pair.Value != DuplicateFileDecision.CandidateForTrash)
                .Key;
            review.DecisionsByFileId[automaticallyKeptFileId] = DuplicateFileDecision.Keep;
        }

        TouchAndUpdateStatus(review);
        return DecisionChangeResult.Success(automaticallyKeptFileId);
    }

    public void MarkReviewedWithoutCleanup(DuplicateGroupReview review)
    {
        foreach (string fileId in review.DecisionsByFileId.Keys.ToArray())
        {
            if (review.DecisionsByFileId[fileId] == DuplicateFileDecision.CandidateForTrash)
            {
                review.DecisionsByFileId[fileId] = DuplicateFileDecision.Undecided;
            }
        }

        review.ReviewedWithoutCleanup = true;
        TouchAndUpdateStatus(review);
    }

    public void UpdateNotes(DuplicateGroupReview review, string? notes)
    {
        review.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        TouchAndUpdateStatus(review);
    }

    public void ApplyStoredState(
        IEnumerable<DuplicateGroupReview> reviews,
        DuplicateReviewState storedState)
    {
        if (storedState.FormatVersion != DuplicateReviewState.CurrentFormatVersion)
        {
            return;
        }

        var storedByStableId = storedState.Groups.ToDictionary(group => group.StableId, StringComparer.Ordinal);
        foreach (DuplicateGroupReview review in reviews)
        {
            if (!storedByStableId.TryGetValue(review.StableId, out StoredDuplicateGroupReview? stored) ||
                stored.FileSize != review.Group.FileSize ||
                !string.Equals(stored.Md5Checksum, review.Group.Md5Checksum, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            ApplyCompatibleDecisions(review, stored);
        }
    }

    public DuplicateReviewSummary BuildSummary(IEnumerable<DuplicateGroupReview> reviews)
    {
        DuplicateGroupReview[] reviewList = reviews.ToArray();
        var summary = new DuplicateReviewSummary
        {
            TotalGroups = reviewList.Length,
            PendingGroups = reviewList.Count(review => review.Status == DuplicateGroupReviewStatus.Pending),
            PartiallyReviewedGroups = reviewList.Count(review => review.Status == DuplicateGroupReviewStatus.PartiallyReviewed),
            ReadyForCleanupGroups = reviewList.Count(review => review.Status == DuplicateGroupReviewStatus.ReadyForCleanup),
            ReviewedWithoutCleanupGroups = reviewList.Count(review => review.Status == DuplicateGroupReviewStatus.ReviewedWithoutCleanup),
            FilesMarkedKeep = reviewList.Sum(review => review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Keep)),
            CandidateFiles = reviewList.Sum(review => review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash)),
            CandidateBytes = reviewList.Sum(review => review.Group.Files
                .Where(file => review.DecisionsByFileId[file.Id] == DuplicateFileDecision.CandidateForTrash)
                .Sum(file => file.Size)),
            SharedCandidateFiles = reviewList.Sum(review => review.Group.Files.Count(file =>
                review.DecisionsByFileId[file.Id] == DuplicateFileDecision.CandidateForTrash && file.IsShared == true)),
            StarredCandidateFiles = reviewList.Sum(review => review.Group.Files.Count(file =>
                review.DecisionsByFileId[file.Id] == DuplicateFileDecision.CandidateForTrash && file.IsStarred == true)),
            UndecidedFilesInPartiallyReviewedGroups = reviewList
                .Where(review => review.Status == DuplicateGroupReviewStatus.PartiallyReviewed)
                .Sum(review => review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Undecided))
        };

        return summary;
    }

    public IReadOnlyList<CleanupPlanRow> BuildCleanupPlan(IEnumerable<DuplicateGroupReview> reviews)
    {
        return reviews
            .Where(review => review.Status == DuplicateGroupReviewStatus.ReadyForCleanup)
            .SelectMany(review =>
            {
                DriveFileInfo keepFile = review.Group.Files.First(file =>
                    review.DecisionsByFileId[file.Id] == DuplicateFileDecision.Keep);

                return review.Group.Files
                    .Where(file => review.DecisionsByFileId[file.Id] == DuplicateFileDecision.CandidateForTrash)
                    .Select(file => new CleanupPlanRow
                    {
                        GroupNumber = review.Group.GroupNumber,
                        KeepFileName = keepFile.Name,
                        CandidateFileName = file.Name,
                        CandidateFileId = file.Id,
                        Path = file.Path,
                        Size = file.Size,
                        Md5Checksum = file.Md5Checksum,
                        Warnings = GetWarnings(file, review.Group)
                    });
            })
            .ToArray();
    }

    public static string GetWarnings(DriveFileInfo file, DuplicateGroup? group = null)
    {
        var warnings = new List<string>();
        if (file.IsShared == true)
        {
            warnings.Add("Compartido");
        }

        if (file.IsStarred == true)
        {
            warnings.Add("Destacado");
        }

        if (!string.IsNullOrWhiteSpace(file.SharedDriveId))
        {
            warnings.Add("Unidad compartida");
        }

        if (file.Path.StartsWith("Ubicaci\u00F3n desconocida", StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add("Ruta incompleta");
        }

        if (group is not null && IsOnlyCopyInFolder(file, group))
        {
            warnings.Add("\u00DAnico en esta carpeta");
        }

        return string.Join(", ", warnings);
    }

    private static bool IsOnlyCopyInFolder(DriveFileInfo file, DuplicateGroup group)
    {
        string folderPath = GetFolderPath(file.Path);
        return group.Files.Count(other => string.Equals(
            GetFolderPath(other.Path),
            folderPath,
            StringComparison.CurrentCultureIgnoreCase)) == 1;
    }

    private static string GetFolderPath(string path)
    {
        int separatorIndex = path.LastIndexOf('/');
        return separatorIndex > 0 ? path[..separatorIndex] : string.Empty;
    }

    private static void ApplyCompatibleDecisions(
        DuplicateGroupReview review,
        StoredDuplicateGroupReview stored)
    {
        foreach (string fileId in review.DecisionsByFileId.Keys.ToArray())
        {
            review.DecisionsByFileId[fileId] = DuplicateFileDecision.Undecided;
        }

        string? keepFileId = stored.DecisionsByFileId
            .FirstOrDefault(pair => pair.Value == DuplicateFileDecision.Keep && review.DecisionsByFileId.ContainsKey(pair.Key))
            .Key;
        if (!string.IsNullOrWhiteSpace(keepFileId))
        {
            review.DecisionsByFileId[keepFileId] = DuplicateFileDecision.Keep;
        }

        foreach ((string fileId, DuplicateFileDecision decision) in stored.DecisionsByFileId)
        {
            if (decision != DuplicateFileDecision.CandidateForTrash ||
                !review.DecisionsByFileId.ContainsKey(fileId) ||
                fileId == keepFileId ||
                review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash) >= review.Group.Files.Count - 1)
            {
                continue;
            }

            review.DecisionsByFileId[fileId] = DuplicateFileDecision.CandidateForTrash;
        }

        review.ReviewedWithoutCleanup = stored.ReviewedWithoutCleanup;
        if (review.ReviewedWithoutCleanup)
        {
            foreach (string fileId in review.DecisionsByFileId.Keys.ToArray())
            {
                if (review.DecisionsByFileId[fileId] == DuplicateFileDecision.CandidateForTrash)
                {
                    review.DecisionsByFileId[fileId] = DuplicateFileDecision.Undecided;
                }
            }
        }

        review.Notes = stored.Notes;
        review.LastModifiedUtc = stored.LastModifiedUtc;
        UpdateStatus(review);
    }

    private static void TouchAndUpdateStatus(DuplicateGroupReview review)
    {
        review.LastModifiedUtc = DateTimeOffset.UtcNow;
        UpdateStatus(review);
    }

    private static void UpdateStatus(DuplicateGroupReview review)
    {
        if (review.ReviewedWithoutCleanup)
        {
            review.Status = DuplicateGroupReviewStatus.ReviewedWithoutCleanup;
            return;
        }

        int keepCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Keep);
        int candidateCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.CandidateForTrash);
        int undecidedCount = review.DecisionsByFileId.Values.Count(value => value == DuplicateFileDecision.Undecided);

        review.Status = keepCount == 0 && candidateCount == 0
            ? DuplicateGroupReviewStatus.Pending
            : keepCount == 1 && candidateCount > 0 && undecidedCount == 0
                ? DuplicateGroupReviewStatus.ReadyForCleanup
                : DuplicateGroupReviewStatus.PartiallyReviewed;
    }
}

public sealed record DecisionChangeResult(bool Accepted, string? Message, string? AutomaticallyKeptFileId)
{
    public static DecisionChangeResult Rejected(string message) => new(false, message, null);

    public static DecisionChangeResult Success(string? automaticallyKeptFileId) => new(true, null, automaticallyKeptFileId);
}
