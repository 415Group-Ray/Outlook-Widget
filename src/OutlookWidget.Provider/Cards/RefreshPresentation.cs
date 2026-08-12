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
    private readonly AuthorizationSuppressionStore? _suppression;
    private readonly Action? _escalateUnrecordedSuppression;
    private RefreshPresentationState _current;
    private long _nextPeerWaitId;

    /// <param name="suppression">
    /// The durable authorization decision. Optional so tests can exercise the transitions without a
    /// filesystem; production always supplies it, and without it a restart discloses what a refusal
    /// withheld.
    /// </param>
    /// <param name="escalateUnrecordedSuppression">
    /// Invoked when a refusal could not be recorded durably, so the decision can be expressed
    /// through a mechanism that does not depend on that file. See <see cref="WriteSuppression"/>.
    /// </param>
    public RefreshPresentation(
        AuthorizationSuppressionStore? suppression = null,
        Action? escalateUnrecordedSuppression = null)
    {
        _suppression = suppression;
        _escalateUnrecordedSuppression = escalateUnrecordedSuppression;

        // **Restored, not assumed.** A restart is the moment the provider knows least: the
        // recovered-instance delivery runs before any new Graph result, so starting from "not
        // suppressed" rendered exactly the senders and subjects a 401 or 403 had withheld. An
        // unreadable record answers withheld, so damage cannot become disclosure either.
        bool withheld = suppression?.Read().DetailsWithheld ?? false;

        _current = new RefreshPresentationState(RefreshPresentationStatus.Idle, withheld);
    }

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
        // Affirmative recovery: a successful authorized read, and nothing else. That is the only
        // result which establishes that this token may read this mailbox.
        //
        // Inconclusive: everything else. Nothing was learned about authorization, so the prior
        // decision stands.
        //
        // Deriving the flag from "is the new status one of the invalidating three" silently treated
        // every inconclusive outcome as recovery: a 401 followed by a dropped connection cleared the
        // suppression and the next delivery serialized cached senders and subjects, on the strength
        // of a request that never reached Graph. Suppression is sticky precisely so that only
        // evidence lifts it, and the absence of evidence is not evidence.
        //
        // Throttling and a missing item were briefly counted as recovery, on the reasoning that a
        // 429 or a 404 is a reply from a service that accepted the credential. That is not sound: a
        // throttle can be applied at the gateway before authorization is evaluated, and this client
        // already ranks Throttled *below* Unauthorized precisely because it is the less conclusive
        // answer. Neither is worth the risk when the cost of being conservative is a counts-only
        // card until the next successful read.
        bool invalidatesDetails = presentation switch
        {
            RefreshPresentationStatus.Unauthorized => true,
            RefreshPresentationStatus.Forbidden => true,
            RefreshPresentationStatus.MailboxNotSupported => true,

            RefreshPresentationStatus.Idle => false,

            // Everything else carries no authorization evidence, so the prior decision stands.
            _ => Current.AuthorizationInvalidatesDetails,
        };

        // A Graph result is evidence about both facts. In particular, a retry may show Loading
        // without making cached details safe; only its eventual non-invalidating result clears the
        // sticky authorization decision.
        // A local Graph result supersedes any peer wait carried through Begin(). The monitor owns
        // only the peer observation identified by that token and must not later overwrite this
        // newer local evidence.
        PersistSuppression(invalidatesDetails);
        Set(new RefreshPresentationState(presentation, invalidatesDetails));
    }

    /// <summary>
    /// Records that the refresh deadline expired while acquiring a token, before Graph was reached.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This path cannot be covered by the Graph status mapping, because it ends before Graph.</b>
    /// When the twenty-second linked deadline expires during silent acquisition, the probe catches
    /// the cancellation and returns <c>Cancelled</c>, the fetcher returns null, and the coordinator
    /// reports <c>FetchFailed</c> — whose handler resets a still-Loading card to Idle. The user
    /// waited out a deadline and the card said everything was fine.
    /// </para>
    /// <para>
    /// Classified as a timeout rather than a cancellation for the same reason the Graph mapping
    /// does: a shutdown also cancels, but a shutting-down provider requests no delivery, so nothing
    /// renders. What survives to be seen is the deadline the user actually waited out.
    /// </para>
    /// <para>
    /// It deliberately says nothing about authorization. A deadline is not evidence either way, so
    /// the sticky decision is carried through untouched.
    /// </para>
    /// </remarks>
    public void ReportAuthenticationTimedOut() => SetStatus(RefreshPresentationStatus.TimedOut);

    public long? Complete(RefreshResult result, RefreshPresentationState previousState)
    {
        switch (result.Outcome)
        {
            case RefreshOutcome.Committed:
                // A commit is a successful authorized read by definition — the snapshot it wrote
                // came from one — so this is affirmative evidence and may clear the record.
                PersistSuppression(false);
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
    /// <param name="stateAdvanced">
    /// Whether the committed generation moved while the peer's lease was live.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>An advanced generation is not proof that the peer wrote it, and this used to assume it
    /// was.</b> The lease deliberately holds no mutex, so any other commit — most obviously a
    /// different-account sign-in clearing the prior snapshot — advances the counter while a peer
    /// refresh is live and possibly failing. Treating that as a successful peer read cleared durable
    /// authorization suppression and discharged a pending sign-in, on the strength of a write nobody
    /// had attributed to anyone.
    /// </para>
    /// <para>
    /// So the advance now decides only what it can support: whether the card returns to Idle or says
    /// the outcome is unknown. <b>It no longer clears the authorization decision</b>, which stays
    /// until this process gets a successful read of its own. The cost is one extra refresh after a
    /// peer genuinely recovers; the alternative was rendering withheld mail because some unrelated
    /// commit moved a number.
    /// </para>
    /// </remarks>
    public bool ResolvePeerWait(long peerWaitId, bool stateAdvanced)
    {
        while (true)
        {
            RefreshPresentationState current = Current;

            if (current.PeerWaitId != peerWaitId)
            {
                return false;
            }

            var resolved = new RefreshPresentationState(
                stateAdvanced
                    ? RefreshPresentationStatus.Idle
                    : RefreshPresentationStatus.StatusUnknown,

                // Carried through untouched. See the remarks: nothing observed here identifies who
                // advanced the generation, so nothing observed here is authorization evidence.
                current.AuthorizationInvalidatesDetails);

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

    /// <summary>
    /// Records the authorization decision durably, so a restart inherits it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written before the in-memory state changes when suppression is being <em>set</em>, so a crash
    /// between the two leaves the more conservative of the two states on disk. Clearing is written
    /// in the same place for symmetry; a crash there leaves the record saying withheld, which costs
    /// a counts-only card until the next successful read and discloses nothing.
    /// </para>
    /// <para>
    /// Skipped when nothing changed, so an unchanging stream of results does not rewrite the file on
    /// every refresh.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// Callers that have already changed <see cref="Current"/> must use
    /// <see cref="WriteSuppression"/> instead: this compares against it, so a caller writing after
    /// its own update sees no change and silently skips the write.
    /// </remarks>
    private void PersistSuppression(bool detailsWithheld)
    {
        if (Current.AuthorizationInvalidatesDetails == detailsWithheld)
        {
            return;
        }

        WriteSuppression(detailsWithheld);
    }

    /// <summary>
    /// Records the decision without consulting the in-memory state, escalating if it cannot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failed write in the withholding direction is a disclosure risk, not a logging matter.</b>
    /// This process still withholds, because the in-memory flag is set either way — but the record is
    /// what the *next* process reads, and an absent or stale one tells it nothing was ever refused.
    /// A recycle would then render the senders and subjects the refusal withheld, which is precisely
    /// the defect the durable record was introduced to fix. Discarding the store's return value left
    /// that hole open while the store was reporting it.
    /// </para>
    /// <para>
    /// The escalation is the disclosure tombstone, which exists for exactly this shape of problem:
    /// section 4 introduces it because the ordinary commit path cannot be the only route to a
    /// fail-closed state. It needs no mutex, it is read before every host call, and an orphan is a
    /// supported outcome that the companion's explicit recovery action removes. So a widget whose
    /// suppression could not be recorded shows counts only until someone clears it deliberately,
    /// rather than showing mail after the next restart.
    /// </para>
    /// <para>
    /// Clearing is not escalated. A failure there leaves the record saying withheld, which is the
    /// safe direction already.
    /// </para>
    /// </remarks>
    private void WriteSuppression(bool detailsWithheld)
    {
        if (_suppression is null)
        {
            return;
        }

        if (_suppression.Write(detailsWithheld) || !detailsWithheld)
        {
            return;
        }

        _escalateUnrecordedSuppression?.Invoke();
    }

    private void Set(RefreshPresentationState state) => Volatile.Write(ref _current, state);
}
