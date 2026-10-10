namespace DriveDuplicateFinder.Services;

public sealed record PagedReadOnlyViewCheckResult(
    bool ActiveRequestIsCurrent,
    bool SupersededRequestCancelled,
    bool SupersededResponseRejected,
    bool CurrentRequestCancellationObserved,
    bool LegacyActionsBlockedForPagedView);

public static class PagedReadOnlyViewLocalChecks
{
    public static PagedReadOnlyViewCheckResult Verify()
    {
        var generations = new PagedRequestGeneration();
        PagedRequestLease first = generations.Begin();
        bool activeRequestIsCurrent = generations.IsCurrent(first);
        PagedRequestLease second = generations.Begin();
        bool supersededRequestCancelled = first.Token.IsCancellationRequested;
        bool supersededResponseRejected = !generations.IsCurrent(first) && generations.IsCurrent(second);
        generations.CancelCurrent();
        bool currentRequestCancellationObserved = second.Token.IsCancellationRequested && !generations.IsCurrent(second);
        generations.Complete(first);
        generations.Complete(second);
        var result = new PagedReadOnlyViewCheckResult(
            activeRequestIsCurrent,
            supersededRequestCancelled,
            supersededResponseRejected,
            currentRequestCancellationObserved,
            !PagedReadOnlyViewSafety.LegacyActionsEnabled(pagedReadOnlyMode: true));
        if (!result.ActiveRequestIsCurrent || !result.SupersededRequestCancelled ||
            !result.SupersededResponseRejected || !result.CurrentRequestCancellationObserved ||
            !result.LegacyActionsBlockedForPagedView)
            throw new InvalidOperationException("Falló la comprobación local de la vista paginada de solo lectura.");
        return result;
    }
}
