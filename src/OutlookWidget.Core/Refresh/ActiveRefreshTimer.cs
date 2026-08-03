using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// Requests an opportunistic refresh at a fixed interval while a surface reports itself active.
/// </summary>
/// <remarks>
/// <para>
/// This is deliberately only an opportunity, not a freshness guarantee. Windows may deactivate a
/// widget quickly or terminate its provider, so cached-first activation and manual refresh remain
/// the dependable triggers. The timer owns no background-service or process-lifetime policy.
/// </para>
/// <para>
/// The callback runs under the same short lock as <see cref="SetActive"/>. Consequently, after a
/// call that deactivates the timer returns, no later callback can enqueue work from an earlier tick.
/// The callback supplied by the provider only marks work pending; it must not perform the refresh
/// transaction itself or block on network or host I/O.
/// </para>
/// </remarks>
public sealed class ActiveRefreshTimer : IDisposable
{
    private readonly Lock _gate = new();
    private readonly Action _requestRefresh;
    private readonly IOperationalLogger _logger;
    private readonly TimeSpan _interval;
    private readonly ITimer _timer;
    private bool _active;
    private bool _disposed;

    public ActiveRefreshTimer(
        Action requestRefresh,
        IOperationalLogger? logger = null)
        : this(
            requestRefresh,
            logger,
            CoordinationBounds.ActiveTimerInterval,
            callback => new Timer(callback, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan))
    {
    }

    internal ActiveRefreshTimer(
        Action requestRefresh,
        IOperationalLogger? logger,
        TimeSpan interval,
        Func<TimerCallback, ITimer> createTimer)
    {
        ArgumentNullException.ThrowIfNull(requestRefresh);
        ArgumentNullException.ThrowIfNull(createTimer);

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(interval, TimeSpan.Zero);

        _requestRefresh = requestRefresh;
        _logger = logger ?? NullOperationalLogger.Instance;
        _interval = interval;
        _timer = createTimer(OnTick);
    }

    /// <summary>Starts or stops periodic refresh opportunities.</summary>
    public void SetActive(bool active)
    {
        lock (_gate)
        {
            if (_disposed || _active == active)
            {
                return;
            }

            _active = active;
            TimeSpan schedule = active ? _interval : Timeout.InfiniteTimeSpan;
            _timer.Change(schedule, schedule);
        }
    }

    private void OnTick(object? state)
    {
        lock (_gate)
        {
            if (_disposed || !_active)
            {
                return;
            }

            try
            {
                _requestRefresh();
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                // Timer callbacks run on the thread pool; an escaping exception can terminate the
                // process. Record only a closed category, then leave the repeating timer armed so a
                // transient queueing defect does not permanently remove this refresh opportunity.
                _logger.Record(
                    OperationalEventId.RefreshTimerCallbackFailed,
                    OperationalOutcome.Failed);
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
            _active = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Dispose();
        }
    }
}
