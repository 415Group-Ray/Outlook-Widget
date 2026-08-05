using System.Globalization;
using System.Text.Json;
using Microsoft.Windows.Widgets;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Provider.Cards;

/// <summary>
/// The inbox card: one Adaptive Card 1.5 template rendering the committed snapshot, or the
/// coordination state when there is no snapshot to show.
/// </summary>
/// <remarks>
/// <para>
/// <b>This replaces the Phase 0 placeholder, and it kept that card's every branch.</b> The
/// placeholder rendered coordination state because no snapshot model, Graph client, or
/// authentication existed. All three exist now, so the success branch renders mail — but absent,
/// cleared, corrupt, unreadable, details-hidden, and signed-out are unchanged, because those states
/// did not stop being reachable when mail arrived. A card that changes when the companion commits
/// remains the only observable proof that cross-process invalidation reaches a real Widgets host.
/// </para>
/// <para>
/// <b>Message details are withheld after <see cref="CoordinationBounds.StaleDetailSuppression"/>.</b>
/// Section 8's error table requires that 24 hours without a successful refresh shows a
/// stale/reconnect state rather than presenting old subjects as current, and that rule had no
/// implementation before this card — the constant existed and nothing consulted it, because nothing
/// rendered a subject. It is enforced here rather than in <c>DeliveryWorker</c> for the same reason
/// the worker cannot enforce it: the worker never deserializes the payload, so the age of the
/// snapshot is not a fact available to it. The counts survive the cutoff; only the per-message
/// details are dropped, because a stale unread count is a staleness problem and a stale subject
/// presented as current is a disclosure one.
/// </para>
/// <para>
/// <b>Suppression removes message rows and nothing else.</b> Both reduced states — the 24-hour bound
/// and <c>CountsOnly</c> — keep the unread count, the Focused count where present, and the last-update
/// time; each says in its own words why the details are missing. The first version of this card
/// replaced the count with a status word in both cases, which turned "hide the details" into "hide
/// the mailbox" and, in counts-only, removed the one thing that mode is named for. The rule to hold
/// is that a count is not a message.
/// </para>
/// <para>
/// <b>Nothing outside the approved field set reaches this card.</b> Sender, subject, received time
/// and read state are rendered; the cached link is deliberately never read here, and no message
/// identifier or link appears in card JSON, which is what section 17 requires of the eventual
/// per-message action. When the effective disclosure mode is signed-out the worker withholds the
/// payload entirely before this code runs, so the signed-out card cannot leak what it was never
/// given.
/// </para>
/// <para>
/// <b>Mailbox text is rendered through <c>RichTextBlock</c>/<c>TextRun</c>, never <c>TextBlock</c>,
/// and that is a security boundary rather than a styling choice.</b> A <c>TextBlock</c>'s
/// <c>text</c> renders a Markdown subset that includes hyperlinks, so a subject or display name of
/// the form <c>[pay now](https://attacker.example)</c> — chosen by anyone who can send mail to this
/// mailbox — would render as a clickable, sender-controlled link on the card. That defeats the
/// point of section 3's <c>Action.OpenUrl</c> ban and the Outlook-host allowlist: the provider is
/// supposed to be the only thing that decides what may be opened. <c>TextRun</c> is documented as
/// not supporting Markdown, which is why the sender and subject go through it and why the received
/// time, headline, and detail — all provider-authored — may stay <c>TextBlock</c>s.
/// </para>
/// <para>
/// The cost is real and is paid deliberately: <c>RichTextBlock</c> has neither <c>maxLines</c> nor
/// <c>wrap</c>, so single-line truncation moves into C# as <see cref="DisplayLineBudget"/>.
/// </para>
/// <para>
/// Template and data are separate strings rather than one pre-substituted document because
/// <c>WidgetUpdateRequestOptions</c> takes them separately and the host does the binding.
/// </para>
/// </remarks>
internal static class InboxCard
{
    /// <summary>
    /// camelCase, because the template binds <c>${headline}</c> rather than <c>${Headline}</c>.
    /// Adaptive Card data binding is case-sensitive, so a default-cased document produces a card
    /// whose every bound field renders empty — a failure that looks like a layout problem rather
    /// than a naming one.
    /// </summary>
    private static readonly JsonSerializerOptions DataOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>
    /// How many message rows each widget size shows.
    /// </summary>
    /// <remarks>
    /// Decided in C# rather than with a <c>$when</c> on <c>$index</c>, because the data document is
    /// already built per instance and a row the size cannot fit should not be in the payload at all.
    /// The small card shows none: its frame fits a headline and one row of actions, which the
    /// measured three-button clipping below established the hard way.
    /// </remarks>
    private static int RowsFor(WidgetSize size) => size switch
    {
        // Four rather than the cached five, measured on 0.4.24.0: five rows plus the three
        // diagnostic lines plus the action row overflowed the large frame and the host rendered the
        // buttons clipped at the bottom edge. The host neither scrolls a widget nor reports that
        // content overflowed, so the only defence is to not exceed the frame. When the diagnostic
        // block moves to the log file this can be revisited against a measurement, not a guess.
        WidgetSize.Large => 4,
        WidgetSize.Medium => 3,
        _ => 0,
    };

    /// <summary>
    /// The template. Constant: size differences are expressed with <c>$when</c> against
    /// <c>$host.widgetSize</c> rather than by returning a different document per size, so one
    /// template serves small, medium, and large and there is no per-size drift.
    /// </summary>
    public static string Template { get; } =
        """
        {
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "type": "AdaptiveCard",
          "version": "1.5",
          "body": [
            {
              "type": "TextBlock",
              "text": "${headline}",
              "size": "Medium",
              "weight": "Bolder",
              "wrap": true
            },
            {
              "type": "TextBlock",
              "text": "${detail}",
              "wrap": true,
              "isSubtle": true,
              "spacing": "None",
              "$when": "${$host.widgetSize != \"small\"}"
            },
            {
              "type": "Container",
              "spacing": "Medium",
              "$when": "${showMessages}",
              "items": [
                {
                  "type": "ColumnSet",
                  "$data": "${messages}",
                  "spacing": "Small",
                  "columns": [
                    {
                      "type": "Column",
                      "width": "stretch",
                      "items": [
                        {
                          "type": "RichTextBlock",
                          "$when": "${isUnread}",
                          "inlines": [
                            {
                              "type": "TextRun",
                              "text": "${displaySender}",
                              "weight": "Bolder"
                            }
                          ]
                        },
                        {
                          "type": "RichTextBlock",
                          "$when": "${isRead}",
                          "inlines": [
                            {
                              "type": "TextRun",
                              "text": "${displaySender}",
                              "weight": "Lighter"
                            }
                          ]
                        },
                        {
                          "type": "RichTextBlock",
                          "spacing": "None",
                          "inlines": [
                            {
                              "type": "TextRun",
                              "text": "${subject}",
                              "weight": "Lighter",
                              "isSubtle": true
                            }
                          ]
                        }
                      ]
                    },
                    {
                      "type": "Column",
                      "width": "auto",
                      "items": [
                        {
                          "type": "TextBlock",
                          "text": "${receivedLabel}",
                          "isSubtle": true,
                          "size": "Small",
                          "horizontalAlignment": "Right",
                          "wrap": false
                        }
                      ]
                    }
                  ]
                }
              ]
            },
            {
              "type": "TextBlock",
              "text": "${diagnosticInstance}",
              "wrap": true,
              "isSubtle": true,
              "size": "Small",
              "fontType": "Monospace",
              "spacing": "Medium",
              "$when": "${$host.widgetSize == \"large\"}"
            },
            {
              "type": "TextBlock",
              "text": "${diagnosticState}",
              "wrap": true,
              "isSubtle": true,
              "size": "Small",
              "fontType": "Monospace",
              "spacing": "None",
              "$when": "${$host.widgetSize == \"large\"}"
            },
            {
              "type": "TextBlock",
              "text": "${diagnosticWidgetId}",
              "wrap": true,
              "isSubtle": true,
              "size": "Small",
              "fontType": "Monospace",
              "spacing": "None",
              "$when": "${$host.widgetSize == \"large\"}"
            },
            {
              "type": "ActionSet",
              "$when": "${showMailActions}",
              "actions": [
                {
                  "type": "Action.Execute",
                  "title": "Open Outlook",
                  "verb": "openOutlook"
                },
                {
                  "type": "Action.Execute",
                  "title": "Refresh",
                  "verb": "refresh"
                }
              ]
            },
            {
              "type": "ActionSet",
              "$when": "${showCompanionAction}",
              "actions": [
                {
                  "type": "Action.Execute",
                  "title": "Open companion",
                  "verb": "openCompanion"
                }
              ]
            }
          ]
        }
        """;

    /// <summary>
    /// Whether this build found usable Entra registration identifiers, read once at startup.
    /// </summary>
    /// <remarks>
    /// Shown in the large-size diagnostic as a status word only — never the tenant or client ID.
    /// Neither is a secret, but a widget card is a surface anyone walking past a screen can read,
    /// and a status is all that is needed to tell "the package shipped without configuration" apart
    /// from an authentication failure. It is set by the composition root rather than read here, so
    /// the card does no I/O on the delivery path.
    /// </remarks>
    public static AuthenticationConfigurationStatus ConfigurationStatus { get; set; } =
        AuthenticationConfigurationStatus.Absent;

    /// <summary>
    /// The result of this process's silent token acquisition, or <see langword="null"/> before the
    /// attempt has finished.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the gate 9 readout.</b> Gate 9 asks whether the provider can acquire a token silently
    /// with a zero parent window handle, and there is no way to observe that from outside the process:
    /// the provider is a background COM server with no console and no window, and the operational log
    /// records categories rather than a running state. Putting the classified status on the card makes
    /// the gate answerable by pinning the widget and reading it.
    /// </para>
    /// <para>
    /// A status word only — never a token, an account, an expiry, or an error message. It is set by the
    /// background probe rather than read here, so the delivery path stays free of I/O.
    /// </para>
    /// </remarks>
    public static TokenAcquisitionStatus? SilentAuthStatus { get; set; }

    public static string Data(WidgetInstance instance, DeliveryState state)
    {
        // Deserialized once, here, for every mode that is given a payload at all — which is every
        // mode except signed-out, where the worker withholds it before this code runs.
        //
        // **Counts-only must reach this too, and gating on Full alone was a defect.** The mode
        // exists to show counts while withholding details; a card that answers it with neither is
        // the one thing the name rules out. Suppression is applied below, per decision, rather than
        // by refusing to look at the snapshot: what counts-only and the stale bound withhold is the
        // message rows, and the count is not a message.
        //
        // A payload that will not parse is treated exactly like a corrupt cache rather than as an
        // empty inbox: "no messages" and "the messages could not be read" are different facts and
        // the card must not conflate them into a reassuring blank.
        bool payloadOffered =
            state.Mode != DisclosureMode.SignedOut
            && state.ReadStatus == CacheReadStatus.Success
            && state.Payload is { Length: > 0 };

        MailboxSnapshot? snapshot =
            payloadOffered ? MailboxSnapshot.TryDeserialize(state.Payload!) : null;

        bool payloadUnreadable = payloadOffered && snapshot is null;

        // The 24-hour rule. Age is measured from the refresh that produced the snapshot, not from
        // the commit, because a commit that merely re-wrote unchanged content did not make the
        // content current.
        bool detailsAreStale =
            snapshot is not null
            && DateTimeOffset.UtcNow - snapshot.RefreshedAtUtc >= CoordinationBounds.StaleDetailSuppression;

        // **One situation, decided once.** Every downstream question — the copy, whether rows are
        // rendered, which actions are offered — is a switch over this and nothing else. See
        // Situate for why that matters more than it looks.
        CardSituation situation = Situate(state, snapshot, payloadUnreadable, detailsAreStale);

        (string headline, string detail) = Describe(situation, snapshot);

        // An authentication state the companion can actually address — not merely any non-success.
        //
        // Excluded: null (pending, not yet a problem, resolves within moments of provider start) and
        // Failed, whose remedy is the next refresh rather than opening anything. Including Failed was a
        // defect with a cost beyond a useless button: needsCompanion suppresses mail actions at the small
        // size, so a transient network error removed **Open Outlook** — which needs no token at all — and
        // replaced it with a companion trip that would report nothing actionable.
        //
        // Included, deliberately, even though signing in cannot fix them: BrokerUnavailable,
        // NoConfiguration, and ApprovalRequired. The companion is where diagnostics live, which is what
        // section 3 means by a broker-unavailable card carrying "an action to open the companion", and
        // what the section 8 state table means by "companion diagnostics". The button is labelled Open
        // companion rather than Sign in, so it promises a place to look rather than a fix.
        bool authNeedsAttention = SilentAuthStatus is
            TokenAcquisitionStatus.InteractionRequired
            or TokenAcquisitionStatus.Cancelled
            or TokenAcquisitionStatus.ApprovalRequired
            or TokenAcquisitionStatus.BrokerUnavailable
            or TokenAcquisitionStatus.NoConfiguration;

        // The companion is offered whenever the card cannot show real content, and which situations
        // those are is stated here rather than derived from a mode comparison.
        //
        // The authentication clause is not redundant with the situation, and omitting it was a bug.
        // The expected steady state once mail exists is a healthy mailbox with an expired token: the
        // detail line says "Sign in required: open the companion" while the situation is perfectly
        // ordinary, so the card asked for an action it did not offer.
        bool needsCompanion = HasNoMailboxToShow(situation) || authNeedsAttention;

        // Mail actions are withheld only on the signed-out card, where offering Refresh and Open
        // Outlook would invite an action whose only possible outcome is failure. A counts-only card
        // keeps them: refreshing its counts and opening Outlook both work and are both reasonable
        // things to want while subjects are hidden.
        //
        // They are ALSO withheld at the small size when the companion action is showing, and that
        // is a measured fix rather than a preference. Three buttons at the small size produced two
        // rows, and the small widget frame is only tall enough for one: the third button was
        // rendered clipped in half at the bottom edge of the card. The host does not scroll a
        // widget or report that content overflowed, so the only way to avoid a visibly broken card
        // is to not exceed one row. Size is available here because the data document is built per
        // instance, which is why this is decided in C# rather than with a compound $when.
        bool oneRowOnly = instance.Size == WidgetSize.Small;

        // Exactly one situation renders message rows. Every other reason to withhold them — the
        // privacy setting, the stale bound, a signed-out card, an unreadable cache — is a different
        // member of the enum with its own copy, so no combination of flags can accidentally agree
        // to show them.
        //
        // This is the only place suppression removes anything. The counts continue to render
        // wherever a snapshot exists, which is what distinguishes hiding details from hiding the
        // mailbox.
        int rows = RowsFor(instance.Size);
        MessageRow[] messages =
            situation == CardSituation.Mailbox && snapshot is not null && rows > 0
                ? [.. snapshot.Messages.Take(rows).Select(ToRow)]
                : [];

        (string diagnosticInstance, string diagnosticState, string diagnosticWidgetId) =
            Diagnostic(instance, state, snapshot);

        // Serialized rather than interpolated. Every string below is provider-authored except the
        // sender and subject, which are attacker-influenced content from a mailbox anyone can send
        // to — so a hand-built JSON literal here would be an injection point rather than merely a
        // construction that could become one.
        return JsonSerializer.Serialize(new InboxCardData
        {
            Headline = headline,
            Detail = detail,
            DiagnosticInstance = diagnosticInstance,
            DiagnosticState = diagnosticState,
            DiagnosticWidgetId = diagnosticWidgetId,

            Messages = messages,
            ShowMessages = messages.Length > 0,

            ShowMailActions = situation != CardSituation.SignedOut && !(oneRowOnly && needsCompanion),
            ShowCompanionAction = needsCompanion,
        }, DataOptions);
    }

    /// <summary>
    /// Projects one cached preview onto the fields the template binds.
    /// </summary>
    /// <remarks>
    /// The cached link is not read. Section 17 requires that no link or message identifier appear in
    /// card JSON at all, so the eventual per-message action will carry a slot index and the snapshot
    /// generation instead — and the safest way to hold that line is for the render path never to
    /// touch the link in the first place.
    /// </remarks>
    private static MessageRow ToRow(MessagePreview preview) => new()
    {
        DisplaySender = MailboxText.ForDisplay(preview.DisplaySender, DisplayLineBudget),
        Subject = MailboxText.ForDisplay(preview.Subject, DisplayLineBudget),
        ReceivedLabel = ReceivedLabel(preview.ReceivedAt),
        IsUnread = !preview.IsRead,
        IsRead = preview.IsRead,
    };

    /// <summary>
    /// The display budget for one line of mailbox text in the message column.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Not a duplicate of <see cref="MailboxLimits"/>.</b> Those bound what may be cached at all,
    /// against a hostile response; this bounds what fits on one line, and exists only because
    /// <c>RichTextBlock</c> — which is what keeps mailbox strings out of a Markdown renderer — has
    /// neither <c>maxLines</c> nor <c>wrap</c>, so it always wraps. A wrapped row grows a card the
    /// host clips without reporting, which is the defect already measured on 0.4.24.0. Approximate
    /// by construction: the medium and large frames share a width, and a character count is a proxy
    /// for a proportional-font measurement. Erring short costs an ellipsis; erring long costs a
    /// clipped action row.
    /// </para>
    /// <para>
    /// The number lives here and the shortening lives in <see cref="MailboxText"/>, because the
    /// budget is a Widgets-host detail and the shortening is hostile-input handling that has to be
    /// unit-testable — which nothing in this project can be.
    /// </para>
    /// </remarks>
    private const int DisplayLineBudget = 34;

    /// <summary>
    /// Formats a received time for display, in the reader's current timezone.
    /// </summary>
    /// <remarks>
    /// Formatted at delivery rather than at cache time, because a cached card can outlive the
    /// timezone it was written in. The rule itself is section 7's and lives in
    /// <see cref="MailboxTime"/> so it can be tested against that specification.
    /// </remarks>
    private static string ReceivedLabel(DateTimeOffset receivedAt) =>
        MailboxTime.ReceivedLabel(receivedAt, DateTimeOffset.Now);

    /// <summary>
    /// What this card is actually showing. Every rendering decision switches over this.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This exists because deriving the answer per decision produced the same defect six times.</b>
    /// The card previously asked its own question at each site — <c>Mode != Full</c> for the actions,
    /// <c>Mode == CountsOnly &amp;&amp; snapshot is null</c> for the copy, a conjunction of four flags
    /// for the rows — and each derivation collapsed several distinct states into a boolean whose
    /// default landed on the wrong side. The actions vanished when the privacy setting went on; a
    /// cleared cache was described as a privacy setting; counts disappeared with the details they
    /// were meant to outlive.
    /// </para>
    /// <para>
    /// One enum, decided once, in a documented precedence order, makes those questions unaskable.
    /// A site cannot re-derive what it is not given, and adding a member breaks every switch that
    /// has not considered it — which is the property that turns the next mistake into a build
    /// failure instead of a card nobody looks at.
    /// </para>
    /// </remarks>
    private enum CardSituation
    {
        /// <summary>Suppressed to signed-out, or genuinely signed out. No mailbox, no actions.</summary>
        SignedOut,

        /// <summary>Nothing has ever been committed.</summary>
        CacheAbsent,

        /// <summary>Explicitly cleared by a logout, account switch, or cache clear.</summary>
        CacheCleared,

        /// <summary>Written in a payload schema this build does not read.</summary>
        CacheUnsupported,

        /// <summary>Present and damaged, or it failed to unprotect.</summary>
        CacheCorrupt,

        /// <summary>Could not be opened at all. Usually transient.</summary>
        CacheUnreadable,

        /// <summary>The envelope opened and the payload inside it would not parse.</summary>
        PayloadUnreadable,

        /// <summary>A successful read that carried no payload. Not an error and not mail.</summary>
        NoMailboxContent,

        /// <summary>A mailbox older than the stale bound: counts survive, details do not.</summary>
        MailboxStale,

        /// <summary>A mailbox the user has asked to see counts only for.</summary>
        MailboxCountsOnly,

        /// <summary>A mailbox rendered in full. The only situation that shows message rows.</summary>
        Mailbox,
    }

    /// <summary>
    /// Classifies one delivery into exactly one situation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order below is the precedence and it is load-bearing twice.
    /// </para>
    /// <para>
    /// <b>Signed-out first.</b> A present tombstone is authoritative regardless of what the cache
    /// holds, so a suppressed delivery must never reach a branch that describes snapshot contents.
    /// </para>
    /// <para>
    /// <b>Cache status before the privacy setting.</b> Counts-only is a statement about a mailbox,
    /// and with no snapshot there is no mailbox to make it about. Checking the mode first meant a
    /// user who had the setting on before signing out saw "details are hidden by a privacy setting"
    /// where the honest answer was that the cache had been cleared — replacing recovery guidance
    /// with an explanation of something that was not the problem.
    /// </para>
    /// </remarks>
    private static CardSituation Situate(
        DeliveryState state,
        MailboxSnapshot? snapshot,
        bool payloadUnreadable,
        bool detailsAreStale)
    {
        if (state.Mode == DisclosureMode.SignedOut)
        {
            return CardSituation.SignedOut;
        }

        switch (state.ReadStatus)
        {
            case CacheReadStatus.Absent:
                return CardSituation.CacheAbsent;
            case CacheReadStatus.Cleared:
                return CardSituation.CacheCleared;
            case CacheReadStatus.UnsupportedVersion:
                return CardSituation.CacheUnsupported;
            case CacheReadStatus.Corrupt:
                return CardSituation.CacheCorrupt;
            case CacheReadStatus.Unreadable:
                return CardSituation.CacheUnreadable;
            case CacheReadStatus.Success:
                break;
            default:
                // An unrecognised status is a cache this build cannot reason about, which is the
                // same practical position as one it cannot read.
                return CardSituation.CacheUnreadable;
        }

        if (payloadUnreadable)
        {
            return CardSituation.PayloadUnreadable;
        }

        if (snapshot is null)
        {
            return CardSituation.NoMailboxContent;
        }

        // Staleness outranks the privacy setting, because a stale counts-only card is stale first:
        // the number itself is old, and saying only that details are hidden would present an old
        // count as current.
        if (detailsAreStale)
        {
            return CardSituation.MailboxStale;
        }

        return state.Mode == DisclosureMode.CountsOnly
            ? CardSituation.MailboxCountsOnly
            : CardSituation.Mailbox;
    }

    /// <summary>Whether the situation is one where there is no mailbox to act on.</summary>
    /// <remarks>
    /// <para>
    /// A switch expression with no discard arm, so adding a situation is a compile error here rather
    /// than a silent classification. That is the point of the enum, and it is the whole reason this
    /// method exists instead of a boolean derived at the call site.
    /// </para>
    /// <para>
    /// CS8524 is suppressed rather than answered with a discard. It fires for values cast in from
    /// outside the declared members, which cannot happen: the enum is private and every value comes
    /// from <see cref="Situate"/>. Adding a discard arm to silence it would also silence CS8509 —
    /// the missing-member check that is the point — so the narrower suppression is the one that
    /// keeps the guarantee.
    /// </para>
    /// </remarks>
#pragma warning disable CS8524
    private static bool HasNoMailboxToShow(CardSituation situation) => situation switch
    {
        CardSituation.SignedOut => true,
        CardSituation.CacheAbsent => true,
        CardSituation.CacheCleared => true,
        CardSituation.CacheUnsupported => true,
        CardSituation.CacheCorrupt => true,
        CardSituation.CacheUnreadable => true,
        CardSituation.PayloadUnreadable => true,
        CardSituation.NoMailboxContent => true,

        // The three mailbox situations. Counts-only and stale are deliberately not here: the user
        // chose one and the other resolves itself, and neither needs a trip to the companion —
        // while including them also cost the mail actions at the small size, where one row fits.
        CardSituation.MailboxStale => false,
        CardSituation.MailboxCountsOnly => false,
        CardSituation.Mailbox => false,
    };

    /// <summary>
    /// The headline and subtitle for one situation.
    /// </summary>
    /// <remarks>
    /// Each branch appends its own authentication sentence where one would otherwise mislead.
    /// <c>DescribeAuthBlocker</c> returns a status word turned into a sentence and never touches the
    /// snapshot, so a suppressed card gains no mailbox metadata from it. Withholding it would be the
    /// actual harm: telling someone to sign in when signing in cannot succeed is the conflation
    /// section 8 forbids.
    /// </remarks>
    private static (string Headline, string Detail) Describe(
        CardSituation situation,
        MailboxSnapshot? snapshot) => situation switch
    {
        CardSituation.SignedOut =>
            ("Signed out", "Open the companion to sign in." + DescribeAuthBlocker()),

        CardSituation.CacheAbsent =>
            ("No cached state yet",
                "The provider is running and rendering. Nothing has been committed to the cache "
                + "yet. " + DescribeSilentAuth()),

        CardSituation.CacheCleared =>
            ("Cache cleared",
                "State was explicitly cleared by a logout, account switch, or cache clear."
                + DescribeAuthBlocker()),

        CardSituation.CacheUnsupported =>
            ("Cache discarded",
                "The cached state was written in a format this build does not read. It will be "
                + "refetched; there is no migration path."),

        CardSituation.CacheCorrupt =>
            ("Cache unreadable",
                "The cached state is corrupt or failed to unprotect. Refresh to rebuild it."),

        CardSituation.CacheUnreadable =>
            ("Cache temporarily unavailable",
                "The cached state could not be opened. This is usually transient."),

        // Distinct from a corrupt cache: the envelope unprotected cleanly and the payload inside it
        // did not parse, which means a schema this build does not recognise rather than damage.
        // Both recover by refetching, and saying which happened saves a support round trip.
        CardSituation.PayloadUnreadable =>
            ("Inbox unavailable",
                "The cached mailbox could not be read in this build's format. It will be refetched."),

        CardSituation.NoMailboxContent =>
            ("Coordination is live",
                "Cached state was read and delivered by the provider, but it carried no mailbox "
                + "content. " + DescribeSilentAuth()),

        CardSituation.MailboxStale =>
            (Headline(snapshot),
                ComposeSubtitle(snapshot!, DetailSuppression.StaleBound) + DescribeAuthBlocker()),

        CardSituation.MailboxCountsOnly =>
            (Headline(snapshot),
                ComposeSubtitle(snapshot!, DetailSuppression.PrivacyMode) + DescribeAuthBlocker()),

        CardSituation.Mailbox =>
            (Headline(snapshot),
                ComposeSubtitle(snapshot!, DetailSuppression.None) + DescribeAuthBlocker()),
    };
#pragma warning restore CS8524

    /// <summary>
    /// The unread count as a headline.
    /// </summary>
    /// <remarks>
    /// <b>The count, in all three mailbox situations, and that is the point.</b> Counts-only and the
    /// stale bound withhold message details; neither withholds the mailbox. Replacing the count with
    /// a status word — which both branches originally did — turned "hide the details" into "hide
    /// everything", and in the counts-only case removed the one thing that mode is named for.
    /// <para>
    /// It carries the number itself rather than a word plus a separate badge. Measured on 0.4.24.0:
    /// an "Unread" headline with a right-aligned count and a subtitle reading "1 unread message, 1
    /// in Focused" stated the same fact three times and spent a line doing it, on a surface where
    /// vertical space is the binding constraint.
    /// </para>
    /// </remarks>
    private static string Headline(MailboxSnapshot? snapshot) => snapshot?.UnreadItemCount switch
    {
        null => "Inbox",
        0 => "Inbox up to date",
        1 => "1 unread",
        int n => $"{n.ToString(CultureInfo.CurrentCulture)} unread",
    };

    /// <summary>
    /// The subtitle for a healthy mailbox: what is unread, and when it was last known good.
    /// </summary>
    /// <remarks>
    /// The Focused count is stated only when the optional query produced one. Section 8 makes that
    /// query's failure non-fatal, so the absence of a Focused number means "not asked, or not
    /// answered" rather than zero, and rendering a zero there would be a fabricated fact.
    /// </remarks>
    /// <summary>
    /// When the snapshot was last refreshed, in the reader's timezone.
    /// </summary>
    /// <remarks>
    /// A time of day alone for a snapshot from today, and the date as well once it is older, because
    /// "Updated 4:58 PM" on a three-day-old card is actively misleading — and a card that has passed
    /// the 24-hour bound is by definition not from today. The formats are the locale's, chosen in
    /// <see cref="MailboxTime"/> rather than hand-built here.
    /// </remarks>
    private static string DescribeUpdated(MailboxSnapshot snapshot) =>
        MailboxTime.UpdatedLabel(snapshot.RefreshedAtUtc, DateTimeOffset.Now);

    /// <summary>Why the message rows are missing, when they are.</summary>
    private enum DetailSuppression
    {
        /// <summary>Nothing is suppressed; the rows are rendered.</summary>
        None,

        /// <summary>A privacy setting or an in-progress account change.</summary>
        PrivacyMode,

        /// <summary>No successful refresh inside <see cref="CoordinationBounds.StaleDetailSuppression"/>.</summary>
        StaleBound,
    }

    /// <summary>
    /// Builds the subtitle from every fact the disclosure decision permits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>One composer, rather than a sentence written per state, and that is the fix for a defect
    /// found three times on one review.</b> Each reduced state used to author its own prose, so each
    /// independently decided which facts to mention — and each forgot a different one. Counts-only
    /// dropped the unread count, the stale bound dropped it too, and when both were repaired they
    /// dropped the Focused count. Every one of those was the same mistake: a state that hides
    /// message details deciding, by omission, to hide numbers as well.
    /// </para>
    /// <para>
    /// Here suppression chooses only a <see cref="DetailSuppression"/>. What may be said is decided
    /// once, in one place, for every state — so a state added later cannot silently drop a fact. It
    /// can only pick a reason.
    /// </para>
    /// <para>
    /// The unread count is deliberately absent: the headline already carries it, and repeating it
    /// here spent a line restating the largest text on the card.
    /// </para>
    /// </remarks>
    private static string ComposeSubtitle(MailboxSnapshot snapshot, DetailSuppression suppression)
    {
        var facts = new List<string>(3);

        string focused = DescribeFocused(snapshot);
        if (focused.Length > 0)
        {
            facts.Add(focused);
        }

        facts.Add(suppression == DetailSuppression.StaleBound
            ? $"Counts last updated {DescribeUpdated(snapshot)}"
            : $"Updated {DescribeUpdated(snapshot)}");

        switch (suppression)
        {
            case DetailSuppression.PrivacyMode:
                facts.Add("Message details are hidden by a privacy setting or an in-progress "
                          + "account change");
                break;

            case DetailSuppression.StaleBound:
                facts.Add("Message details are hidden because there has been no successful refresh "
                          + "for over 24 hours. Refresh to reconnect");
                break;
        }

        return string.Join(". ", facts) + ".";
    }

    /// <summary>
    /// The Focused clause, or empty when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// A bare clause with no punctuation: <see cref="ComposeSubtitle"/> owns how facts are joined.
    /// Empty when the optional query produced no count — section 8 makes that query's failure
    /// non-fatal, so its absence means "not asked, or not answered" rather than zero, and printing a
    /// zero there would be a fabricated fact. Empty too when nothing is unread, because "0 in
    /// Focused" alongside an "Inbox up to date" headline is noise.
    /// </remarks>
    private static string DescribeFocused(MailboxSnapshot snapshot) =>
        snapshot is { UnreadItemCount: > 0, FocusedUnreadCount: int focused }
            ? $"{focused.ToString(CultureInfo.CurrentCulture)} in Focused"
            : string.Empty;

    /// <summary>
    /// A trailing sentence for cards whose own copy would otherwise mislead about authentication, or
    /// empty when there is nothing to add.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Only the states that make the surrounding advice <em>wrong</em> produce a sentence. A signed-out
    /// or cleared card already says to open the companion and sign in, which is correct guidance when the
    /// outcome is interaction-required and misleading when an administrator has to approve, the broker is
    /// unavailable, or the build shipped without a registration.
    /// </para>
    /// <para>
    /// Silence for every other status is deliberate. Appending "acquired a token" to a signed-out card,
    /// or the pending sentence to every card in the moments after provider start, would be noise on the
    /// two most frequently seen cards.
    /// </para>
    /// <para>
    /// Leading space, because the caller concatenates it onto a completed sentence.
    /// </para>
    /// </remarks>
    private static string DescribeAuthBlocker() =>
        SilentAuthStatus switch
        {
            TokenAcquisitionStatus.ApprovalRequired =>
                " Signing in will not be enough: an administrator must approve mailbox access for this "
                + "app.",

            TokenAcquisitionStatus.BrokerUnavailable =>
                " Signing in will not work yet: the Windows authentication broker is unavailable.",

            TokenAcquisitionStatus.NoConfiguration =>
                " Signing in will not work: this build shipped without a usable Entra registration.",

            TokenAcquisitionStatus.InteractionRequired =>
                " Sign in required: open the companion.",

            _ => string.Empty,
        };

    /// <summary>
    /// The authentication sentence for cards that have no mail to describe, where the token state is
    /// the most useful thing the card can say.
    /// </summary>
    private static string DescribeSilentAuth() =>
        SilentAuthStatus switch
        {
            null => "Silent authentication has not finished yet.",
            TokenAcquisitionStatus.Acquired => "The provider acquired a token silently.",
            _ => "Silent authentication reported " + SilentAuthStatus.Value + ".",
        };

    /// <summary>
    /// The large-size diagnostic block: what this pass read, and what the host already holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Retained from the Phase 0 card rather than removed with it. There is no log file — production
    /// logging is metadata-free and in-memory — so these three lines are the only way to observe the
    /// provider's committed generation, what the host was last given, and the silent-token state on a
    /// running machine. Both installed-package gate readings in the evidence report were taken from
    /// here.
    /// </para>
    /// <para>
    /// Large only, so the sizes a user actually pins stay clean. Nothing here names a sender, a
    /// subject, an account, or a tenant: the mailbox appears as a message count and nothing more.
    /// </para>
    /// </remarks>
    private static (string Instance, string State, string WidgetId) Diagnostic(
        WidgetInstance instance,
        DeliveryState state,
        MailboxSnapshot? snapshot)
    {
        string payload = state.Payload is null
            ? "none"
            : $"{state.Payload.Length} bytes";

        // The recovered generation is shown alongside the current one, because the pair is what
        // makes CustomState recovery observable at all. After a reboot the provider rebuilds this
        // from GetWidgetInfos(), so "delivered" reflects what the host was holding before the
        // restart while "generation" is what is committed now. "none" means nothing has been
        // delivered to this instance yet, or the host returned a value that would not parse.
        string delivered = instance.DeliveredGeneration is long g
            ? g.ToString(CultureInfo.InvariantCulture)
            : "none";

        string messages = snapshot is null
            ? "none"
            : snapshot.Messages.Count.ToString(CultureInfo.InvariantCulture);

        return (
            $"{instance.DefinitionId} · {instance.Size} · "
                + (instance.IsActive ? "active" : "inactive"),
            $"generation {state.Generation} · delivered {delivered} · mode {state.Mode} · "
                + $"read {state.ReadStatus} · payload {payload} · cached {messages}",
            $"config {ConfigurationStatus} · silent auth "
                + $"{(SilentAuthStatus?.ToString() ?? "pending")} · widget {instance.Id}");
    }

    /// <summary>
    /// The data contract, so the property names the template binds to are declared once.
    /// </summary>
    private sealed class InboxCardData
    {
        public string Headline { get; init; } = string.Empty;

        public string Detail { get; init; } = string.Empty;

        public IReadOnlyList<MessageRow> Messages { get; init; } = [];

        public bool ShowMessages { get; init; }

        public string DiagnosticInstance { get; init; } = string.Empty;

        public string DiagnosticState { get; init; } = string.Empty;

        public string DiagnosticWidgetId { get; init; } = string.Empty;

        public bool ShowMailActions { get; init; }

        public bool ShowCompanionAction { get; init; }
    }

    /// <summary>
    /// One rendered message row. The approved display fields only.
    /// </summary>
    private sealed class MessageRow
    {
        public string DisplaySender { get; init; } = string.Empty;

        public string Subject { get; init; } = string.Empty;

        public string ReceivedLabel { get; init; } = string.Empty;

        /// <summary>Whether the message is unread. Selects the bolder sender line.</summary>
        /// <remarks>
        /// <para>
        /// Read state is rendered as font weight rather than as a glyph or a colour. A glyph costs a
        /// column on a card whose width is the scarce dimension, and colour alone would carry the
        /// distinction in a way a reader who cannot perceive it would lose entirely. The two weights
        /// are Microsoft's own widget type ramp — Body is <c>Default, Lighter</c> and Body Strong is
        /// <c>Default, Bolder</c> — rather than a pair chosen here, which is also why the read row
        /// is Lighter and not the Adaptive Card default weight.
        /// </para>
        /// <para>
        /// <b>Two booleans selecting two whole TextBlocks, rather than one string bound into the
        /// <c>weight</c> property.</b> Binding a value into a non-text property depends on the host
        /// substituting before it parses the card, and on 0.4.24.0 every sender rendered at the same
        /// weight with exactly one message unread — so whatever the host did with
        /// <c>"weight": "${senderWeight}"</c>, it was not the intended thing. <c>$when</c> on a
        /// boolean is the mechanism the rest of this template already relies on and the host is
        /// demonstrably applying. The redundant <see cref="IsRead"/> is carried so the template needs
        /// no negation operator, whose support here is equally unverified.
        /// </para>
        /// </remarks>
        public bool IsUnread { get; init; }

        /// <summary>The complement of <see cref="IsUnread"/>. See its remarks for why both exist.</summary>
        public bool IsRead { get; init; }
    }
}
