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
/// One atomic view of the transient copy and the independent privacy decision carried across it.
/// </summary>
internal sealed record RefreshPresentationState(
    RefreshPresentationStatus Status,
    bool AuthorizationInvalidatesDetails);

/// <summary>
/// Bridges refresh outcomes into card presentation without persisting operational state or widening
/// <see cref="DeliveryState"/> with provider-only concerns.
/// </summary>
internal sealed class RefreshPresentation
{
    private RefreshPresentationState _current = new(RefreshPresentationStatus.Idle, false);

    public RefreshPresentationState Current => Volatile.Read(ref _current);

    public RefreshPresentationState Begin()
    {
        while (true)
        {
            RefreshPresentationState current = Current;
            var loading = current with { Status = RefreshPresentationStatus.Loading };

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, loading, current), current))
            {
                return current;
            }
        }
    }

    public void ReportGraphStatus(GraphMailStatus status)
    {
        RefreshPresentationStatus presentation = status switch
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
        };

        bool invalidatesDetails = presentation is
            RefreshPresentationStatus.Unauthorized
            or RefreshPresentationStatus.Forbidden
            or RefreshPresentationStatus.MailboxNotSupported;

        // A Graph result is evidence about both facts. In particular, a retry may show Loading
        // without making cached details safe; only its eventual non-invalidating result clears the
        // sticky authorization decision.
        Set(new RefreshPresentationState(presentation, invalidatesDetails));
    }

    public void Complete(RefreshResult result, RefreshPresentationState previousState)
    {
        switch (result.Outcome)
        {
            case RefreshOutcome.Committed:
                Set(new RefreshPresentationState(RefreshPresentationStatus.Idle, false));
                break;
            case RefreshOutcome.Discarded:
            case RefreshOutcome.Cancelled:
                SetStatus(RefreshPresentationStatus.Idle);
                break;
            case RefreshOutcome.SkippedDebounce:
                // Begin temporarily replaces the prior presentation with Loading. A debounce is
                // not a successful refresh, so restore the preceding error if nothing else changed
                // presentation state while the skipped request was being classified.
                RestoreIfLoading(previousState);
                break;
            case RefreshOutcome.SkippedLeaseHeld:
                SetStatus(RefreshPresentationStatus.RefreshInProgress);
                break;
            case RefreshOutcome.SkippedContention:
            case RefreshOutcome.CommitFailed:
                SetStatus(RefreshPresentationStatus.StatusUnknown);
                break;
            case RefreshOutcome.DeadlineExceeded:
                SetStatus(RefreshPresentationStatus.TimedOut);
                break;
            case RefreshOutcome.FetchFailed:
                // A Graph category reported by the fetcher is more useful than the coordinator's
                // generic outcome. If the fetch ended before Graph, authentication copy already
                // explains it, so do not turn that into a fabricated network error.
                if (Current.Status == RefreshPresentationStatus.Loading)
                {
                    SetStatus(RefreshPresentationStatus.Idle);
                }

                break;
        }
    }

    public void Fail() => SetStatus(RefreshPresentationStatus.ServiceFailure);

    public bool MarkUnknownIfWaiting() =>
        ChangeWaitingTo(RefreshPresentationStatus.StatusUnknown, clearAuthorizationInvalidation: false);

    public bool MarkCompleteIfWaiting() =>
        ChangeWaitingTo(RefreshPresentationStatus.Idle, clearAuthorizationInvalidation: true);

    private void RestoreIfLoading(RefreshPresentationState previousState)
    {
        while (true)
        {
            RefreshPresentationState current = Current;

            if (current.Status != RefreshPresentationStatus.Loading
                || current.AuthorizationInvalidatesDetails != previousState.AuthorizationInvalidatesDetails)
            {
                return;
            }

            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref _current, previousState, current),
                    current))
            {
                return;
            }
        }
    }

    private bool ChangeWaitingTo(
        RefreshPresentationStatus status,
        bool clearAuthorizationInvalidation)
    {
        while (true)
        {
            RefreshPresentationState current = Current;

            if (current.Status != RefreshPresentationStatus.RefreshInProgress)
            {
                return false;
            }

            var changed = new RefreshPresentationState(
                status,
                clearAuthorizationInvalidation ? false : current.AuthorizationInvalidatesDetails);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, changed, current), current))
            {
                return true;
            }
        }
    }

    private void SetStatus(RefreshPresentationStatus status) =>
        Update(current => current with { Status = status });

    private void Update(Func<RefreshPresentationState, RefreshPresentationState> change)
    {
        while (true)
        {
            RefreshPresentationState current = Current;
            RefreshPresentationState changed = change(current);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, changed, current), current))
            {
                return;
            }
        }
    }

    private void Set(RefreshPresentationState state) => Volatile.Write(ref _current, state);
}
