using System.Globalization;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// A derived boot-session identifier, used to tell a live lease from one left by a
/// previous boot.
/// </summary>
/// <remarks>
/// <para>
/// Lease expiry is measured with <see cref="ISystemClock.TickCount64"/>, which is
/// per-boot monotonic and restarts at zero when the machine boots. Without a
/// boot-session discriminator a record written shortly before a reboot would carry a
/// large tick value that, compared against a small post-boot tick value, reads as
/// "expires far in the future" — a stale lease appearing live, which is the one
/// failure direction this design must not permit.
/// </para>
/// <para>
/// Windows exposes no managed boot-identity API, so the stamp is derived:
/// <c>UtcNow - TickCount64</c> approximates the instant of boot. Because
/// <c>GetTickCount64</c> includes time spent asleep, that value is stable within a
/// boot session. Two processes computing it at slightly different moments differ by
/// a few milliseconds, so the result is quantized to absorb that jitter.
/// </para>
/// <para>
/// Quantization reduces jitter sensitivity but does not eliminate it: two samples a
/// few milliseconds apart can still straddle a quantum boundary and round to
/// different values. That case is rare and lands in the same safe direction as every
/// other mismatch — the reader treats the lease as expired and reclaims early.
/// </para>
/// <para>
/// <b>Failure direction.</b> A large wall-clock correction — an NTP step or a manual
/// clock change — shifts the computed stamp, so an existing lease stops matching and
/// is treated as expired. The consequence is an early reclaim, at worst one
/// duplicate refresh, which the generation compare at commit already handles. The
/// opposite error cannot occur: any mismatch reads as expired, never as live.
/// </para>
/// </remarks>
public static class BootSessionStamp
{
    /// <summary>
    /// Quantization applied to the derived boot instant. Large enough to absorb the
    /// jitter between two processes sampling the clock at different moments, small
    /// enough that a genuine reboot is never mistaken for the same session — a
    /// machine cannot boot, run, and reboot inside one quantum in any way that
    /// matters, and if it somehow did, both records would be equally stale.
    /// </summary>
    public static readonly TimeSpan Quantum = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Computes the current boot-session stamp as a fixed-format, culture-invariant
    /// UTC string suitable for byte-for-byte equality comparison.
    /// </summary>
    public static string Current(ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        DateTimeOffset bootInstant = clock.UtcNow - TimeSpan.FromMilliseconds(clock.TickCount64);

        long quantumTicks = Quantum.Ticks;
        long quantized = (bootInstant.UtcTicks + (quantumTicks / 2)) / quantumTicks * quantumTicks;

        // Round-trip format with a fixed culture. The stamp is only ever compared
        // for equality, so its stability as text is the property that matters.
        return new DateTimeOffset(quantized, TimeSpan.Zero)
            .ToString("yyyy-MM-ddTHH:mm:ss.fffffffZ", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Whether a stored stamp belongs to the current boot session. A stamp that is
    /// absent, malformed, or from another session is not current, so the caller
    /// treats the record as expired.
    /// </summary>
    public static bool IsCurrent(string? storedStamp, ISystemClock clock) =>
        !string.IsNullOrEmpty(storedStamp)
        && string.Equals(storedStamp, Current(clock), StringComparison.Ordinal);
}
