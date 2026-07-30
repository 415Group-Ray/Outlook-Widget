using System.Globalization;


namespace OutlookWidget.Core.Models;

/// <summary>
/// What one Graph read returned, before it is given an identity and a timestamp.
/// </summary>
/// <remarks>
/// <para>
/// <b>Separate from <see cref="MailboxSnapshot"/> on purpose, and it lives here rather than in
/// <c>Models</c> for the same reason.</b> The client knows what the mailbox said; it does not know
/// which account was selected, which tenant the registration belongs to, or when the refresh
/// transaction committed. Folding those in would either make the client take arguments it has no use
/// for, or let a snapshot be built carrying an account that was never checked against the token the
/// read used.
/// </para>
/// <para>
/// So this type is the Graph boundary's output and the snapshot is the product's cached state.
/// <see cref="MailboxSnapshot.Create"/> is the one place the two meet.
/// </para>
/// </remarks>
public sealed class MailboxReadout
{
    /// <summary>Total items in the Inbox folder, as the folder itself reports.</summary>
    public required int TotalItemCount { get; init; }

    /// <summary>
    /// Unread items in the Inbox folder. Authoritative for the folder and inclusive of all item
    /// types, including meeting requests — so it is not expected to reconcile item-for-item with
    /// <see cref="Messages"/>, and the two are labelled differently for that reason.
    /// </summary>
    public required int UnreadItemCount { get; init; }

    /// <summary>
    /// The Focused unread count, or <see langword="null"/> when the setting is off or the optional
    /// request failed. Null is a first-class value here: section 7 requires the optional count's
    /// failure to leave the two required results intact.
    /// </summary>
    public int? FocusedUnreadCount { get; init; }

    /// <summary>At most <see cref="MailboxLimits.MaxMessages"/> previews, newest first.</summary>
    public required IReadOnlyList<MessagePreview> Messages { get; init; }

    /// <summary>Counts only, never content. See <see cref="MessagePreview"/>'s remarks.</summary>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{nameof(MailboxReadout)} ({Messages.Count} messages, {UnreadItemCount} unread)");
}
