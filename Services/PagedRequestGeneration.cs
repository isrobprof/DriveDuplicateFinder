namespace DriveDuplicateFinder.Services;

public sealed class PagedRequestGeneration
{
    private readonly object _sync = new();
    private CancellationTokenSource? _activeRequest;
    private long _generation;

    public bool HasActiveRequest
    {
        get
        {
            lock (_sync) return _activeRequest is not null;
        }
    }

    public PagedRequestLease Begin()
    {
        CancellationTokenSource? previous;
        PagedRequestLease request;
        lock (_sync)
        {
            previous = _activeRequest;
            _activeRequest = new CancellationTokenSource();
            _generation++;
            request = new PagedRequestLease(_generation, _activeRequest, _activeRequest.Token);
        }

        previous?.Cancel();
        return request;
    }

    public bool IsCurrent(PagedRequestLease request)
    {
        lock (_sync)
        {
            return request.Generation == _generation &&
                ReferenceEquals(request.Source, _activeRequest) &&
                !request.Token.IsCancellationRequested;
        }
    }

    public void Complete(PagedRequestLease request)
    {
        lock (_sync)
        {
            if (ReferenceEquals(request.Source, _activeRequest))
                _activeRequest = null;
            request.Source.Dispose();
        }
    }

    public void CancelCurrent()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            _generation++;
            active = _activeRequest;
            _activeRequest = null;
        }

        active?.Cancel();
    }
}

public sealed record PagedRequestLease(long Generation, CancellationTokenSource Source, CancellationToken Token);

public static class PagedReadOnlyViewSafety
{
    public static bool LegacyActionsEnabled(bool pagedReadOnlyMode, bool demoMode = false) => !pagedReadOnlyMode && !demoMode;

    public static bool HasPreviousPage(int offset) => offset > 0;

    public static bool HasNextPage(int offset, int pageCount, long totalCount) =>
        pageCount > 0 && (long)offset + pageCount < totalCount;
}
