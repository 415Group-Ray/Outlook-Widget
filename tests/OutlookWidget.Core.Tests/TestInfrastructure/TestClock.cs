using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Tests.TestInfrastructure;

/// <summary>
/// A controllable <see cref="ISystemClock"/>.
/// </summary>
/// <remarks>
/// The two clocks move independently on purpose. That is the only way to test the
/// behaviours the design actually claims: advancing ticks without wall-clock simulates
/// elapsed time within a boot session, advancing wall-clock without ticks simulates an NTP
/// step, and resetting ticks while wall-clock continues simulates a reboot. A single
/// combined fake clock could express none of those.
/// </remarks>
internal sealed class TestClock : ISystemClock
{
    private long _ticks;
    private DateTimeOffset _utcNow;

    public TestClock(long initialTicks = 1_000_000, DateTimeOffset? initialUtc = null)
    {
        _ticks = initialTicks;
        _utcNow = initialUtc ?? new DateTimeOffset(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);
    }

    public long TickCount64 => Interlocked.Read(ref _ticks);

    public DateTimeOffset UtcNow => _utcNow;

    /// <summary>Ordinary passage of time: both clocks advance together.</summary>
    public void Advance(TimeSpan amount)
    {
        Interlocked.Add(ref _ticks, (long)amount.TotalMilliseconds);
        _utcNow = _utcNow.Add(amount);
    }

    /// <summary>
    /// A wall-clock correction with no real time passing: an NTP step or a manual change.
    /// The derived boot stamp shifts, so existing leases stop matching.
    /// </summary>
    public void StepWallClock(TimeSpan amount) => _utcNow = _utcNow.Add(amount);

    /// <summary>
    /// A reboot: tick count restarts near zero while wall-clock keeps going. This is the
    /// case that makes a boot-session discriminator necessary, because a pre-reboot record's
    /// large tick value would otherwise read as far-future.
    /// </summary>
    public void SimulateReboot(TimeSpan downtime, long resumeTicks = 5_000)
    {
        _utcNow = _utcNow.Add(downtime);
        Interlocked.Exchange(ref _ticks, resumeTicks);
    }
}
