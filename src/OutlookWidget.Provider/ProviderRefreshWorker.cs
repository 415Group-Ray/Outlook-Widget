using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Provider;

/// <summary>Runs provider refreshes off the Widgets host callback threads.</summary>
internal sealed class ProviderRefreshWorker : IDisposable
{
    private static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(2);
    private readonly RefreshCoordinator _coordinator;
    private readonly IRefreshFetcher _fetcher;
    private readonly ProtectedCache _cache;
    private readonly SelectedAccountStore _selectedAccounts;
    private readonly IDeliveryRequester _delivery;
    private readonly IOperationalLogger _logger;
    private readonly RefreshPresentation _presentation;
    private readonly PeerRefreshMonitor _peerMonitor;
    private readonly Func<Guid?> _readSignInToken;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// The sign-in token this worker has already spent a recovery attempt on, or
    /// <see langword="null"/> before it has spent any.
    /// </summary>
    /// <remarks>
    /// Guarded by <see cref="_gate"/> rather than made atomic: it is only read and written from the
    /// staleness check, which runs on the single drain loop.
    /// </remarks>
    private Guid? _recoveredSignInToken;
    private readonly Lock _gate = new();
    private readonly Queue<RefreshWork> _pending = new();
    private Task? _drain;
    private bool _running;
    private bool _disposed;

    public ProviderRefreshWorker(
        RefreshCoordinator coordinator,
        IRefreshFetcher fetcher,
        ProtectedCache cache,
        SelectedAccountStore selectedAccounts,
        IDeliveryRequester delivery,
        RefreshPresentation presentation,
        IOperationalLogger? logger = null,
        Func<Guid?>? readSignInToken = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selectedAccounts);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(presentation);

        // Optional so existing construction sites keep compiling; a worker without it simply has no
        // durable fallback and relies on the sign-in event alone.
        _readSignInToken = readSignInToken ?? (static () => null);
        _coordinator = coordinator;
        _fetcher = fetcher;
        _cache = cache;
        _selectedAccounts = selectedAccounts;
        _delivery = delivery;
        _presentation = presentation;
        _logger = logger ?? NullOperationalLogger.Instance;
        _peerMonitor = new PeerRefreshMonitor(
            coordinator.IsRefreshInProgress,
            ReadKnownGeneration,
            delivery.RequestDelivery);
    }

    public void Request(RefreshTrigger trigger)
    {
        Enqueue(new RefreshWork(trigger, OnlyIfStale: false));
    }

    public void RequestIfStale(RefreshTrigger trigger) =>
        Enqueue(new RefreshWork(trigger, OnlyIfStale: true));

    private void Enqueue(RefreshWork work)
    {
        lock (_gate)
        {
            if (_disposed || _shutdown.IsCancellationRequested)
            {
                return;
            }

            // Duplicate triggers are equivalent because each pass re-reads current state. Retain
            // distinct triggers so a manual action still reaches the coordinator's debounce rule.
            if (!_pending.Contains(work))
            {
                _pending.Enqueue(work);
            }

            if (_running)
            {
                return;
            }

            _running = true;
            _drain = Task.Run(DrainAsync);
        }
    }

    private bool IsStale()
    {
        if (HasUnrecoveredSignIn())
        {
            return true;
        }

        CacheReadResult read = _cache.Read();
        MailboxSnapshot? snapshot = read.IsSuccess && read.Payload is { } payload
            ? MailboxSnapshot.TryDeserialize(payload)
            : null;

        DateTimeOffset now = SystemClock.Instance.UtcNow;

        if (snapshot is null
            || snapshot.RefreshedAtUtc > now
            || now - snapshot.RefreshedAtUtc > CoordinationBounds.ActivationStaleness)
        {
            return true;
        }

        SelectedAccountResult selected = _selectedAccounts.Read();

        // A newly selected account is authoritative even while the prior snapshot is young. An
        // unreadable selection also fails closed by forcing the refresh/auth path; a genuinely absent
        // legacy selection retains the timestamp rule because SilentAuthService may still use its
        // single-account fallback.
        if (selected.Status == SelectedAccountStatus.Unreadable
            || (selected.Status == SelectedAccountStatus.Recorded
                && !string.Equals(
                    selected.HomeAccountId,
                    snapshot.HomeAccountId,
                    StringComparison.Ordinal)))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Whether a completed sign-in has been recorded that this worker has not yet acted on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The durable half of sign-in recovery.</b> The sign-in event is the fast path and remains
    /// the normal one: it forces a refresh immediately through <c>Request</c>, bypassing staleness
    /// entirely. But a named event is an accelerant, and a raise that cannot open its handle loses
    /// the fact. Then the ordinary staleness rule looks at a seconds-old snapshot for an unchanged
    /// account, declines to refresh, and the card stays on "Sign in required" — with no Refresh
    /// action at the small size — until a provider recycle or a five-minute timer tick.
    /// </para>
    /// <para>
    /// The companion writes the token before it raises the event, so this read can only be behind,
    /// never ahead. Any later opportunity discovers what the raise failed to announce.
    /// </para>
    /// <para>
    /// <b>Deliberately not conditioned on authorization suppression.</b> That was an earlier fix,
    /// and it needed the worker to consult presentation state, which put a display concern into the
    /// staleness rule and forced every unrelated signal through it. A recorded sign-in is a fact
    /// about the account rather than about the card, and refreshing once after one is correct
    /// whatever the card happens to be showing.
    /// </para>
    /// <para>
    /// <b>One attempt per token, which is what bounds it.</b> The token changes only when the
    /// companion completes another sign-in, so a retry that fails again does not schedule anything
    /// further, and a burst of unrelated signals cannot become a burst of Graph requests. An absent
    /// or unreadable record answers "no evidence" and spends nothing.
    /// </para>
    /// </remarks>
    /// <remarks>
    /// <b>Reports; it does not retire.</b> Retiring here marked the token spent before anything was
    /// known about whether a fetch happened, which lost recovery whenever a peer held the lease: the
    /// pass returned <c>SkippedLeaseHeld</c>, and if that peer then expired without committing, every
    /// later fresh-snapshot check skipped the attempt that was still owed. The token is retired in
    /// <see cref="DrainAsync"/> instead, once an attempt has actually been made.
    /// </remarks>
    private bool HasUnrecoveredSignIn()
    {
        if (_readSignInToken() is not { } token)
        {
            return false;
        }

        lock (_gate)
        {
            return _recoveredSignInToken != token;
        }
    }

    /// <summary>
    /// Marks a recorded sign-in as acted on, once this worker has genuinely attempted a refresh.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called for every completed pass, not only for the ones staleness scheduled, which is what
    /// stops one sign-in producing two Graph transactions. The event-driven fast path forces a
    /// refresh through <c>Request</c>; that pass commits and raises the ordinary state-change event;
    /// its <c>RequestIfStale</c> would otherwise see the same token still outstanding and force a
    /// second fetch over a snapshot committed moments earlier.
    /// </para>
    /// <para>
    /// The token is the one captured before the pass began, so a sign-in completed while this
    /// refresh was in flight writes a newer token that does not match and is still recovered.
    /// </para>
    /// <para>
    /// Outcomes that never reached a fetch leave it outstanding. A lease held by a peer, a
    /// contended mutex, a debounce, or a cancelled shutdown are all "no attempt was made", and the
    /// recovery is still owed — the peer may expire without committing anything.
    /// </para>
    /// </remarks>
    private void RetireSignInToken(Guid? token, RefreshOutcome outcome)
    {
        if (token is null)
        {
            return;
        }

        bool attempted = outcome is not (RefreshOutcome.SkippedLeaseHeld
            or RefreshOutcome.SkippedContention
            or RefreshOutcome.SkippedDebounce
            or RefreshOutcome.Cancelled);

        if (!attempted)
        {
            return;
        }

        lock (_gate)
        {
            _recoveredSignInToken = token;
        }
    }

    private async Task DrainAsync()
    {
        while (true)
        {
            RefreshWork work;

            lock (_gate)
            {
                if (_pending.Count == 0 || _shutdown.IsCancellationRequested)
                {
                    _running = false;
                    return;
                }

                work = _pending.Dequeue();
            }

            try
            {
                if (work.OnlyIfStale && !IsStale())
                {
                    continue;
                }

                // Captured before the pass so a sign-in completed while it runs writes a newer token
                // that this pass does not retire.
                Guid? signInToken = _readSignInToken();

                RefreshPresentationState previousState = _presentation.Begin();
                _delivery.RequestDelivery();

                RefreshResult result = await _coordinator
                    .RefreshAsync(_fetcher, work.Trigger, _shutdown.Token)
                    .ConfigureAwait(false);

                RetireSignInToken(signInToken, result.Outcome);

                long? peerWaitId = _presentation.Complete(result, previousState);

                if (peerWaitId is { } id)
                {
                    _ = _peerMonitor.RunAsync(
                        _presentation,
                        id,
                        result.PeerLeaseStartingGeneration,
                        _shutdown.Token);
                }

                // Token acquisition can change the card's authentication state even when there is
                // no snapshot to commit. RefreshCoordinator requests delivery only for a successful
                // commit, so noncommitted outcomes need a pass here to converge from Acquired to the
                // fail-closed card (or back again). Skipped outcomes may request a redundant pass;
                // DeliveryWorker deliberately coalesces those requests.
                if (result.Delivery == DeliveryRequestOutcome.NotRequested
                    && !_shutdown.IsCancellationRequested)
                {
                    _delivery.RequestDelivery();
                }
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                _presentation.Fail();
                // RefreshCoordinator and the production fetcher convert expected failures to values.
                // A final containment boundary keeps an unexpected defect off the COM callback path.
                _logger.Record(OperationalEventId.GraphRequestFailed, OperationalOutcome.Failed);

                // Preserve auth-state convergence even if an unexpected failure occurs after token
                // acquisition but before the coordinator can return a result.
                if (!_shutdown.IsCancellationRequested)
                {
                    _delivery.RequestDelivery();
                }
            }
        }
    }

    private long? ReadKnownGeneration()
    {
        // ReadGeneration intentionally collapses an inaccessible file to zero for compare-only
        // callers. Peer completion instead needs to distinguish a real zero from an unknown value,
        // because a false advance can clear authorization suppression.
        CacheReadResult read = _cache.Read();
        return read.Status == CacheReadStatus.Unreadable ? null : read.Generation;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _shutdown.Cancel();
        }

        try
        {
            _drain?.Wait(ShutdownDrain);
        }
        catch (Exception)
        {
            // Process shutdown is best effort and bounded.
        }

        _shutdown.Dispose();
    }

    private readonly record struct RefreshWork(RefreshTrigger Trigger, bool OnlyIfStale);
}
