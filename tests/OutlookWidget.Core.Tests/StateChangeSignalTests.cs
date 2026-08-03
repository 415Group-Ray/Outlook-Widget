using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Covers signalling a state change without committing one.
/// </summary>
/// <remarks>
/// This exists for a specific convergence bug. The provider probed authentication once per process and
/// lives until its last widget is unpinned, so the ordinary flow — a widget rendering "sign in
/// required", the user opening the companion from it and signing in — left the card asking for a
/// sign-in that had already happened. The companion now raises this signal and the provider re-probes.
/// </remarks>
public sealed class StateChangeSignalTests
{
    [Fact]
    public void A_raised_signal_reaches_a_listener()
    {
        using var fixture = new CoordinationFixture();
        using var woken = new ManualResetEventSlim(false);

        using var listener = new StateChangeListener(
            fixture.Paths,
            () => woken.Set(),
            fixture.Logger);

        bool delivered = StateChangeSignal.Raise(fixture.Paths);

        Assert.True(delivered, "Raise reported no listener while one was constructed.");

        Assert.True(
            woken.Wait(TimeSpan.FromSeconds(5)),
            "The listener was never invoked. Without this the companion's sign-in cannot reach a "
                + "running provider, and a pinned widget holds its original authentication result "
                + "until it is unpinned.");
    }

    [Fact]
    public void Raising_with_nothing_listening_is_reported_rather_than_thrown()
    {
        // The common case: the companion was opened from Start with no provider running. It must not
        // throw, because a sign-in that succeeded is not a failure just because nobody was listening.
        using var fixture = new CoordinationFixture();

        Assert.False(StateChangeSignal.Raise(fixture.Paths));
    }

    [Theory]
    [MemberData(nameof(OperationalOpenFailures))]
    public void Operational_named_event_failures_are_undelivered_accelerants(Exception failure)
    {
        bool delivered = NamedEventSignal.TryRaise(
            "OutlookWidget-Test-Signal",
            _ => throw failure);

        Assert.False(delivered);
    }

    public static TheoryData<Exception> OperationalOpenFailures =>
        new()
        {
            new WaitHandleCannotBeOpenedException("simulated"),
            new IOException("simulated"),
            new UnauthorizedAccessException("simulated"),
        };

    [Fact]
    public void Repeated_signals_each_produce_a_notification()
    {
        // The events are manual-reset and the listener resets before notifying, so a second signal
        // after the first was consumed must wake it again rather than being absorbed. A provider that
        // stopped converging after one sign-in would be worse than one that never converged, because
        // the failure would only appear on a second account switch.
        using var fixture = new CoordinationFixture();
        using var countdown = new CountdownEvent(2);

        using var listener = new StateChangeListener(
            fixture.Paths,
            () =>
            {
                if (!countdown.IsSet)
                {
                    countdown.Signal();
                }
            },
            fixture.Logger);

        Assert.True(StateChangeSignal.Raise(fixture.Paths));

        // Wait for the first to land before raising again, so this asserts two notifications rather
        // than accidentally coalescing into one.
        Assert.True(countdown.Wait(TimeSpan.FromSeconds(5)) || countdown.CurrentCount == 1);

        Assert.True(StateChangeSignal.Raise(fixture.Paths));

        Assert.True(
            countdown.Wait(TimeSpan.FromSeconds(5)),
            "A second signal did not produce a second notification.");
    }
}
