namespace OutlookWidget.Core.Refresh;

/// <summary>Best-effort signalling for package-user-wide named events.</summary>
/// <remarks>
/// Named events are convergence accelerants. Durable state remains authoritative, so an absent,
/// inaccessible, or otherwise unopenable event must never turn a completed state mutation or a
/// published disclosure tombstone into a failed operation.
/// </remarks>
internal static class NamedEventSignal
{
    public static bool TryRaise(string eventName) =>
        TryRaise(eventName, EventWaitHandle.OpenExisting);

    internal static bool TryRaise(
        string eventName,
        Func<string, EventWaitHandle> openExisting)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(openExisting);

        try
        {
            using EventWaitHandle namedEvent = openExisting(eventName);
            return namedEvent.Set();
        }
        catch (Exception e) when (e is WaitHandleCannotBeOpenedException
                                     or IOException
                                     or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
