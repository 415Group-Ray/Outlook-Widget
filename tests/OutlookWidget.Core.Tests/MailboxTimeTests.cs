using System.Globalization;
using OutlookWidget.Core.Models;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Section 7's rule for rendering an instant from a cached snapshot.
/// </summary>
/// <remarks>
/// The rule is binary — short time from the current local date, short date before it, never a
/// relative string — and the provider once added a third case, rendering a weekday for the previous
/// six days. That is ambiguous the moment a card outlives the week, which is the same failure the
/// no-relative-strings rule exists to prevent. These tests hold the implementation to the
/// specification rather than to what looks friendly.
/// </remarks>
public sealed class MailboxTimeTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 8, 5, 14, 30, 0, TimeSpan.Zero);

    private static DateTimeOffset LocalInstant(int daysAgo, int hour, int minute) =>
        new DateTimeOffset(
            DateTime.SpecifyKind(
                new DateTime(2026, 8, 5, hour, minute, 0).AddDays(-daysAgo),
                DateTimeKind.Local));

    [Fact]
    public void A_message_from_today_shows_the_locale_short_time()
    {
        DateTimeOffset received = LocalInstant(daysAgo: 0, hour: 9, minute: 5);

        string expected = received.ToLocalTime().DateTime.ToString("t", CultureInfo.CurrentCulture);

        Assert.Equal(expected, MailboxTime.ReceivedLabel(received, Now));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(9)]
    [InlineData(40)]
    public void A_message_from_any_earlier_day_shows_the_locale_short_date(int daysAgo)
    {
        // 1 through 6 are the range that previously rendered a weekday. There is no boundary in the
        // specification, so there must be none in the output.
        DateTimeOffset received = LocalInstant(daysAgo, hour: 9, minute: 5);

        string expected = received.ToLocalTime().DateTime.ToString("d", CultureInfo.CurrentCulture);

        Assert.Equal(expected, MailboxTime.ReceivedLabel(received, Now));
    }

    [Fact]
    public void A_received_label_never_renders_a_weekday_name()
    {
        // The specific regression: "Mon" for something three days old.
        DateTimeOffset received = LocalInstant(daysAgo: 3, hour: 9, minute: 5);

        string label = MailboxTime.ReceivedLabel(received, Now);

        foreach (string day in CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames)
        {
            Assert.DoesNotContain(day, label, StringComparison.CurrentCultureIgnoreCase);
        }
    }

    [Fact]
    public void An_update_label_from_today_shows_only_the_time()
    {
        DateTimeOffset refreshed = LocalInstant(daysAgo: 0, hour: 16, minute: 58);

        string expected = refreshed.ToLocalTime().DateTime.ToString("t", CultureInfo.CurrentCulture);

        Assert.Equal(expected, MailboxTime.UpdatedLabel(refreshed, Now));
    }

    [Fact]
    public void An_update_label_from_an_earlier_day_carries_the_date_as_well()
    {
        // Distinct from a received label, and on purpose: this answers how current the card is, so a
        // bare date drops the precision that question needs. A stale card reading "Updated 4:58 PM"
        // is the failure being avoided.
        DateTimeOffset refreshed = LocalInstant(daysAgo: 3, hour: 16, minute: 58);

        string expected = refreshed.ToLocalTime().DateTime.ToString("g", CultureInfo.CurrentCulture);

        Assert.Equal(expected, MailboxTime.UpdatedLabel(refreshed, Now));
        Assert.NotEqual(
            refreshed.ToLocalTime().DateTime.ToString("t", CultureInfo.CurrentCulture),
            MailboxTime.UpdatedLabel(refreshed, Now));
    }

    [Fact]
    public void A_label_is_never_relative()
    {
        // Cached cards outlive the phrase. Absolute forms only, at every age.
        string[] relativeWords = ["ago", "yesterday", "today", "now", "minute", "hour"];

        foreach (int daysAgo in (int[])[0, 1, 3, 30])
        {
            DateTimeOffset instant = LocalInstant(daysAgo, hour: 11, minute: 15);

            foreach (string label in (string[])
                     [
                         MailboxTime.ReceivedLabel(instant, Now),
                         MailboxTime.UpdatedLabel(instant, Now),
                     ])
            {
                foreach (string word in relativeWords)
                {
                    Assert.DoesNotContain(word, label, StringComparison.OrdinalIgnoreCase);
                }
            }
        }
    }
}
