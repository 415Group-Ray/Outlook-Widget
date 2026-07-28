using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// Creates the two cross-process notification events and calls back when either is signalled.
/// </summary>
/// <remarks>
/// <para>
/// <b>The missing half of the signalling design.</b> <c>StateCommitCoordinator</c> and
/// <c>DisclosureTombstoneStore</c> both signal by calling
/// <see cref="EventWaitHandle.OpenExisting(string)"/> and swallowing
/// <see cref="WaitHandleCannotBeOpenedException"/> when nothing is listening. Until this type
/// existed, nothing ever created those events, so every cross-process signal in the product was
/// a silent no-op: correct by construction and never once delivered. This is the listener that
/// makes the companion's commits reach the provider.
/// </para>
/// <para>
/// <b>Manual-reset, and why that is safe despite the obvious lost-wakeup shape.</b> The signal
/// carries no payload and both events are manual-reset, because a signaller must be able to
/// notify every listener rather than exactly one. A manual-reset event stays set, so this loop
/// resets it after waking. The order is wait, reset, then notify — never notify then reset. A
/// signal arriving between the reset and the notification is not lost, because the callback
/// re-reads current state on disk rather than acting on anything the signal carried: a pass that
/// has not yet read state will see that change, and a pass that has will be followed by another.
/// State on disk is authoritative and the event is only an accelerant.
/// </para>
/// <para>
/// <b>Ownership is per-process, not global.</b> Each process creates the events by name;
/// whichever gets there first creates them and the rest open the same objects.
/// <see cref="Dispose"/> releases only this process's handles, so a companion exiting does not
/// destroy the provider's events, and the last handle closing simply means nothing is listening
/// — which the signallers already tolerate.
/// </para>
/// </remarks>
public sealed class StateChangeListener : IDisposable
{
    private readonly EventWaitHandle _stateChanged;
    private readonly EventWaitHandle _suppressDetails;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Action _onChanged;
    private readonly IOperationalLogger _logger;
    private readonly Thread _worker;

    private long _notifications;
    private bool _disposed;

    /// <param name="paths">Supplies the event names, so both processes agree on them.</param>
    /// <param name="onChanged">
    /// Invoked after either event fires. Must return promptly and must not throw; it is called on
    /// this listener's own thread, so blocking here delays every subsequent notification. In the
    /// provider this is <c>DeliveryWorker.RequestDelivery</c>, which only sets a marker.
    /// </param>
    /// <param name="logger">Metadata-free operational logging.</param>
    public StateChangeListener(
        CoordinationPaths paths,
        Action onChanged,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(onChanged);

        _onChanged = onChanged;
        _logger = logger ?? NullOperationalLogger.Instance;

        // EventResetMode.ManualReset with createdNew ignored: the first process in wins and the
        // others open the same named object, which is exactly the intent.
        _stateChanged = new EventWaitHandle(
            initialState: false, EventResetMode.ManualReset, paths.StateChangedEventName);
        _suppressDetails = new EventWaitHandle(
            initialState: false, EventResetMode.ManualReset, paths.SuppressDetailsEventName);

        _worker = new Thread(RunLoop)
        {
            IsBackground = true,
            Name = "OutlookWidget.StateChangeListener",
        };
        _worker.Start();
    }

    /// <summary>How many times the callback has been invoked. For assertions and diagnostics.</summary>
    public long Notifications => Interlocked.Read(ref _notifications);

    private void RunLoop()
    {
        WaitHandle[] handles = [_stateChanged, _suppressDetails, _shutdown.Token.WaitHandle];
        const int ShutdownIndex = 2;

        while (true)
        {
            int signalled = WaitHandle.WaitAny(handles);

            if (signalled == ShutdownIndex)
            {
                return;
            }

            // Reset before notifying, so a signal raised during the callback leaves the event set
            // and produces another pass rather than being absorbed by a later reset.
            try
            {
                ((EventWaitHandle)handles[signalled]).Reset();
            }
            catch (ObjectDisposedException)
            {
                // Shutdown won the race. Nothing left to notify.
                return;
            }

            Interlocked.Increment(ref _notifications);

            try
            {
                _onChanged();
            }
            catch (Exception)
            {
                // A failing callback must not end the only listener in the process. Losing this
                // thread would mean every future commit by the companion goes unnoticed until the
                // provider is recycled, which is a far worse outcome than one dropped
                // notification.
                _logger.Record(OperationalEventId.DeliveryFailed, OperationalOutcome.Failed);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        _shutdown.Cancel();

        // The callback is required to return promptly, so a bounded join is enough and an
        // unbounded one would risk holding up provider shutdown after the last widget is deleted.
        _worker.Join(TimeSpan.FromSeconds(2));

        _stateChanged.Dispose();
        _suppressDetails.Dispose();
        _shutdown.Dispose();
    }
}
