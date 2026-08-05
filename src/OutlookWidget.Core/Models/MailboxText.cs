using System.Globalization;
using System.Text;

namespace OutlookWidget.Core.Models;

/// <summary>
/// The single place mailbox-controlled text is shortened, for storage or for display.
/// </summary>
/// <remarks>
/// <para>
/// <b>This type exists because the same defect was written twice, independently.</b> Both the cache
/// bound in <c>GraphResponseReader</c> and the display bound in the provider's card sliced a string
/// at a UTF-16 index. A sender or subject is chosen by anyone who can send mail to this mailbox, so
/// a cut landing between the halves of a surrogate pair is a reachable input, not a curiosity: it
/// leaves a lone surrogate, which is not valid UTF-16, and <c>System.Text.Json</c> refuses to encode
/// it. In the cache path that failure aborts the refresh commit; in the render path it drops a
/// delivery pass. Both look like an intermittent outage caused by one particular message.
/// </para>
/// <para>
/// <b>The two bounds are genuinely different and are kept separate on purpose.</b>
/// <see cref="ClampToLength"/> bounds how much is stored, so it must not exceed the caller's UTF-16
/// budget — a limit that exists to bound memory against a hostile response. <see cref="ForDisplay"/>
/// bounds how much is seen, so it counts what a reader perceives as characters and may return a
/// longer UTF-16 string than its budget. Collapsing them into one function would silently break
/// whichever caller did not get the semantics it needed.
/// </para>
/// <para>
/// This lives in the core rather than beside either caller for a reason beyond deduplication: the
/// provider is not referenced by the test project and cannot be unit-tested, so string handling with
/// hostile inputs belongs where it can have real tests rather than a source-level assertion. The
/// caller still owns its budget — the number of characters that fit a widget line is a
/// Widgets-host detail and stays in the provider.
/// </para>
/// </remarks>
public static class MailboxText
{
    /// <summary>Marks display text that was shortened. One character, not three dots.</summary>
    public const string Ellipsis = "…";

    /// <summary>
    /// Shortens <paramref name="value"/> to at most <paramref name="maxUnits"/> UTF-16 code units
    /// without splitting a surrogate pair.
    /// </summary>
    /// <remarks>
    /// For storage bounds. The result never exceeds the budget: when the cut would land inside a
    /// surrogate pair the high surrogate is dropped as well, so the string shortens by one more unit
    /// rather than growing. No ellipsis, because a stored value is data rather than presentation and
    /// the caller may bound it again for display.
    /// </remarks>
    public static string ClampToLength(string value, int maxUnits)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegative(maxUnits);

        if (value.Length <= maxUnits)
        {
            return value;
        }

        // A high surrogate as the last retained unit means the pair straddles the boundary. Its low
        // half is being discarded, so the high half has to go too.
        int cut = maxUnits;
        if (cut > 0 && char.IsHighSurrogate(value[cut - 1]))
        {
            cut--;
        }

        return value[..cut];
    }

    /// <summary>
    /// Prepares <paramref name="value"/> for rendering on one line of at most
    /// <paramref name="maxTextElements"/> perceived characters.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Counts grapheme clusters rather than code units or runes, so an emoji built from a
    /// zero-width-joiner sequence, or a letter followed by combining marks, is one character and is
    /// never cut through the middle. Counting runes would fix surrogate pairs and still split those.
    /// </para>
    /// <para>
    /// Control characters are flattened to spaces first. A subject containing a newline is
    /// well-formed, cacheable, and would wrap a card element that has no wrap control — growing a
    /// card the Widgets host clips without reporting. Runs of whitespace collapse so the flattening
    /// does not leave gaps.
    /// </para>
    /// </remarks>
    public static string ForDisplay(string value, int maxTextElements)
    {
        ArgumentNullException.ThrowIfNull(value);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxTextElements);

        string flat = Flatten(value);

        // A UTF-16 length inside the budget cannot hold more grapheme clusters than the budget, so
        // the common case never walks the string.
        if (flat.Length <= maxTextElements)
        {
            return flat;
        }

        TextElementEnumerator elements = StringInfo.GetTextElementEnumerator(flat);
        int seen = 0;
        int cut = -1;

        while (elements.MoveNext())
        {
            seen++;

            // Where the last element that fits alongside the ellipsis begins.
            if (seen == maxTextElements)
            {
                cut = elements.ElementIndex;
            }
            else if (seen > maxTextElements)
            {
                break;
            }
        }

        // Long in UTF-16 units but short in perceived characters: nothing to shorten.
        return seen <= maxTextElements
            ? flat
            : string.Concat(flat.AsSpan(0, cut).TrimEnd(), Ellipsis);
    }

    /// <summary>Replaces control characters with spaces and collapses whitespace runs.</summary>
    private static string Flatten(string value)
    {
        bool hasControl = false;

        foreach (char candidate in value)
        {
            if (char.IsControl(candidate))
            {
                hasControl = true;
                break;
            }
        }

        if (!hasControl)
        {
            return value;
        }

        var builder = new StringBuilder(value.Length);
        bool previousWasSpace = false;

        foreach (char candidate in value)
        {
            char next = char.IsControl(candidate) ? ' ' : candidate;

            if (next == ' ' && previousWasSpace)
            {
                continue;
            }

            builder.Append(next);
            previousWasSpace = next == ' ';
        }

        return builder.ToString().Trim();
    }
}
