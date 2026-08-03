using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Provider;

/// <summary>Runs provider refreshes off the Widgets host callback threads.</summary>
internal sealed class ProviderRefreshWorker : IDisposable
{
    private static readonly TimeSpan ShutdownDrain = TimeSpan.FromSeconds(2);

    private readonly RefreshCoordinator _coordinator;
    private readonly IRefreshFetcher _fetcher;
    private readonly ProtectedCache _cache;
    private readonly IOperationalLogger _logger;
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
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        ArgumentNullException.ThrowIfNull(fetcher);
        ArgumentNullException.ThrowIfNull(cache);

        _coordinator = coordinator;
        _fetcher = fetcher;
        _cache = cache;
        _logger = logger ?? NullOperationalLogger.Instance;
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

                await _coordinator
                    .RefreshAsync(_fetcher, work.Trigger, _shutdown.Token)
                    .ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                // RefreshCoordinator and the production fetcher convert expected failures to values.
                // A final containment boundary keeps an unexpected defect off the COM callback path.
                _logger.Record(OperationalEventId.GraphRequestFailed, OperationalOutcome.Failed);
            }
        }
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
