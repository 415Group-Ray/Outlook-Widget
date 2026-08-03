using OutlookWidget.Core.Caching;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// Raises the package-user-wide state-changed event.
/// </summary>
/// <remarks>
/// <para>
/// <b>Uses the shared named-event helper.</b> State changes and disclosure suppression use separate
/// events, but both are accelerants over authoritative state on disk. Their operational failure
/// policy must remain identical instead of drifting between two exception filters.
/// </para>
/// <para>
/// <b>Signalling without a commit is legitimate, and narrowly so.</b> The coordinator's own rule is
/// that it signals only after a successful commit, because "a signal without a generation change
/// teaches listeners to distrust the signal". That rule is about the *snapshot* generation, and it
/// still holds. This exists for state that is real but lives outside the snapshot — an account
/// appearing in the shared token cache after the companion signs in, which changes what the provider
/// can do and changes no generation. Without it, the provider would keep rendering a
/// sign-in-required card while a perfectly good token sat in the broker.
/// </para>
/// <para>
/// The event is payload-free and every listener re-reads state for itself, so a listener woken by
/// this cannot tell it apart from a commit and does not need to.
/// </para>
/// </remarks>
public static class StateChangeSignal
{
    /// <summary>
    /// Signals that committed state or authentication state changed. Never throws.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> if a listener's event was set; <see langword="false"/> if nothing is
    /// listening. The return value is for diagnostics and tests — no caller should behave differently,
    /// because state on disk is authoritative and the event is only an accelerant.
    /// </returns>
    public static bool Raise(CoordinationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return NamedEventSignal.TryRaise(paths.StateChangedEventName);
    }
}
