using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OutlookWidget.Core.Models;

/// <summary>
/// The bounds every value read from Graph is held to before it is cached or rendered.
/// </summary>
/// <remarks>
/// <para>
/// <b>These exist because the response is not trusted merely because it was authenticated.</b> A
/// subject is attacker-influenced content — anyone who can send mail to this mailbox chooses it — and
/// it flows into an Adaptive Card and into a DPAPI-protected file. Unbounded strings there are a
/// memory and rendering problem rather than a correctness one, which is exactly the kind of defect
/// that never appears until someone sends a megabyte subject line.
/// </para>
/// <para>
/// Truncation rather than rejection for the two display strings, and rejection for the link. A long
/// subject is still a useful card; a malformed or non-HTTPS <c>webLink</c> is an action that must not
/// be offered at all, and dropping the link keeps the rest of the message.
/// </para>
/// </remarks>
public static class MailboxLimits
{
    /// <summary>The plan's five-message ceiling, applied to the response as well as to the request.</summary>
    /// <remarks>
    /// The request already carries <c>$top=5</c>. This is applied again on the response because
    /// section 7 requires the client to bound the result "even if a malformed response contains
    /// more" — a server-side bound is a request, not a guarantee.
    /// </remarks>
    public const int MaxMessages = 5;

    /// <summary>Subjects longer than this are truncated.</summary>
    public const int MaxSubjectLength = 255;

    /// <summary>Display sender names longer than this are truncated.</summary>
    public const int MaxSenderLength = 128;

    /// <summary>Links longer than this are dropped rather than truncated.</summary>
    public const int MaxWebLinkLength = 2048;

    /// <summary>Shown in place of an empty subject. Applied locally; Graph is never asked for it.</summary>
    public const string NoSubjectLabel = "(No subject)";

    /// <summary>Shown when neither <c>from</c> nor <c>sender</c> carries a usable name or address.</summary>
    public const string UnknownSenderLabel = "Unknown sender";
}

/// <summary>
/// One cached message preview: the approved fields and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <b>A class with an explicit <see cref="object.ToString"/>, not a record, for the reason
/// <c>TokenAcquisitionResult</c> is.</b> A positional or synthesised record <c>ToString</c> prints
/// every member, so interpolating one of these into an exception message, a debugger-adjacent
/// diagnostic, or a stray <c>Console.WriteLine</c> would emit a sender and a subject line — mailbox
/// content that section 11 forbids leaving this process. The override below prints nothing about the
/// message.
/// </para>
/// <para>
/// There is deliberately no message body, body preview, attachment, recipient list, or message ID
/// here. The ID is requested (section 7 documents the <c>$select</c>) but not modelled: section 8's
/// cache-contents list does not include it, and nothing in v1 addresses a message by ID — the
/// <c>webLink</c> is what opens one.
/// </para>
/// </remarks>
public sealed class MessagePreview
{
    /// <summary>The sender's display name, or their address, or the unknown-sender label.</summary>
    public required string DisplaySender { get; init; }

    /// <summary>The subject, or the no-subject label. Never a body or body preview.</summary>
    public required string Subject { get; init; }

    /// <summary>
    /// When the message was received, as an absolute instant. Rendered in the user's current
    /// timezone at display time rather than formatted here, because a cached card can outlive both
    /// the timezone it was written in and any relative phrasing.
    /// </summary>
    public required DateTimeOffset ReceivedAt { get; init; }

    /// <summary>Whether the message has been read.</summary>
    public required bool IsRead { get; init; }

    /// <summary>
    /// The Outlook web link, or <see langword="null"/> when the response carried none that passed
    /// validation. A null link means the message renders without an open action rather than with a
    /// broken one.
    /// </summary>
    public string? WebLink { get; init; }

    /// <summary>Says nothing about the message. See the type remarks.</summary>
    public override string ToString() => nameof(MessagePreview);
}

/// <summary>
/// The cached mailbox state, exactly as section 8 lists it.
/// </summary>
/// <remarks>
/// <para>
/// This is the payload <c>ProtectedCache</c> protects. It carries the selected MSAL home-account
/// identifier rather than a user principal name or a derived hash, because the account is what a
/// refresh has to compare against before committing — and a UPN would be mailbox-adjacent identity
/// stored for no operational purpose.
/// </para>
/// <para>
/// <b><see cref="SchemaVersion"/> is not the same version <c>ProtectedCache</c> writes.</b> That one
/// guards the envelope — magic, header layout, DPAPI framing. This one guards the payload's own
/// shape, which can change without the envelope changing at all. Both discard rather than migrate:
/// this cache is reconstructible by definition, so a mismatch means refetch.
/// </para>
/// </remarks>
public sealed class MailboxSnapshot
{
    /// <summary>Current payload schema. Increment on any shape change; old payloads are discarded.</summary>
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>The payload schema this snapshot was written with.</summary>
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    /// <summary>The tenant of the registration that read this mailbox.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>
    /// The MSAL home-account identifier the token was acquired for.
    /// </summary>
    /// <remarks>
    /// Recorded so a commit can verify the account has not changed while I/O was in flight, and so a
    /// later read can tell whose mailbox this is without holding a UPN. It is opaque to everything
    /// here: nothing parses it, and nothing renders it.
    /// </remarks>
    public required string HomeAccountId { get; init; }

    /// <summary>Total items in the Inbox folder.</summary>
    public required int TotalItemCount { get; init; }

    /// <summary>Unread items in the Inbox folder.</summary>
    public required int UnreadItemCount { get; init; }

    /// <summary>The optional Focused unread count, or <see langword="null"/>.</summary>
    public int? FocusedUnreadCount { get; init; }

    /// <summary>At most <see cref="MailboxLimits.MaxMessages"/> previews, newest first.</summary>
    public required IReadOnlyList<MessagePreview> Messages { get; init; }

    /// <summary>
    /// When the refresh that produced this snapshot succeeded.
    /// </summary>
    /// <remarks>
    /// Wall-clock UTC rather than a tick count, and this is the one place that is correct: the value
    /// outlives the boot session that wrote it, so a monotonic tick would be meaningless to the
    /// process that reads it back. It drives the 24-hour stale-detail suppression and the "last
    /// updated" line, neither of which is a coordination decision.
    /// </remarks>
    public required DateTimeOffset RefreshedAtUtc { get; init; }

    /// <summary>Builds a snapshot from one readout plus the identity and time the client cannot know.</summary>
    public static MailboxSnapshot Create(
        MailboxReadout readout,
        Guid tenantId,
        string homeAccountId,
        DateTimeOffset refreshedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(readout);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeAccountId);

        return new MailboxSnapshot
        {
            TenantId = tenantId,
            HomeAccountId = homeAccountId,
            TotalItemCount = readout.TotalItemCount,
            UnreadItemCount = readout.UnreadItemCount,
            FocusedUnreadCount = readout.FocusedUnreadCount,
            Messages = readout.Messages,
            RefreshedAtUtc = refreshedAtUtc,
        };
    }

    /// <summary>Serialises to the bytes <c>ProtectedCache</c> protects.</summary>
    public byte[] Serialize() => JsonSerializer.SerializeToUtf8Bytes(this, SerializerOptions);

    /// <summary>
    /// Reads a payload back, or answers <see langword="null"/> when it cannot be trusted.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Every failure is null rather than an exception</b>, and an unrecognised schema version is a
    /// failure like any other. The caller's response is identical in all cases — discard and refetch —
    /// so distinguishing them would only invite a caller to handle one and forget another.
    /// </para>
    /// <para>
    /// <b><c>required</c> does not mean non-null, and assuming it did was a real defect here.</b> The
    /// modifier enforces that a property is <em>set</em>, not that it is set to something; a payload
    /// containing <c>"messages": null</c> deserialises with a null collection and every declared
    /// nullable-reference annotation intact, because <see cref="JsonSerializer"/> does not enforce
    /// them. Counting that collection threw <see cref="NullReferenceException"/>, which no filter
    /// below catches — so a malformed cache crashed the load instead of being discarded, which is the
    /// one thing this method promises never to do. The explicit checks are the enforcement.
    /// </para>
    /// </remarks>
    public static MailboxSnapshot? TryDeserialize(ReadOnlySpan<byte> payload)
    {
        try
        {
            MailboxSnapshot? snapshot =
                JsonSerializer.Deserialize<MailboxSnapshot>(payload, SerializerOptions);

            return snapshot is not null && IsUsable(snapshot) ? snapshot : null;
        }
        catch (Exception e) when (e is JsonException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a deserialised payload can be trusted enough to render.
    /// </summary>
    /// <remarks>
    /// The element check matters as much as the collection one: <c>"messages": [null]</c> is just as
    /// valid to the serialiser, and a null entry would survive to the render path and throw there
    /// instead — further from the cause and inside the delivery worker rather than the load.
    /// </remarks>
    private static bool IsUsable(MailboxSnapshot snapshot)
    {
        if (snapshot.SchemaVersion != CurrentSchemaVersion
            || string.IsNullOrWhiteSpace(snapshot.HomeAccountId)
            || snapshot.Messages is null
            || snapshot.Messages.Count > MailboxLimits.MaxMessages)
        {
            return false;
        }

        foreach (MessagePreview? preview in snapshot.Messages)
        {
            if (preview is null || preview.DisplaySender is null || preview.Subject is null)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Counts only, never content. See <see cref="MessagePreview"/>'s remarks.</summary>
    /// <remarks>
    /// The null-conditional is not defensive noise: <c>required</c> enforces that a property is set,
    /// not that it is set to something, so a payload carrying <c>"messages": null</c> deserialises with
    /// a null collection — the same trap <see cref="TryDeserialize"/> documents at length and then
    /// guards. <see cref="ToString"/> is reachable on an instance that never went through that guard,
    /// and a diagnostic that throws while describing bad state is the least useful moment to throw.
    /// </remarks>
    public override string ToString() =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{nameof(MailboxSnapshot)} ({Messages?.Count ?? 0} messages, {UnreadItemCount} unread)");
}
