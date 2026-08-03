using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

public sealed class ActiveRefreshTimerTests
{
    [Fact]
    public void Timer_starts_disabled_and_repeats_only_while_active()
    {
        var timer = new ManualTimer();
        int requests = 0;

        using var active = new ActiveRefreshTimer(
            () => requests++,
            logger: null,
            TimeSpan.FromMinutes(5),
            callback => timer.Attach(callback));

        Assert.Empty(timer.Schedules);
        timer.Fire();
        Assert.Equal(0, requests);

        active.SetActive(true);

        Assert.Equal(
            (TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)),
            Assert.Single(timer.Schedules));

        timer.Fire();
        timer.Fire();
        Assert.Equal(2, requests);

        // Idempotent activation does not push the next opportunity five minutes farther out.
        active.SetActive(true);
        Assert.Single(timer.Schedules);

        active.SetActive(false);
        Assert.Equal(
            (Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan),
            timer.Schedules[^1]);

        timer.Fire();
        Assert.Equal(2, requests);
    }

    [Fact]
    public void Reactivation_arms_a_new_full_interval()
    {
        var timer = new ManualTimer();

        using var active = new ActiveRefreshTimer(
            () => { },
            logger: null,
            TimeSpan.FromMinutes(5),
            callback => timer.Attach(callback));

        active.SetActive(true);
        active.SetActive(false);
        active.SetActive(true);

        Assert.Equal(3, timer.Schedules.Count);
        Assert.Equal(
            (TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(5)),
            timer.Schedules[^1]);
    }

    [Fact]
    public void A_callback_failure_is_contained_and_does_not_disarm_the_timer()
    {
        var timer = new ManualTimer();
        var logger = new RecordingLogger();
        int attempts = 0;

        using var active = new ActiveRefreshTimer(
            () =>
            {
                attempts++;
                if (attempts == 1)
                {
                    throw new InvalidOperationException("test");
                }
            },
            logger,
            TimeSpan.FromMinutes(5),
            callback => timer.Attach(callback));

        active.SetActive(true);
        timer.Fire();
        timer.Fire();

        Assert.Equal(2, attempts);
        Assert.True(logger.Saw(
            OperationalEventId.RefreshTimerCallbackFailed,
            OperationalOutcome.Failed));
    }

    [Fact]
    public void Dispose_disarms_the_timer_and_ignores_later_activation()
    {
        var timer = new ManualTimer();
        int requests = 0;
        var active = new ActiveRefreshTimer(
            () => requests++,
            logger: null,
            TimeSpan.FromMinutes(5),
            callback => timer.Attach(callback));

        active.SetActive(true);
        active.Dispose();
        active.SetActive(true);
        timer.Fire();

        Assert.True(timer.IsDisposed);
        Assert.Equal(0, requests);
        Assert.Equal(
            (Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan),
            timer.Schedules[^1]);
    }

    private sealed class ManualTimer : ITimer
    {
        private TimerCallback? _callback;

        public List<(TimeSpan DueTime, TimeSpan Period)> Schedules { get; } = [];

        public bool IsDisposed { get; private set; }

        public ManualTimer Attach(TimerCallback callback)
        {
            _callback = callback;
            return this;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedules.Add((dueTime, period));
            return true;
        }

        public void Fire() => _callback?.Invoke(null);

        public void Dispose() => IsDisposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
