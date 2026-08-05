using System.Text.Json;
using OutlookWidget.Core.Models;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The shortening applied to mailbox-controlled text, for storage and for display.
/// </summary>
/// <remarks>
/// Both bounds previously sliced at a UTF-16 index in independently written code. A sender or
/// subject is chosen by anyone who can send mail to this mailbox, so a cut landing between the
/// halves of a surrogate pair is reachable input rather than a curiosity — and the resulting lone
/// surrogate cannot be encoded as JSON, which aborted a refresh commit on the cache path and dropped
/// a delivery pass on the render path. These tests exist so that class of failure has to be
/// deliberately reintroduced.
/// </remarks>
public sealed class MailboxTextTests
{
    /// <summary>A single emoji: one grapheme, one rune, two UTF-16 code units.</summary>
    private const string Emoji = "\U0001F600";

    /// <summary>Family emoji: one grapheme built from four runes joined by zero-width joiners.</summary>
    private const string JoinedEmoji = "\U0001F468‍\U0001F469‍\U0001F467";

    [Fact]
    public void A_storage_clamp_never_splits_a_surrogate_pair()
    {
        // The reported defect, at its exact boundary: the budget falls between the two halves.
        string value = new string('a', 32) + Emoji + "trailing";

        string clamped = MailboxText.ClampToLength(value, 33);

        Assert.Equal(new string('a', 32), clamped);
        Assert.DoesNotContain(clamped, c => char.IsSurrogate(c));
    }

    [Fact]
    public void A_storage_clamp_keeps_a_pair_that_fits_whole()
    {
        string value = new string('a', 32) + Emoji + "trailing";

        string clamped = MailboxText.ClampToLength(value, 34);

        Assert.Equal(new string('a', 32) + Emoji, clamped);
    }

    [Fact]
    public void A_storage_clamp_never_exceeds_its_budget()
    {
        // Dropping the orphaned high surrogate must shorten the result, never lengthen it. The
        // budget bounds memory against a hostile response, so growing to preserve a character would
        // defeat the reason it exists.
        string value = string.Concat(Enumerable.Repeat(Emoji, 40));

        for (int budget = 0; budget <= 20; budget++)
        {
            Assert.True(MailboxText.ClampToLength(value, budget).Length <= budget);
        }
    }

    [Fact]
    public void A_storage_clamp_leaves_a_short_value_untouched()
    {
        Assert.Equal("short", MailboxText.ClampToLength("short", 255));
        Assert.Equal(string.Empty, MailboxText.ClampToLength(string.Empty, 255));
    }

    [Fact]
    public void Clamped_storage_text_still_serializes()
    {
        // The failure the clamp exists to prevent, reproduced end to end: System.Text.Json refuses
        // to encode a lone surrogate, so an unsafe cut turned one message into a failed commit.
        string value = new string('a', 32) + Emoji + "trailing";

        string clamped = MailboxText.ClampToLength(value, 33);

        // Would throw if the clamp left half a pair behind.
        string json = JsonSerializer.Serialize(clamped);

        Assert.NotNull(json);
    }

    [Fact]
    public void A_display_clamp_counts_perceived_characters_not_code_units()
    {
        // Ten emoji are twenty UTF-16 units but ten characters to a reader, so a budget of ten fits
        // them all. Counting units would truncate a string that fits.
        string value = string.Concat(Enumerable.Repeat(Emoji, 10));

        Assert.Equal(value, MailboxText.ForDisplay(value, 10));
    }

    [Fact]
    public void A_display_clamp_takes_a_joined_emoji_whole_or_not_at_all()
    {
        // A rune-based clamp fixes surrogate pairs and still cuts this one into pieces, leaving a
        // dangling zero-width joiner and a stray person. Grapheme counting means the only two
        // outcomes are all of it or none of it — never a fragment.
        string value = new string('a', 10) + JoinedEmoji + new string('b', 40);

        // Budget reached before the emoji: it is excluded entirely, joiners and all.
        string excluded = MailboxText.ForDisplay(value, 11);

        Assert.Equal(new string('a', 10) + MailboxText.Ellipsis, excluded);
        Assert.DoesNotContain('‍', excluded);

        // Budget reaching past it: the whole sequence survives, joiners included.
        string included = MailboxText.ForDisplay(value, 12);

        Assert.Equal(new string('a', 10) + JoinedEmoji + MailboxText.Ellipsis, included);
    }

    [Fact]
    public void A_display_clamp_marks_what_it_shortened()
    {
        string clamped = MailboxText.ForDisplay(new string('a', 100), 10);

        Assert.EndsWith(MailboxText.Ellipsis, clamped, StringComparison.Ordinal);
        Assert.Equal(10, clamped.Length);
    }

    [Fact]
    public void A_display_clamp_leaves_a_value_inside_the_budget_alone()
    {
        Assert.Equal("Rob Mitchell", MailboxText.ForDisplay("Rob Mitchell", 34));
    }

    [Fact]
    public void A_display_clamp_flattens_control_characters()
    {
        // A subject containing a newline is well formed and cacheable, and would wrap a card element
        // that has no wrap control — growing a card the Widgets host clips without reporting.
        string clamped = MailboxText.ForDisplay("first\r\nsecond\tthird", 34);

        Assert.Equal("first second third", clamped);
    }

    [Fact]
    public void Display_output_always_serializes_whatever_the_cut_lands_on()
    {
        // Every budget against a string whose every boundary is hostile: a surrogate pair at each
        // position. Any cut that split one would fail to encode.
        string value = string.Concat(Enumerable.Repeat(Emoji, 30));

        for (int budget = 1; budget <= 40; budget++)
        {
            string clamped = MailboxText.ForDisplay(value, budget);

            JsonSerializer.Serialize(clamped);
        }
    }

    [Fact]
    public void A_display_clamp_rejects_a_budget_that_cannot_hold_anything()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => MailboxText.ForDisplay("value", 0));
    }
}
