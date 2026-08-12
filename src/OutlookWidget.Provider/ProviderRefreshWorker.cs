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
    private readonly CancellationTokenSource _shutdown = new();
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
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selectedAccounts);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(presentation);

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

                RefreshPresentationState previousState = _presentation.Begin();
                _delivery.RequestDelivery();

                RefreshResult result = await _coordinator
                    .RefreshAsync(_fetcher, work.Trigger, _shutdown.Token)
                    .ConfigureAwait(false);

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
