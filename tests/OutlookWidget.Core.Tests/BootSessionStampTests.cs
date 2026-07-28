using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The boot-session discriminator, and specifically its failure <em>direction</em>.
/// </summary>
/// <remarks>
/// A stale lease appearing live would wedge or corrupt coordination; an early reclaim costs
/// at most one duplicate refresh that the generation compare already handles. Every test here
/// asserts which of those two a given clock anomaly produces.
/// </remarks>
public sealed class BootSessionStampTests
{
    [Fact]
    public void Stamp_is_stable_as_time_passes_within_one_boot_session()
    {
        var clock = new TestClock();
        string before = BootSessionStamp.Current(clock);

        // Both clocks advance together, which is what ordinary elapsed time looks like —
        // including time asleep, since GetTickCount64 counts it.
        clock.Advance(TimeSpan.FromHours(6));

        Assert.Equal(before, BootSessionStamp.Current(clock));
    }

    [Fact]
    public void Stamp_changes_across_a_reboot()
    {
        var clock = new TestClock(initialTicks: 9_000_000);
        string beforeReboot = BootSessionStamp.Current(clock);

        clock.SimulateReboot(downtime: TimeSpan.FromMinutes(3));

        Assert.NotEqual(beforeReboot, BootSessionStamp.Current(clock));
    }

    [Fact]
    public void Lease_from_a_previous_boot_session_is_expired_however_large_its_tick_value()
    {
        var clock = new TestClock(initialTicks: 9_000_000);

        // A lease claimed just before the reboot, with an expiry far in the future by tick
        // value alone.
        var lease = new LeaseRecord
        {
            OwnerProcessId = 4242,
            OwnerInstanceId = Guid.NewGuid(),
            ExpiresAtTicks = clock.TickCount64 + 30_000,
            BootStamp = BootSessionStamp.Current(clock),
        };

        Assert.True(lease.IsLive(clock));

        clock.SimulateReboot(downtime: TimeSpan.FromMinutes(1), resumeTicks: 5_000);

        // Without the discriminator this would read as live: 5_000 < 9_030_000. The stamp is
        // the only thing that makes it expired, which is exactly why it exists.
        Assert.True(lease.ExpiresAtTicks > clock.TickCount64);
        Assert.False(lease.IsLive(clock));
    }

    [Fact]
    public void Wall_clock_step_causes_early_reclaim_rather_than_a_stale_lease_looking_live()
    {
        var clock = new TestClock();

        var lease = new LeaseRecord
        {
            OwnerProcessId = Environment.ProcessId,
            OwnerInstanceId = Guid.NewGuid(),
            ExpiresAtTicks = clock.TickCount64 + 30_000,
            BootStamp = BootSessionStamp.Current(clock),
        };

        Assert.True(lease.IsLive(clock));

        // An NTP correction or a manual clock change: wall-clock moves, no real time passes.
        clock.StepWallClock(TimeSpan.FromMinutes(10));

        // The derived stamp shifts, so the lease stops matching and is reclaimed early. The
        // opposite error is impossible: a mismatch can only ever read as expired.
        Assert.False(lease.IsLive(clock));
    }

    [Fact]
    public void Backwards_wall_clock_step_also_expires_rather_than_extending_a_lease()
    {
        var clock = new TestClock();

        var lease = new LeaseRecord
        {
            OwnerProcessId = Environment.ProcessId,
            OwnerInstanceId = Guid.NewGuid(),
            ExpiresAtTicks = clock.TickCount64 + 30_000,
            BootStamp = BootSessionStamp.Current(clock),
        };

        clock.StepWallClock(TimeSpan.FromHours(-2));

        Assert.False(lease.IsLive(clock));
    }

    [Fact]
    public void Small_sampling_jitter_between_two_processes_yields_the_same_stamp()
    {
        // Two processes computing the stamp a few milliseconds apart must normally agree,
        // otherwise every claim would reclaim every other process's live lease.
        var first = new TestClock(initialTicks: 1_000_000);
        var second = new TestClock(initialTicks: 1_000_003);
        second.StepWallClock(TimeSpan.FromMilliseconds(3));

        Assert.Equal(BootSessionStamp.Current(first), BootSessionStamp.Current(second));
    }

    [Fact]
    public void Absent_or_malformed_stamp_is_never_current()
    {
        var clock = new TestClock();

        Assert.False(BootSessionStamp.IsCurrent(null, clock));
        Assert.False(BootSessionStamp.IsCurrent(string.Empty, clock));
        Assert.False(BootSessionStamp.IsCurrent("not-a-timestamp", clock));
    }
}
