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
    bool AuthorizationInvalidatesDetails,
    long? PeerWaitId = null);

/// <summary>
/// Bridges refresh outcomes into card presentation without persisting operational state or widening
/// <see cref="DeliveryState"/> with provider-only concerns.
/// </summary>
internal sealed class RefreshPresentation
{
    private RefreshPresentationState _current = new(RefreshPresentationStatus.Idle, false);
    private long _nextPeerWaitId;

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

        // **Three outcomes, not two, and conflating the last two was a disclosure bug.**
        //
        // Invalidating: the mailbox refused this token. Sender and subject must be withheld.
        //
        // Affirmative recovery: the request reached the mailbox and was answered, which is positive
        // evidence that authorization works again. Throttling and a missing item both qualify — a
        // 429 or a 404 is a reply from a service that accepted the credential.
        //
        // Inconclusive: nothing was learned about authorization. A network failure, a timeout, a
        // cancellation, an unparseable body, or a 5xx says only that the attempt did not complete.
        //
        // Deriving the flag from "is the new status one of the invalidating three" silently treated
        // every inconclusive outcome as recovery: a 401 followed by a dropped connection cleared the
        // suppression and the next delivery serialized cached senders and subjects, on the strength
        // of a request that never reached Graph. Suppression is sticky precisely so that only
        // evidence lifts it, and the absence of evidence is not evidence.
        bool invalidatesDetails = presentation switch
        {
            RefreshPresentationStatus.Unauthorized => true,
            RefreshPresentationStatus.Forbidden => true,
            RefreshPresentationStatus.MailboxNotSupported => true,

            RefreshPresentationStatus.Idle => false,
            RefreshPresentationStatus.ItemNotFound => false,
            RefreshPresentationStatus.Throttled => false,

            // Everything else carries no authorization evidence, so the prior decision stands.
            _ => Current.AuthorizationInvalidatesDetails,
        };

        // A Graph result is evidence about both facts. In particular, a retry may show Loading
        // without making cached details safe; only its eventual non-invalidating result clears the
        // sticky authorization decision.
        // A local Graph result supersedes any peer wait carried through Begin(). The monitor owns
        // only the peer observation identified by that token and must not later overwrite this
        // newer local evidence.
        Set(new RefreshPresentationState(presentation, invalidatesDetails));
    }

    public long? Complete(RefreshResult result, RefreshPresentationState previousState)
    {
        switch (result.Outcome)
        {
            case RefreshOutcome.Committed:
                Set(new RefreshPresentationState(RefreshPresentationStatus.Idle, false));
                return null;
            case RefreshOutcome.Discarded:
            case RefreshOutcome.Cancelled:
                SetStatus(RefreshPresentationStatus.Idle);
                return null;
            case RefreshOutcome.SkippedDebounce:
                // Begin temporarily replaces the prior presentation with Loading. A debounce is
                // not a successful refresh, so restore the preceding error if nothing else changed
                // presentation state while the skipped request was being classified.
                RestoreIfLoading(previousState);
                return null;
            case RefreshOutcome.SkippedLeaseHeld:
                return BeginPeerWait();
            case RefreshOutcome.SkippedContention:
            case RefreshOutcome.CommitFailed:
                SetStatus(RefreshPresentationStatus.StatusUnknown);
                return null;
            case RefreshOutcome.DeadlineExceeded:
                SetStatus(RefreshPresentationStatus.TimedOut);
                return null;
            case RefreshOutcome.FetchFailed:
                // A Graph category reported by the fetcher is more useful than the coordinator's
                // generic outcome. If the fetch ended before Graph, authentication copy already
                // explains it, so do not turn that into a fabricated network error.
                if (Current.Status == RefreshPresentationStatus.Loading)
                {
                    SetStatus(RefreshPresentationStatus.Idle);
                }

                return null;
        }

        return null;
    }

    public void Fail() => SetStatus(RefreshPresentationStatus.ServiceFailure);

    /// <summary>
    /// Resolves one identified peer wait from lease/generation evidence, regardless of temporary
    /// display transitions. A stale monitor cannot change a replacement wait or newer local result.
    /// </summary>
    public bool ResolvePeerWait(long peerWaitId, bool peerCommitted)
    {
        while (true)
        {
            RefreshPresentationState current = Current;

            if (current.PeerWaitId != peerWaitId)
            {
                return false;
            }

            var resolved = new RefreshPresentationState(
                peerCommitted
                    ? RefreshPresentationStatus.Idle
                    : RefreshPresentationStatus.StatusUnknown,
                peerCommitted ? false : current.AuthorizationInvalidatesDetails);

            if (ReferenceEquals(Interlocked.CompareExchange(ref _current, resolved, current), current))
            {
                return true;
            }
        }
    }

    private long BeginPeerWait()
    {
        long peerWaitId = Interlocked.Increment(ref _nextPeerWaitId);
        Update(current => new RefreshPresentationState(
            RefreshPresentationStatus.RefreshInProgress,
            current.AuthorizationInvalidatesDetails,
            peerWaitId));
        return peerWaitId;
    }

    private void RestoreIfLoading(RefreshPresentationState previousState)
    {
        while (true)
        {
            RefreshPresentationState current = Current;

            if (current.Status != RefreshPresentationStatus.Loading
                || current.AuthorizationInvalidatesDetails != previousState.AuthorizationInvalidatesDetails
                || current.PeerWaitId != previousState.PeerWaitId)
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

    private void SetStatus(RefreshPresentationStatus status) =>
        Update(current => current with { Status = status, PeerWaitId = null });

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
