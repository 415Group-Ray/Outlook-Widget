using OutlookWidget.Core.Graph;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Provider.Cards;

/// <summary>The transient, metadata-free refresh state rendered over the committed cache.</summary>
internal enum RefreshPresentationStatus
{
    Idle,
    Loading,
    RefreshInProgress,
    StatusUnknown,
    Unauthorized,
    Forbidden,
    MailboxNotSupported,
    ItemNotFound,
    Throttled,
    TimedOut,
    Offline,
    InvalidResponse,
    ServiceFailure,
}

/// <summary>
/// Bridges refresh outcomes into card presentation without persisting operational state or widening
/// <see cref="DeliveryState"/> with provider-only concerns.
/// </summary>
internal sealed class RefreshPresentation
{
    private int _status;

    public RefreshPresentationStatus Current =>
        (RefreshPresentationStatus)Volatile.Read(ref _status);

    public RefreshPresentationStatus Begin() =>
        (RefreshPresentationStatus)Interlocked.Exchange(
            ref _status,
            (int)RefreshPresentationStatus.Loading);

    public void ReportGraphStatus(GraphMailStatus status) => Set(status switch
    {
        GraphMailStatus.Success => RefreshPresentationStatus.Idle,
        GraphMailStatus.Unauthorized => RefreshPresentationStatus.Unauthorized,
        GraphMailStatus.Forbidden => RefreshPresentationStatus.Forbidden,
        GraphMailStatus.MailboxNotSupported => RefreshPresentationStatus.MailboxNotSupported,
        GraphMailStatus.ItemNotFound => RefreshPresentationStatus.ItemNotFound,
        GraphMailStatus.Throttled => RefreshPresentationStatus.Throttled,
        GraphMailStatus.TimedOut => RefreshPresentationStatus.TimedOut,
        // Graph returns Cancelled both for provider shutdown and when the outer refresh deadline
        // cancels its in-flight request before the nested Graph timeout. Shutdown requests no
        // delivery, so classifying this as TimedOut preserves the user-visible deadline failure
        // without rendering a spurious card while the provider exits.
        GraphMailStatus.Cancelled => RefreshPresentationStatus.TimedOut,
        GraphMailStatus.NetworkFailure => RefreshPresentationStatus.Offline,
        GraphMailStatus.InvalidResponse => RefreshPresentationStatus.InvalidResponse,
        GraphMailStatus.ServiceFailure => RefreshPresentationStatus.ServiceFailure,
        _ => RefreshPresentationStatus.ServiceFailure,
    });

    public void Complete(RefreshResult result, RefreshPresentationStatus previousStatus)
    {
        switch (result.Outcome)
        {
            case RefreshOutcome.Committed:
            case RefreshOutcome.Discarded:
            case RefreshOutcome.Cancelled:
                Set(RefreshPresentationStatus.Idle);
                break;
            case RefreshOutcome.SkippedDebounce:
                // Begin temporarily replaces the prior presentation with Loading. A debounce is
                // not a successful refresh, so restore the preceding error if nothing else changed
                // presentation state while the skipped request was being classified.
                Interlocked.CompareExchange(
                    ref _status,
                    (int)previousStatus,
                    (int)RefreshPresentationStatus.Loading);
                break;
            case RefreshOutcome.SkippedLeaseHeld:
                Set(RefreshPresentationStatus.RefreshInProgress);
                break;
            case RefreshOutcome.SkippedContention:
            case RefreshOutcome.CommitFailed:
                Set(RefreshPresentationStatus.StatusUnknown);
                break;
            case RefreshOutcome.DeadlineExceeded:
                Set(RefreshPresentationStatus.TimedOut);
                break;
            case RefreshOutcome.FetchFailed:
                // A Graph category reported by the fetcher is more useful than the coordinator's
                // generic outcome. If the fetch ended before Graph, authentication copy already
                // explains it, so do not turn that into a fabricated network error.
                if (Current == RefreshPresentationStatus.Loading)
                {
                    Set(RefreshPresentationStatus.Idle);
                }

                break;
        }
    }

    public void Fail() => Set(RefreshPresentationStatus.ServiceFailure);

    public bool MarkUnknownIfWaiting() =>
        Interlocked.CompareExchange(
            ref _status,
            (int)RefreshPresentationStatus.StatusUnknown,
            (int)RefreshPresentationStatus.RefreshInProgress)
        == (int)RefreshPresentationStatus.RefreshInProgress;

    public bool MarkCompleteIfWaiting() =>
        Interlocked.CompareExchange(
            ref _status,
            (int)RefreshPresentationStatus.Idle,
            (int)RefreshPresentationStatus.RefreshInProgress)
        == (int)RefreshPresentationStatus.RefreshInProgress;

    public void Clear() => Set(RefreshPresentationStatus.Idle);

    private void Set(RefreshPresentationStatus status) =>
        Volatile.Write(ref _status, (int)status);
}
