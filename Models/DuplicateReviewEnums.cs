namespace DriveDuplicateFinder.Models;

public enum DuplicateFileDecision
{
    Undecided,
    Keep,
    CandidateForTrash
}

public enum DuplicateGroupReviewStatus
{
    Pending,
    PartiallyReviewed,
    ReadyForCleanup,
    ReviewedWithoutCleanup
}
