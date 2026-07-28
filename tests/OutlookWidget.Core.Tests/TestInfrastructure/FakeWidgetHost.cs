using System.Text;
using OutlookWidget.Core.Delivery;

namespace OutlookWidget.Core.Tests.TestInfrastructure;

/// <summary>
/// A widget host that can be stalled, made to fail, and interrogated afterwards.
/// </summary>
/// <remarks>
/// Models the two properties of the real host that matter to the design: the call is
/// synchronous and void, and it can block for an unbounded time. A stalled delivery is not an
/// artificial scenario — it is the wedged Widgets host the plan repeatedly reasons about, and
/// the only way to observe that an in-flight payload cannot be retracted.
/// </remarks>
internal sealed class FakeWidgetHost : IWidgetDeliverySink, IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<DeliveryState> _delivered = [];
    private readonly ManualResetEventSlim _stall = new(initialState: true);
    private readonly ManualResetEventSlim _entered = new(initialState: false);

    private int _concurrentCalls;
    private int _maxConcurrentCalls;
    private bool _throwOnDeliver;

    /// <summary>Every state handed to the host, in order.</summary>
    public IReadOnlyList<DeliveryState> Delivered
    {
        get
        {
            lock (_gate)
            {
                return [.. _delivered];
            }
        }
    }

    /// <summary>
    /// The highest number of simultaneous calls observed. Must never exceed one: the whole
    /// ordering guarantee rests on no two host calls being in flight.
    /// </summary>
    public int MaxConcurrentCalls => Volatile.Read(ref _maxConcurrentCalls);

    /// <summary>The payload of the most recent delivery, decoded, or null if it carried none.</summary>
    public string? LastPayloadText
    {
        get
        {
            lock (_gate)
            {
                if (_delivered.Count == 0)
                {
                    return null;
                }

                byte[]? payload = _delivered[^1].Payload;
                return payload is null ? null : Encoding.UTF8.GetString(payload);
            }
        }
    }

    public void ThrowOnNextDeliveries() => Volatile.Write(ref _throwOnDeliver, true);

    /// <summary>Makes the next call block until <see cref="Release"/>.</summary>
    public void Stall()
    {
        _entered.Reset();
        _stall.Reset();
    }

    /// <summary>Releases a stalled call.</summary>
    public void Release() => _stall.Set();

    /// <summary>
    /// Waits until a call has actually entered the host, so a test can commit newer state
    /// knowing the older payload is genuinely already in flight and no longer recallable.
    /// </summary>
    public bool WaitUntilInsideDeliver(TimeSpan timeout) => _entered.Wait(timeout);

    public void Deliver(DeliveryState state)
    {
        int concurrent = Interlocked.Increment(ref _concurrentCalls);

        // Track the maximum rather than asserting inline, so a violation is reported by the
        // test rather than as an exception from a background worker thread.
        int observedMax = Volatile.Read(ref _maxConcurrentCalls);
        while (concurrent > observedMax
               && Interlocked.CompareExchange(ref _maxConcurrentCalls, concurrent, observedMax) != observedMax)
        {
            observedMax = Volatile.Read(ref _maxConcurrentCalls);
        }

        try
        {
            _entered.Set();

            // Unbounded by design. UpdateWidget has no documented timeout and no cancellation.
            _stall.Wait();

            lock (_gate)
            {
                _delivered.Add(state);
            }

            if (Volatile.Read(ref _throwOnDeliver))
            {
                throw new InvalidOperationException("Simulated Widgets host failure.");
            }
        }
        finally
        {
            Interlocked.Decrement(ref _concurrentCalls);
        }
    }

    public void Dispose()
    {
        _stall.Set();
        _stall.Dispose();
        _entered.Dispose();
    }
}
