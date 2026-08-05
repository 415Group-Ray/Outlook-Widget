namespace OutlookWidget.Core.Refresh;

/// <summary>
/// Answers how much a widget may display right now.
/// </summary>
/// <remarks>
/// An interface so the delivery worker depends on the question rather than on the two sources that
/// answer it. <see cref="DisclosureTombstoneStore"/> answers it for in-flight operations alone;
/// <see cref="DisclosurePolicy"/> answers it for those plus the user's standing preference.
/// </remarks>
public interface IDisclosurePolicy
{
    /// <summary>The strongest suppression currently in force.</summary>
    DisclosureMode GetEffectiveMode();
}

/// <summary>
/// The whole disclosure question: in-flight operations, and the standing privacy setting.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two independent sources, combined by taking the stronger.</b> A tombstone says "an operation
/// that reduces disclosure is running right now"; the setting says "the user asked for counts
/// only, permanently". Neither can weaken the other, which is why this is a maximum rather than a
/// precedence rule — the same property that makes the per-operation tombstone files safe without a
/// lock, applied one level up.
/// </para>
/// <para>
/// <b>Both sources fail closed, and they fail to different modes on purpose.</b> Unreadable
/// suppression state means an operation may be mid-flight with unknown intent, so the tombstone
/// store answers <see cref="DisclosureMode.SignedOut"/>. Unreadable settings mean only that a
/// rendering preference is unknown, so the settings store answers counts-only. Escalating a corrupt
/// preferences file all the way to a signed-out card would tell the user they are signed out when
/// they are not, which is its own kind of wrong answer.
/// </para>
/// <para>
/// Read on every delivery pass rather than cached, because both sources are written by another
/// process and delivery re-reads its inputs immediately before each host call. A cached preference
/// would be exactly the staleness the delivery contract exists to prevent.
/// </para>
/// </remarks>
public sealed class DisclosurePolicy : IDisclosurePolicy
{
    private readonly DisclosureTombstoneStore _tombstones;
    private readonly WidgetSettingsStore _settings;

    public DisclosurePolicy(DisclosureTombstoneStore tombstones, WidgetSettingsStore settings)
    {
        ArgumentNullException.ThrowIfNull(tombstones);
        ArgumentNullException.ThrowIfNull(settings);

        _tombstones = tombstones;
        _settings = settings;
    }

    public DisclosureMode GetEffectiveMode()
    {
        DisclosureMode inFlight = _tombstones.GetEffectiveMode();

        // Read regardless, rather than short-circuiting when the tombstone already says SignedOut.
        // The cost is one file read on the rarest path, and the benefit is that this method has no
        // branch whose behaviour depends on evaluation order — which is the property that makes the
        // maximum below trustworthy.
        SettingsReadResult settings = _settings.Read();

        DisclosureMode standing = settings.Settings.HideMessageDetails
            ? DisclosureMode.CountsOnly
            : DisclosureMode.Full;

        // The enum's numeric ordering is load-bearing and documented as such: stronger suppression
        // is a larger value, so "the strongest wins" is a comparison.
        return inFlight >= standing ? inFlight : standing;
    }
}
