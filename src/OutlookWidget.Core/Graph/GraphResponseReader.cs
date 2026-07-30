using System.Globalization;
using System.Text.Json;
using OutlookWidget.Core.Launching;
using OutlookWidget.Core.Models;

namespace OutlookWidget.Core.Graph;

/// <summary>
/// Turns Graph JSON into the approved model, or refuses it.
/// </summary>
/// <remarks>
/// <para>
/// <b>Nothing here trusts the response because it was authenticated.</b> Section 8 step 5 requires
/// response types, maximum string lengths, URLs, timestamps, and item count all to be validated, and
/// every one of those checks lives in this file so a reviewer can see the whole boundary at once.
/// </para>
/// <para>
/// <b>Every refusal returns <see langword="false"/> and says nothing about what was wrong.</b> That
/// is not laziness: the only thing that could describe the fault is the response text, and section 6
/// forbids carrying it out of here. The caller reports
/// <see cref="GraphMailStatus.InvalidResponse"/> as a category, which is what
/// <c>IOperationalLogger</c> can record anyway.
/// </para>
/// <para>
/// <b>Missing optional fields degrade; missing required fields refuse.</b> A message with no subject
/// is an ordinary message and gets a local label. A message with no <c>receivedDateTime</c> cannot be
/// ordered or aged, so the response is rejected rather than the message silently dropped — dropping
/// it would quietly show four messages where five exist and look like an empty mailbox trend.
/// </para>
/// </remarks>
internal static class GraphResponseReader
{
    /// <summary>Section 7's sender preference order: the author, then the sending mailbox.</summary>
    private static readonly string[] SenderProperties = ["from", "sender"];

    /// <summary>A display name is preferred over an address, which is preferred over nothing.</summary>
    private static readonly string[] SenderFields = ["name", "address"];

    /// <summary>Reads the Inbox folder's two counts.</summary>
    internal static bool TryReadFolderCounts(JsonElement root, out int totalItemCount, out int unreadItemCount)
    {
        totalItemCount = 0;
        unreadItemCount = 0;

        return root.ValueKind == JsonValueKind.Object
               && TryReadCount(root, "totalItemCount", out totalItemCount)
               && TryReadCount(root, "unreadItemCount", out unreadItemCount);
    }

    /// <summary>
    /// Reads the <c>@odata.count</c> a filtered count query returns.
    /// </summary>
    /// <remarks>
    /// The returned message is deliberately ignored: the query asks for <c>$top=1</c> only because
    /// the endpoint requires a result shape, and the count is the answer. Reading the array instead
    /// would report 1 for any non-empty mailbox.
    /// </remarks>
    internal static bool TryReadODataCount(JsonElement root, out int count)
    {
        count = 0;
        return root.ValueKind == JsonValueKind.Object && TryReadCount(root, "@odata.count", out count);
    }

    /// <summary>
    /// Reads the message collection, bounded to <see cref="MailboxLimits.MaxMessages"/>.
    /// </summary>
    /// <remarks>
    /// The bound is applied here as well as in the request's <c>$top</c>, because a request parameter
    /// is something asked for rather than something guaranteed. Extra entries are truncated rather
    /// than treated as a fault: over-delivery is not evidence the first five are wrong.
    /// </remarks>
    internal static bool TryReadMessages(JsonElement root, out IReadOnlyList<MessagePreview> messages)
    {
        messages = [];

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("value", out JsonElement value)
            || value.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var previews = new List<MessagePreview>(MailboxLimits.MaxMessages);

        foreach (JsonElement element in value.EnumerateArray())
        {
            if (previews.Count == MailboxLimits.MaxMessages)
            {
                break;
            }

            if (!TryReadMessage(element, out MessagePreview? preview))
            {
                return false;
            }

            previews.Add(preview);
        }

        messages = previews;
        return true;
    }

    private static bool TryReadMessage(JsonElement element, out MessagePreview preview)
    {
        preview = null!;

        if (element.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        // Required. A message that cannot be dated cannot be ordered or aged out, and the 24-hour
        // stale-detail rule depends on ages being real.
        //
        // The style pair is deliberate and it is NOT the combination that is invalid on DateTime.
        // DateTime.TryParse rejects RoundtripKind together with AssumeUniversal and throws
        // ArgumentException for it; DateTimeOffset.TryParse validates styles differently and accepts
        // the pair. Verified rather than assumed, and pinned by a test, because it reads like a bug
        // and has already been reported as one.
        //
        // Both flags earn their place. Graph documents UTC with a trailing Z, and every value that
        // carries an offset is honoured by RoundtripKind. AssumeUniversal covers the case Graph does
        // not promise never to send: a timestamp with no offset at all, which RoundtripKind alone
        // would read as *local time* — silently shifting a received time by the machine's UTC offset
        // and, near midnight, onto the wrong day in the card's date formatting.
        if (!element.TryGetProperty("receivedDateTime", out JsonElement received)
            || received.ValueKind != JsonValueKind.String
            || !DateTimeOffset.TryParse(
                received.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal,
                out DateTimeOffset receivedAt))
        {
            return false;
        }

        // Required, and only True/False are accepted. A missing read state would have to be guessed,
        // and guessing "read" hides mail while guessing "unread" contradicts the folder count.
        if (!element.TryGetProperty("isRead", out JsonElement isRead)
            || isRead.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        preview = new MessagePreview
        {
            DisplaySender = ReadSender(element),
            Subject = ReadSubject(element),
            ReceivedAt = receivedAt.ToUniversalTime(),
            IsRead = isRead.GetBoolean(),
            WebLink = ReadWebLink(element),
        };

        return true;
    }

    /// <summary>
    /// The subject, trimmed, truncated, and labelled when empty.
    /// </summary>
    /// <remarks>
    /// A wrong type is treated as absent rather than as a fault. The field is optional in the model,
    /// so a non-string there is a malformed optional value, and rejecting the whole response over one
    /// would deny the user four perfectly good messages to protest about the fifth's subject.
    /// </remarks>
    private static string ReadSubject(JsonElement element)
    {
        string subject = element.TryGetProperty("subject", out JsonElement value)
                         && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

        return subject.Length == 0
            ? MailboxLimits.NoSubjectLabel
            : Truncate(subject, MailboxLimits.MaxSubjectLength);
    }

    /// <summary>
    /// The sender, preferring <c>from</c> over <c>sender</c> and a name over an address.
    /// </summary>
    /// <remarks>
    /// Section 7's order: <c>from</c>, then <c>sender</c>, then the unknown label. No body fallback
    /// is requested and none could be — <c>Mail.ReadBasic</c> does not return one.
    /// </remarks>
    private static string ReadSender(JsonElement element)
    {
        foreach (string property in SenderProperties)
        {
            if (!element.TryGetProperty(property, out JsonElement holder)
                || holder.ValueKind != JsonValueKind.Object
                || !holder.TryGetProperty("emailAddress", out JsonElement address)
                || address.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (string field in SenderFields)
            {
                if (address.TryGetProperty(field, out JsonElement text)
                    && text.ValueKind == JsonValueKind.String
                    && text.GetString()?.Trim() is { Length: > 0 } value)
                {
                    return Truncate(value, MailboxLimits.MaxSenderLength);
                }
            }
        }

        return MailboxLimits.UnknownSenderLabel;
    }

    /// <summary>
    /// The web link, or <see langword="null"/> when it is absent, malformed, oversized, or not HTTPS.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Truncating a URL would be worse than dropping it</b>, which is why the length rule differs
    /// from the two display strings: a truncated link is a live action that navigates somewhere other
    /// than the message. The message still renders without one.
    /// </para>
    /// <para>
    /// <b>The host is checked, not just the scheme, and checking only the scheme was the gap.</b>
    /// Section 11 accepts HTTPS links only from expected Outlook hosts; a response carrying
    /// <c>https://example.com/…</c> passed the old check purely for being absolute HTTPS and would
    /// have been cached as an openable action. <see cref="OutlookWebLink"/> holds the list, shared
    /// with the launch-time check section 3 requires, so the two cannot drift apart.
    /// </para>
    /// </remarks>
    private static string? ReadWebLink(JsonElement element)
    {
        if (!element.TryGetProperty("webLink", out JsonElement value)
            || value.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        string? link = value.GetString();

        if (link is not { Length: > 0 } || link.Length > MailboxLimits.MaxWebLinkLength)
        {
            return null;
        }

        return OutlookWebLink.IsAllowed(link) ? link : null;
    }

    /// <summary>
    /// Reads a non-negative integer count, rejecting anything else.
    /// </summary>
    /// <remarks>
    /// A JSON number that is fractional or outside <see cref="int"/> fails
    /// <see cref="JsonElement.TryGetInt32"/>, so the type and range checks are the same call. Negative
    /// is rejected separately because it parses fine and cannot mean anything for an item count.
    /// </remarks>
    private static bool TryReadCount(JsonElement root, string property, out int count)
    {
        count = 0;

        return root.TryGetProperty(property, out JsonElement value)
               && value.ValueKind == JsonValueKind.Number
               && value.TryGetInt32(out count)
               && count >= 0;
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];
}
