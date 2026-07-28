using System.Text.Json;
using System.Text.Json.Serialization;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// The cross-process refresh lease: an expiring record, not a held lock.
/// </summary>
/// <remarks>
/// <para>
/// Single-flight cannot be expressed as a held primitive here. A named mutex would
/// have to be held across awaited WAM and Graph work, which its thread affinity
/// forbids; a named semaphore would avoid affinity but not death, because a killed
/// holder never releases its count. Either way the expiry machinery is required, so
/// the design uses the expiry machinery alone and holds nothing.
/// </para>
/// <para>
/// Crash recovery then falls out for free. A killed owner leaves an expired record,
/// and expiry alone reclaims it: no <see cref="AbandonedMutexException"/> dependence
/// and no separate watchdog timer.
/// </para>
/// </remarks>
public sealed record LeaseRecord
{
    /// <summary>Process ID of the owner, for diagnostics and opportunistic early reclaim only.</summary>
    [JsonPropertyName("pid")]
    public required int OwnerProcessId { get; init; }

    /// <summary>
    /// Identifies the owning coordinator instance. A PID can be reused; this cannot,
    /// so ownership comparison at release uses this rather than the PID.
    /// </summary>
    [JsonPropertyName("instance")]
    public required Guid OwnerInstanceId { get; init; }

    /// <summary>
    /// Expiry as an <see cref="ISystemClock.TickCount64"/> value. Per-boot monotonic
    /// and directly comparable across processes on the machine. Never a wall-clock
    /// time: a backwards clock step would make an expired lease look live.
    /// </summary>
    [JsonPropertyName("expiresAtTicks")]
    public required long ExpiresAtTicks { get; init; }

    /// <summary>
    /// Which boot session the tick value belongs to. Without this, a record written
    /// shortly before a reboot carries a large tick value that reads as far-future
    /// against a small post-boot tick value.
    /// </summary>
    [JsonPropertyName("bootStamp")]
    public required string BootStamp { get; init; }

    /// <summary>
    /// Whether this record is still live for a reader using <paramref name="clock"/>.
    /// A record from another boot session is expired by definition, whatever its tick
    /// value says.
    /// </summary>
    public bool IsLive(ISystemClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        if (!BootSessionStamp.IsCurrent(BootStamp, clock))
        {
            return false;
        }

        return clock.TickCount64 < ExpiresAtTicks;
    }

    /// <summary>Whether <paramref name="instanceId"/> owns this record.</summary>
    public bool IsOwnedBy(Guid instanceId) => OwnerInstanceId == instanceId;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact and stable. The record is machine-local coordination state, not a
        // document anyone reads by hand.
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public string Serialize() => JsonSerializer.Serialize(this, SerializerOptions);

    /// <summary>
    /// Parses a stored record, returning <see langword="null"/> when the content is
    /// absent, truncated, or malformed.
    /// </summary>
    /// <remarks>
    /// <b>An unparseable lease is treated as absent, and that direction is deliberate
    /// — note that it is the opposite of the disclosure tombstone's.</b> The two
    /// records fail safe in opposite directions because they protect against opposite
    /// harms. A tombstone that cannot be read must suppress, because the harm is
    /// disclosing data that should be hidden. A lease that cannot be read must be
    /// ignorable, because the harm is a refresh that can never run again: treating an
    /// unreadable lease as live would wedge refreshing permanently, whereas treating
    /// it as absent costs at most one duplicate request, which the generation compare
    /// at commit already handles.
    /// </remarks>
    public static LeaseRecord? TryParse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        try
        {
            LeaseRecord? record = JsonSerializer.Deserialize<LeaseRecord>(content, SerializerOptions);

            if (record is null || string.IsNullOrEmpty(record.BootStamp))
            {
                return null;
            }

            return record;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
