namespace OutlookWidget.Core.Refresh;

/// <summary>
/// The two distinct notions of time the coordination design depends on, kept
/// behind one seam so the boot-session and clock-step behaviours can actually be
/// tested rather than reasoned about.
/// </summary>
/// <remarks>
/// <para>
/// These are not interchangeable and the distinction is the whole point.
/// <see cref="TickCount64"/> is per-boot monotonic and is the only source used for
/// measuring durations and lease expiry. <see cref="UtcNow"/> is wall-clock, is
/// subject to NTP steps and manual changes, and is used for exactly one purpose:
/// deriving the boot-session discriminator, and there only for equality.
/// </para>
/// <para>
/// Substituting wall-clock time for the tick-based expiry would reintroduce the
/// failure this separation exists to prevent — a backwards clock step making an
/// expired lease look live.
/// </para>
/// </remarks>
public interface ISystemClock
{
    /// <summary>
    /// Milliseconds since the machine booted, including time spent asleep.
    /// Per-boot monotonic and directly comparable across processes on the machine.
    /// </summary>
    long TickCount64 { get; }

    /// <summary>
    /// Wall-clock UTC. Used only to derive the boot-session discriminator, and
    /// only for an equality comparison — never for measuring a duration.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>The real machine clock.</summary>
public sealed class SystemClock : ISystemClock
{
    public static SystemClock Instance { get; } = new();

    public long TickCount64 => Environment.TickCount64;

    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
