using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>How a settings change ended.</summary>
public enum SettingsChangeOutcome
{
    /// <summary>Written, and the provider signalled.</summary>
    Applied,

    /// <summary>The stored value already matched. Nothing was written and nothing was suppressed.</summary>
    Unchanged,

    /// <summary>The write failed. See <see cref="SettingsChangeResult.DetailsRemainHidden"/>.</summary>
    WriteFailed,

    /// <summary>
    /// Written, but this operation's tombstone could not be removed, so details stay hidden until
    /// explicit recovery clears it.
    /// </summary>
    SuppressionClearFailed,
}

/// <summary>The bounded result of one settings change.</summary>
/// <param name="Outcome">How it ended.</param>
/// <param name="DetailsRemainHidden">
/// Whether the widget is left suppressed. True whenever a disclosure-reducing change published its
/// tombstone and did not clear it — which is the fail-closed direction, and the reason a failure
/// here is reported rather than retried silently.
/// </param>
/// <param name="ProviderNotified">
/// Whether the running provider was told, or <see langword="null"/> when nothing was written and so
/// there was nothing to tell it.
/// </param>
/// <remarks>
/// <b><see cref="ProviderNotified"/> is separate from the outcome on purpose.</b> The named event is
/// an accelerant over authoritative disk state, so a provider that is not running — or an event this
/// process cannot open — must never turn a committed mutation into a failure. But it must not be
/// silently discarded either: the setting is stored and a live widget may keep rendering the old
/// disclosure until something else prompts it, which is a thing the companion can usefully say and
/// nothing else is positioned to notice.
/// </remarks>
public readonly record struct SettingsChangeResult(
    SettingsChangeOutcome Outcome,
    bool DetailsRemainHidden,
    bool? ProviderNotified = null)
{
    /// <summary>Whether the stored settings now match what was asked for.</summary>
    public bool IsApplied =>
        Outcome is SettingsChangeOutcome.Applied
            or SettingsChangeOutcome.Unchanged
            or SettingsChangeOutcome.SuppressionClearFailed;
}

/// <summary>
/// Applies a settings change with the ordering its direction requires.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only one direction is dangerous, and only that direction pays for a tombstone.</b> Turning
/// "hide message details" <em>on</em> reduces what the widget may show, so it follows the same
/// suppress-first ordering as logout and account switch: publish the counts-only tombstone, then
/// attempt the write. If the write fails, the tombstone stays and the widget shows counts only —
/// the state the user asked for, reached by the safe path rather than the committed one. Turning it
/// <em>off</em> increases disclosure and commits normally, because there is no safety argument for
/// pre-emptively revealing more.
/// </para>
/// <para>
/// <b>An unreadable stored value is unknown, not "already hidden".</b> The read substitutes a
/// fail-closed value so a caller that ignores the status cannot disclose more than it should — but
/// that substitution is for rendering, not for deciding whether a change is needed. Treating it as
/// the current preference made a hide request compare equal and return unchanged, dropping it: the
/// user's choice went unrecorded, and details could reappear as soon as the file became readable.
/// An unknown state therefore always writes, which records the preference and repairs the file in
/// the same step.
/// </para>
/// <para>
/// This is the one settings path. The companion must not write
/// <see cref="WidgetSettingsStore.Write"/> directly, for the same reason it must not write
/// <c>SelectedAccountStore</c> directly: the write is one step of an ordered operation, and a
/// caller that performs only that step skips the ordering that makes it safe.
/// </para>
/// </remarks>
public sealed class SettingsChangeCoordinator
{
    private readonly WidgetSettingsStore _settings;
    private readonly DisclosureTombstoneStore _tombstones;
    private readonly IOperationalLogger _logger;
    private readonly Func<bool> _signalStateChanged;

    public SettingsChangeCoordinator(
        CoordinationPaths paths,
        WidgetSettingsStore settings,
        DisclosureTombstoneStore tombstones,
        IOperationalLogger? logger = null)
        : this(
            settings,
            tombstones,
            logger,
            () => NamedEventSignal.TryRaise((paths ?? throw new ArgumentNullException(nameof(paths)))
                .StateChangedEventName))
    {
    }

    internal SettingsChangeCoordinator(
        WidgetSettingsStore settings,
        DisclosureTombstoneStore tombstones,
        IOperationalLogger? logger,
        Func<bool> signalStateChanged)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(tombstones);
        ArgumentNullException.ThrowIfNull(signalStateChanged);

        _settings = settings;
        _tombstones = tombstones;
        _logger = logger ?? NullOperationalLogger.Instance;
        _signalStateChanged = signalStateChanged;
    }

    /// <summary>Stores <paramref name="desired"/>, suppressing first when that reduces disclosure.</summary>
    public SettingsChangeResult Apply(WidgetSettings desired)
    {
        ArgumentNullException.ThrowIfNull(desired);

        SettingsReadResult current = _settings.Read();

        // **The status decides whether a comparison is possible at all, not the substituted value.**
        // An unreadable file renders as counts-only, and an earlier version treated that as "already
        // hidden" and reported a hide request Unchanged — dropping it. The stored preference was
        // never unknown-and-therefore-satisfied: it was unknown. If the unreadability was transient,
        // or the user is asking precisely because the file is damaged, the request would be silently
        // discarded and details could reappear the moment the file became readable again.
        //
        // So an unknown state always writes. That both records the preference and repairs the file.
        bool storedValueIsKnown =
            current.Status is SettingsReadStatus.Success or SettingsReadStatus.Absent;

        if (storedValueIsKnown
            && current.Settings.HideMessageDetails == desired.HideMessageDetails)
        {
            // "Nothing to write" is not the same as "nothing is hidden". A previous hide attempt
            // whose write failed left its counts-only tombstone behind with the stored setting
            // still false, so a later request to show details finds nothing to change and would
            // otherwise report success while the card stays suppressed until explicit recovery
            // removes the marker. The answer is about what the user will see, not about what this
            // operation did.
            return new SettingsChangeResult(
                SettingsChangeOutcome.Unchanged,
                DetailsRemainHidden: SuppressionIsInForce());
        }

        return desired.HideMessageDetails
            ? Reduce(desired)
            : Reveal(desired);
    }

    /// <summary>Turning details off: suppress first, then write.</summary>
    private SettingsChangeResult Reduce(WidgetSettings desired)
    {
        DisclosureSuppression suppression = _tombstones.Suppress(DisclosureMode.CountsOnly);

        try
        {
            try
            {
                _settings.Write(desired);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The tombstone stays. The user asked for details to be hidden and they are hidden,
                // by the fail-closed path rather than the committed one — so this reports a failure
                // whose visible effect is nonetheless what was asked for.
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.PrivacySettingChangeFailed, OperationalOutcome.Failed);
                return new SettingsChangeResult(SettingsChangeOutcome.WriteFailed, DetailsRemainHidden: true);
            }

            // Signalled before the tombstone is cleared, so the provider converges on the committed
            // setting rather than on a window where neither is in force.
            bool notified = _signalStateChanged();

            suppression.CommitAndClear();

            if (!suppression.IsCleared)
            {
                // One bounded retry, matching sign-out: a sharing violation here is often transient.
                suppression.CommitAndClear();
            }

            if (!suppression.IsCleared)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.PrivacySettingChangeFailed, OperationalOutcome.Failed);

                // The setting is stored and the widget is suppressed by a marker nothing will clear
                // on its own. Both agree today — counts only — so the visible state is correct, and
                // it stops being correct only if the user later turns details back on.
                return new SettingsChangeResult(
                    SettingsChangeOutcome.SuppressionClearFailed,
                    DetailsRemainHidden: true,
                    ProviderNotified: notified);
            }

            _logger.Record(OperationalEventId.PrivacySettingChanged, OperationalOutcome.Success);

            // Our own marker is gone, but another operation's may not be, so the honest answer is
            // read rather than assumed.
            return new SettingsChangeResult(
                SettingsChangeOutcome.Applied,
                DetailsRemainHidden: SuppressionIsInForce(),
                ProviderNotified: notified);
        }
        finally
        {
            // Any escape after publication must leave the marker on disk but stop presenting this
            // operation as live, so same-process explicit recovery can see it.
            if (!suppression.IsCleared)
            {
                suppression.CompleteWithoutClearing();
            }
        }
    }

    /// <summary>Turning details back on: no tombstone, because nothing is being hidden.</summary>
    private SettingsChangeResult Reveal(WidgetSettings desired)
    {
        try
        {
            _settings.Write(desired);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing was suppressed, so nothing is stuck. The previous setting stands and the
            // widget keeps hiding details until the user tries again.
            _logger.Record(OperationalEventId.PrivacySettingChangeFailed, OperationalOutcome.Failed);
            return new SettingsChangeResult(SettingsChangeOutcome.WriteFailed, DetailsRemainHidden: true);
        }

        bool notified = _signalStateChanged();

        _logger.Record(OperationalEventId.PrivacySettingChanged, OperationalOutcome.Success);

        // The setting now says show details, which does not mean they are shown: a stranded marker
        // from an earlier failed operation still suppresses until recovery clears it, and this is
        // the path a user takes to undo exactly that kind of failure.
        return new SettingsChangeResult(
            SettingsChangeOutcome.Applied,
            DetailsRemainHidden: SuppressionIsInForce(),
            ProviderNotified: notified);
    }

    /// <summary>
    /// Whether any tombstone still suppresses, regardless of who wrote it.
    /// </summary>
    /// <remarks>
    /// Read from the tombstone store rather than from this operation's own handle, because the
    /// question is what the widget will render and not what this call did. A marker stranded by an
    /// earlier failure suppresses just as effectively as one this operation published.
    /// </remarks>
    private bool SuppressionIsInForce() =>
        _tombstones.GetEffectiveMode() != DisclosureMode.Full;
}
