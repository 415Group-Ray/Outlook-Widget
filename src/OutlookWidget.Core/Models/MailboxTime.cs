using System.Globalization;

namespace OutlookWidget.Core.Models;

/// <summary>
/// How an instant from a cached snapshot is written for a reader.
/// </summary>
/// <remarks>
/// <para>
/// <b>Section 7 states the rule and it is deliberately binary:</b> render in the user's current
/// Windows timezone, use the locale's short time for something from the current local date, the
/// locale's short date for anything older, and never a relative string such as "2 minutes ago"
/// because a cached card can outlive that label.
/// </para>
/// <para>
/// A weekday was once rendered for the previous six days, on the reasoning that "Mon" is friendlier
/// than a date. It is also ambiguous the moment the card outlives the week — which is the exact
/// failure the no-relative-strings rule exists to prevent, arrived at from the other direction. The
/// rule is binary because the middle ground does not survive a card that is not re-delivered.
/// </para>
/// <para>
/// This sits in the core, and takes the current instant as an argument, so the rule can be tested.
/// The provider that renders it cannot be: the test project does not reference it. Formatting that
/// encodes an approved specification belongs where a test can hold it to that specification.
/// </para>
/// </remarks>
public static class MailboxTime
{
    /// <summary>
    /// Labels when a message arrived: short time from today, short date before that.
    /// </summary>
    public static string ReceivedLabel(DateTimeOffset receivedAt, DateTimeOffset now) =>
        Format(receivedAt, now, includeTimeOnOlderDates: false);

    /// <summary>
    /// Labels when the snapshot was last refreshed.
    /// </summary>
    /// <remarks>
    /// Carries the time as well as the date once it is not from today, because this answers "how
    /// current is what you are looking at" rather than "when did this arrive", and a bare date drops
    /// the precision that question needs. Still absolute, so it does not decay like a relative
    /// label.
    /// </remarks>
    public static string UpdatedLabel(DateTimeOffset refreshedAt, DateTimeOffset now) =>
        Format(refreshedAt, now, includeTimeOnOlderDates: true);

    private static string Format(DateTimeOffset instant, DateTimeOffset now, bool includeTimeOnOlderDates)
    {
        DateTime local = instant.ToLocalTime().DateTime;

        if (local.Date == now.ToLocalTime().Date)
        {
            // "t" — the locale's short time.
            return local.ToString("t", CultureInfo.CurrentCulture);
        }

        // "d" is the locale's short date; "g" is that plus the short time.
        return local.ToString(includeTimeOnOlderDates ? "g" : "d", CultureInfo.CurrentCulture);
    }
}
