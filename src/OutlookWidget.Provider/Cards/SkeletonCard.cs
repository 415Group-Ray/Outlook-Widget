using System.Text.Json;
using Microsoft.Windows.Widgets;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Provider.Cards;

/// <summary>
/// The Phase 0 placeholder card: one Adaptive Card 1.5 template whose data describes the
/// coordination state the delivery pass actually read.
/// </summary>
/// <remarks>
/// <para>
/// <b>This renders coordination state, not mail, and that is the point.</b> No snapshot model,
/// Graph client, or authentication exists yet, so there is nothing truthful to show about an
/// inbox. What it does show is every branch the real card will have to handle — absent, cleared,
/// corrupt, unreadable, details-hidden, signed-out — driven by the same
/// <see cref="DeliveryState"/> the production sink will consume. That makes the native gates
/// observable now: a card that changes when the companion commits is proof of cross-process
/// invalidation reaching a real Widgets host, which no unit test can establish.
/// </para>
/// <para>
/// <b>Nothing here can disclose mailbox content, because it is never given any.</b> The only
/// value derived from the payload is its length, and when the effective disclosure mode is
/// signed-out the worker withholds the payload entirely before this code runs. Phase 2 replaces
/// this with the real small/medium/large templates.
/// </para>
/// <para>
/// Template and data are separate strings rather than one pre-substituted document because
/// <c>WidgetUpdateRequestOptions</c> takes them separately and the host does the binding. Keeping
/// that split now means the size-conditional rendering below is exercised by the host's own
/// templating engine rather than by string concatenation that would have to be thrown away.
/// </para>
/// </remarks>
internal static class SkeletonCard
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
              "$when": "${$host.widgetSize != \"small\"}"
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
    /// Builds the data document for one instance from the state this delivery pass read.
    /// </summary>
    /// <summary>
    /// Whether this build found usable Entra registration identifiers, read once at startup.
    /// </summary>
    /// <remarks>
    /// Shown in the large-size diagnostic as a status word only — never the tenant or client ID.
    /// Neither is a secret, but a widget card is a surface anyone walking past a screen can read,
    /// and a status is all that is needed to tell "the package shipped without configuration" apart
    /// from "authentication has not been built yet". It is set by the composition root rather than
    /// read here, so the card does no I/O on the delivery path.
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
        (string headline, string detail) = Describe(state);

        bool disclosureReduced = state.Mode != DisclosureMode.Full;

        // The companion is the only way out of a suppressed or unusable state, so it is offered
        // whenever the card cannot show real content.
        bool needsCompanion = disclosureReduced || state.ReadStatus != CacheReadStatus.Success;

        // Mail actions are withheld whenever disclosure is reduced: offering Refresh and Open
        // Outlook on a signed-out card would invite an action whose only possible outcome is
        // failure.
        //
        // They are ALSO withheld at the small size when the companion action is showing, and that
        // is a measured fix rather than a preference. Three buttons at the small size produced two
        // rows, and the small widget frame is only tall enough for one: the third button was
        // rendered clipped in half at the bottom edge of the card. The host does not scroll a
        // widget or report that content overflowed, so the only way to avoid a visibly broken card
        // is to not exceed one row. Size is available here because the data document is built per
        // instance, which is why this is decided in C# rather than with a compound $when.
        bool oneRowOnly = instance.Size == WidgetSize.Small;

        (string diagnosticInstance, string diagnosticState, string diagnosticWidgetId) =
            Diagnostic(instance, state);

        // Serialized rather than interpolated. Every string below is provider-authored today, but
        // a hand-built JSON literal is the kind of construction that silently becomes an injection
        // point the first time a value comes from elsewhere.
        return JsonSerializer.Serialize(new SkeletonCardData
        {
            Headline = headline,
            Detail = detail,
            DiagnosticInstance = diagnosticInstance,
            DiagnosticState = diagnosticState,
            DiagnosticWidgetId = diagnosticWidgetId,

            ShowMailActions = !disclosureReduced && !(oneRowOnly && needsCompanion),
            ShowCompanionAction = needsCompanion,
        }, DataOptions);
    }

    private static (string Headline, string Detail) Describe(DeliveryState state)
    {
        // Disclosure mode is checked before the read status, deliberately. A present tombstone is
        // authoritative regardless of what the cache holds, so a suppressed state must never fall
        // through to a branch that describes snapshot contents.
        if (state.Mode == DisclosureMode.SignedOut)
        {
            return ("Signed out", "Open the companion to sign in.");
        }

        if (state.Mode == DisclosureMode.CountsOnly)
        {
            return ("Details hidden", "Message details are hidden by a privacy setting or an "
                                     + "in-progress account change.");
        }

        return state.ReadStatus switch
        {
            CacheReadStatus.Success =>
                ("Coordination is live",
                    "Cached state was read and delivered by the provider. No mailbox data exists "
                    + "yet: Graph access arrives with the next core slice. " + DescribeSilentAuth()),

            CacheReadStatus.Absent =>
                ("No cached state yet",
                    "The provider is running and rendering. Nothing has been committed to the "
                    + "cache yet. " + DescribeSilentAuth()),

            CacheReadStatus.Cleared =>
                ("Cache cleared",
                    "State was explicitly cleared by a logout, account switch, or cache clear."),

            CacheReadStatus.UnsupportedVersion =>
                ("Cache discarded",
                    "The cached state was written in a format this build does not read. It will "
                    + "be refetched; there is no migration path."),

            CacheReadStatus.Corrupt =>
                ("Cache unreadable",
                    "The cached state is corrupt or failed to unprotect. Refresh to rebuild it."),

            CacheReadStatus.Unreadable =>
                ("Cache temporarily unavailable",
                    "The cached state could not be opened. This is usually transient."),

            _ => ("Unknown state", "The provider read a cache status it does not recognise."),
        };
    }

    /// <summary>
    /// One sentence describing what the provider's silent acquisition did, for the medium and large
    /// detail line.
    /// </summary>
    /// <remarks>
    /// Phrased so that each outcome states its own remedy, because these are the same distinctions the
    /// real signed-out, sign-in-required, and broker-unavailable cards must draw in Phase 2 and this is
    /// the first place the wording gets tried against a real host.
    /// </remarks>
    private static string DescribeSilentAuth() =>
        SilentAuthStatus switch
        {
            null => "Silent token acquisition has not finished yet.",

            TokenAcquisitionStatus.Acquired =>
                "The provider acquired a token silently with no window of its own, so gate 9 passes.",

            TokenAcquisitionStatus.InteractionRequired =>
                "Sign-in required: open the companion and sign in.",

            TokenAcquisitionStatus.ApprovalRequired =>
                "An administrator must approve mailbox access for this app.",

            TokenAcquisitionStatus.BrokerUnavailable =>
                "The Windows authentication broker is unavailable, so signing in will not help.",

            TokenAcquisitionStatus.NoConfiguration =>
                "This build shipped without a usable Entra registration.",

            _ => "Silent token acquisition failed; this is usually transient.",
        };

    /// <summary>
    /// The large-size diagnostic, as three separate lines.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returned as three strings bound to three <c>TextBlock</c>s rather than one string containing
    /// <c>\n</c>. The single-string version was measured on the Widgets Board and the newlines were
    /// dropped: the three lines ran together into one wrapped paragraph reading
    /// "… · active generation 0 · mode Full · read Absent · payload none widget d7c0709a…", where
    /// "active" and "generation" appear to be one field. An Adaptive Card <c>TextBlock</c> gives no
    /// guarantee that a lone newline survives, and separate blocks with explicit spacing need no
    /// guarantee.
    /// </para>
    /// <para>
    /// Large size only, and gate-oriented: instance identity, size, active state, generation, and
    /// the byte count are what distinguish one instance's state from another's. The widget ID is a
    /// host handle, not mailbox or identity metadata.
    /// </para>
    /// </remarks>
    private static (string Instance, string State, string WidgetId) Diagnostic(
        WidgetInstance instance,
        DeliveryState state)
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
            ? g.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : "none";

        return (
            $"{instance.DefinitionId} · {instance.Size} · "
                + (instance.IsActive ? "active" : "inactive"),
            $"generation {state.Generation} · delivered {delivered} · mode {state.Mode} · "
                + $"read {state.ReadStatus} · payload {payload}",
            $"config {ConfigurationStatus} · silent auth "
                + $"{(SilentAuthStatus?.ToString() ?? "pending")} · widget {instance.Id}");
    }

    /// <summary>
    /// The data contract, so the property names the template binds to are declared once.
    /// </summary>
    private sealed class SkeletonCardData
    {
        public string Headline { get; init; } = string.Empty;

        public string Detail { get; init; } = string.Empty;

        public string DiagnosticInstance { get; init; } = string.Empty;

        public string DiagnosticState { get; init; } = string.Empty;

        public string DiagnosticWidgetId { get; init; } = string.Empty;

        public bool ShowMailActions { get; init; }

        public bool ShowCompanionAction { get; init; }
    }
}
