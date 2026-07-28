using System.Text;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Delivery ordering. These tests assert <em>final convergence</em>, which is the guarantee the
/// design actually offers, and deliberately do not assert the absence of a transient older
/// render, which it cannot offer: an in-flight <c>UpdateWidget</c> cannot be retracted.
/// </summary>
public sealed class DeliveryWorkerTests
{
    private static byte[] Payload(string content) => Encoding.UTF8.GetBytes(content);

    private static bool WaitFor(Func<bool> condition, TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            Thread.Sleep(10);
        }

        return condition();
    }

    [Fact]
    public void A_request_delivers_the_currently_committed_state()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("committed"));
        worker.RequestDelivery();

        Assert.True(WaitFor(() => worker.CompletedPasses >= 1, TimeSpan.FromSeconds(5)));
        Assert.Equal("committed", host.LastPayloadText);
        Assert.Equal(1, host.Delivered[^1].Generation);
    }

    [Fact]
    public void The_trigger_carries_no_payload_so_content_is_chosen_at_delivery_time()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("first"));

        // Stall so the request is made against the first state but the pass has not read yet.
        host.Stall();
        worker.RequestDelivery();
        Assert.True(host.WaitUntilInsideDeliver(TimeSpan.FromSeconds(5)));

        // The pass has already read "first" and is inside the call, so this commit cannot change
        // what it delivers — that is the un-retractable window, and it is real.
        fixture.SeedState(Payload("second"));
        host.Release();

        Assert.True(WaitFor(() => worker.CompletedPasses >= 1, TimeSpan.FromSeconds(5)));

        // A fresh request now picks up the newer state without anyone handing it a payload.
        worker.RequestDelivery();
        Assert.True(WaitFor(() => host.LastPayloadText == "second", TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void No_two_host_calls_are_ever_in_flight()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("state"));

        // Hammer the worker from many threads. Serialization is the property that removes
        // interleaving altogether, so there is no ordering left to get wrong.
        Parallel.For(0, 200, _ => worker.RequestDelivery());

        Assert.True(WaitFor(() => worker.CompletedPasses >= 1, TimeSpan.FromSeconds(10)));
        Assert.Equal(1, host.MaxConcurrentCalls);
    }

    [Fact]
    public void Concurrent_requests_coalesce_rather_than_queueing_one_pass_each()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("state"));

        host.Stall();
        worker.RequestDelivery();
        Assert.True(host.WaitUntilInsideDeliver(TimeSpan.FromSeconds(5)));

        // Fifty requests arrive while one pass is in flight. The pending marker has depth one,
        // so they collapse into a single follow-up pass — N queued requests and one queued
        // request produce identical output, because the pass reads current state.
        for (int i = 0; i < 50; i++)
        {
            worker.RequestDelivery();
        }

        host.Release();

        Assert.True(WaitFor(() => worker.CompletedPasses >= 2, TimeSpan.FromSeconds(5)));

        // Let the worker settle, then confirm it did not run fifty-one passes.
        Thread.Sleep(200);
        Assert.True(
            worker.CompletedPasses <= 3,
            $"Expected coalescing to a small number of passes, saw {worker.CompletedPasses}.");
        Assert.True(worker.CoalescedRequests >= 49);
    }

    [Fact]
    public void A_request_arriving_mid_pass_produces_exactly_one_follow_up_pass()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("first"));

        host.Stall();
        worker.RequestDelivery();
        Assert.True(host.WaitUntilInsideDeliver(TimeSpan.FromSeconds(5)));

        fixture.SeedState(Payload("second"));
        worker.RequestDelivery();

        host.Release();

        // The follow-up pass re-reads state, so the final content is the newer generation rather
        // than the stalled pass's.
        Assert.True(WaitFor(() => host.LastPayloadText == "second", TimeSpan.FromSeconds(5)));
        Assert.Equal(2, host.Delivered[^1].Generation);
    }

    [Fact]
    public void Delivery_converges_on_the_signed_out_card_after_an_account_switch_behind_a_stalled_pass()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("previous account subjects"));

        host.Stall();
        worker.RequestDelivery();
        Assert.True(host.WaitUntilInsideDeliver(TimeSpan.FromSeconds(5)));

        // An account switch commits while the older payload is already inside the host call.
        DisclosureSuppression suppression = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);
        worker.RequestDelivery();

        host.Release();

        Assert.True(WaitFor(() => worker.CompletedPasses >= 2, TimeSpan.FromSeconds(5)));

        // The assertion is the converged end state, not the absence of a transient older
        // payload. The stalled pass's content had already entered UpdateWidget and cannot be
        // recalled; what the design guarantees is that the last thing delivered is correct.
        Assert.Equal(DisclosureMode.SignedOut, host.Delivered[^1].Mode);
        Assert.Null(host.Delivered[^1].Payload);

        Assert.False(suppression.IsCleared);
    }

    [Fact]
    public void A_pass_that_has_not_yet_entered_the_host_call_never_builds_pre_change_content()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();

        fixture.SeedState(Payload("previous account subjects"));

        // Suppress before the worker exists, so no pass can ever have read the pre-change state.
        fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);
        worker.RequestDelivery();

        Assert.True(WaitFor(() => worker.CompletedPasses >= 1, TimeSpan.FromSeconds(5)));

        // The payload is withheld entirely rather than passed along with a mode flag the sink is
        // trusted to honour. A sink cannot leak what it was never given.
        Assert.All(host.Delivered, state =>
        {
            Assert.Equal(DisclosureMode.SignedOut, state.Mode);
            Assert.Null(state.Payload);
        });
    }

    [Fact]
    public void Counts_only_suppression_still_supplies_the_payload_for_the_counts()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("counts and messages"));
        fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);

        worker.RequestDelivery();
        Assert.True(WaitFor(() => worker.CompletedPasses >= 1, TimeSpan.FromSeconds(5)));

        // Counts-only hides message details, not the counts themselves, so the sink needs the
        // snapshot. Only the signed-out mode withholds it.
        Assert.Equal(DisclosureMode.CountsOnly, host.Delivered[^1].Mode);
        Assert.NotNull(host.Delivered[^1].Payload);
    }

    [Fact]
    public void A_failing_host_is_recorded_and_does_not_stop_later_passes()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("first"));
        host.ThrowOnNextDelivery();

        worker.RequestDelivery();
        Assert.True(WaitFor(
            () => fixture.Logger.Saw(Diagnostics.OperationalEventId.DeliveryFailed),
            TimeSpan.FromSeconds(5)));

        // A wedged or failing host is a host-level failure the provider cannot fix. It must not
        // corrupt refresh accounting or block the next activation, so the worker keeps running.
        long passesBefore = worker.CompletedPasses;
        worker.RequestDelivery();

        Assert.True(WaitFor(() => worker.CompletedPasses > passesBefore, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public void A_host_cancellation_is_recorded_and_does_not_stop_later_passes()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("state"));
        host.CancelNextDelivery();

        worker.RequestDelivery();
        Assert.True(WaitFor(
            () => fixture.Logger.Saw(Diagnostics.OperationalEventId.DeliveryFailed),
            TimeSpan.FromSeconds(5)));

        long passesBefore = worker.CompletedPasses;
        worker.RequestDelivery();

        Assert.True(WaitFor(() => worker.CompletedPasses > passesBefore, TimeSpan.FromSeconds(5)));
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.DeliveryCompleted));
    }

    [Fact]
    public void Disposing_while_a_pass_is_stalled_does_not_hang_shutdown()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();

        fixture.SeedState(Payload("state"));

        var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        host.Stall();
        worker.RequestDelivery();
        Assert.True(host.WaitUntilInsideDeliver(TimeSpan.FromSeconds(5)));

        // The provider must exit cleanly after the last enabled widget is deleted. Blocking
        // shutdown on a wedged host would turn a rendering problem into a process that will not
        // exit, so the join is bounded.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        worker.Dispose();
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(4),
            $"Dispose took {stopwatch.Elapsed} while the host was stalled.");
    }

    [Fact]
    public void Requests_racing_disposal_do_not_throw()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();

        fixture.SeedState(Payload("state"));

        var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        // An Activate callback or state-changed listener can request delivery at the exact moment
        // the provider shuts down after its last widget is deleted. An unsynchronized disposal
        // flag left a window between the check and the semaphore release, so the requester could
        // touch a disposed semaphore and throw ObjectDisposedException out of a host callback.
        var exceptions = new System.Collections.Concurrent.ConcurrentBag<Exception>();

        using var start = new ManualResetEventSlim(false);

        var requesters = Enumerable.Range(0, 8).Select(_ => new Thread(() =>
        {
            start.Wait();
            for (int i = 0; i < 500; i++)
            {
                try
                {
                    worker.RequestDelivery();
                }
                catch (Exception e)
                {
                    exceptions.Add(e);
                }
            }
        })).ToArray();

        foreach (Thread thread in requesters)
        {
            thread.Start();
        }

        start.Set();
        Thread.Sleep(20);
        worker.Dispose();

        foreach (Thread thread in requesters)
        {
            thread.Join(TimeSpan.FromSeconds(10));
        }

        Assert.Empty(exceptions);
    }

    [Fact]
    public void Requesting_delivery_after_disposal_is_a_no_op()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();

        fixture.SeedState(Payload("state"));

        var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);
        worker.Dispose();

        worker.RequestDelivery();

        Assert.Equal(0, host.MaxConcurrentCalls);
    }

    [Fact]
    public void A_cleared_cache_is_delivered_as_cleared_rather_than_as_corruption()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        fixture.SeedState(Payload("previous account"));

        using (MutationLock heldLock = fixture.Mutex.Acquire())
        {
            fixture.Cache.Clear(heldLock);
        }

        worker.RequestDelivery();
        Assert.True(WaitFor(() => worker.CompletedPasses >= 1, TimeSpan.FromSeconds(5)));

        // What the sink is told matters: Cleared is authoritative signed-out state and renders the
        // signed-out card, while Corrupt renders an error or recovery card. Reporting a normal
        // sign-out as corruption would show the user a fault they did not cause.
        Assert.Equal(Caching.CacheReadStatus.Cleared, host.Delivered[^1].ReadStatus);
        Assert.Null(host.Delivered[^1].Payload);
    }

    [Fact]
    public void An_absent_snapshot_still_produces_a_delivery_so_the_sink_can_render_signed_out()
    {
        using var fixture = new CoordinationFixture();
        using var host = new FakeWidgetHost();
        using var worker = new DeliveryWorker(fixture.Cache, fixture.Tombstones, host, fixture.Logger);

        worker.RequestDelivery();

        Assert.True(WaitFor(() => worker.CompletedPasses >= 1, TimeSpan.FromSeconds(5)));

        // Delivering nothing would leave a first-run widget blank instead of showing the
        // signed-out card with its sign-in action.
        Assert.Equal(Caching.CacheReadStatus.Absent, host.Delivered[^1].ReadStatus);
        Assert.Null(host.Delivered[^1].Payload);
    }
}
