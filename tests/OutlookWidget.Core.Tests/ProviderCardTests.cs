using System.Text.Json;
using System.Text.RegularExpressions;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Checks on the provider's Adaptive Card template, read from source.
/// </summary>
/// <remarks>
/// <para>
/// A widget card fails quietly. Malformed template JSON, a bound field the data document does not
/// contain, or an action verb the provider does not handle all produce a widget that renders —
/// blank, or with empty text, or with a button that does nothing — rather than an error. The
/// Widgets host surfaces no diagnostic, so without these checks the first sign of any of them is
/// looking at a pinned widget and wondering.
/// </para>
/// <para>
/// The template is read out of the source file rather than referenced, because the test project
/// cannot reference the provider without adopting its Windows-SDK-versioned target framework. See
/// the test project for that trade.
/// </para>
/// </remarks>
public sealed class ProviderCardTests
{
    private static string CardSourcePath =>
        Path.Combine(RepositorySources.ProviderSourceDirectory, "Cards", "InboxCard.cs");

    private static string CardSource() => File.ReadAllText(CardSourcePath);

    /// <summary>
    /// Extracts the raw string literal assigned to <c>Template</c>.
    /// </summary>
    private static string TemplateJson()
    {
        string source = CardSource();

        Match match = Regex.Match(
            source,
            @"Template\s*\{\s*get;\s*\}\s*=\s*""""""(?<json>.*?)""""""",
            RegexOptions.Singleline);

        Assert.True(
            match.Success,
            $"Could not find the Template raw string literal in {CardSourcePath}. If the "
                + "declaration changed shape, this pattern must change with it — silently finding "
                + "nothing would make every check in this class vacuous.");

        return match.Groups["json"].Value;
    }

    [Fact]
    public void The_template_is_valid_json()
    {
        // A template the host cannot parse renders nothing at all. This is the cheapest possible
        // check and it covers the most common edit-time mistake in a hand-written card: a trailing
        // comma or an unbalanced brace inside a multi-line literal.
        using JsonDocument document = JsonDocument.Parse(TemplateJson());

        Assert.Equal(JsonValueKind.Object, document.RootElement.ValueKind);
    }

    [Fact]
    public void The_template_declares_the_adaptive_card_schema_version_the_plan_requires()
    {
        // Section 1 chose Adaptive Cards 1.5 deliberately, over the HTML widget option. A template
        // claiming an older version silently loses the features that choice was made for.
        using JsonDocument document = JsonDocument.Parse(TemplateJson());

        Assert.Equal("AdaptiveCard", document.RootElement.GetProperty("type").GetString());
        Assert.Equal("1.5", document.RootElement.GetProperty("version").GetString());
    }

    [Fact]
    public void Every_field_the_template_binds_exists_in_the_data_document()
    {
        // This is the check that catches a whole class of invisible failure. Adaptive Card data
        // binding is case-sensitive and silent: a template binding ${headline} against a document
        // containing "Headline" renders an empty string, so the card appears with its layout intact
        // and no text. That looks like a styling problem, and it is a naming one.
        string template = TemplateJson();

        // Tokens of the form ${name}. Bindings that reach into the host context ($host.widgetSize)
        // or use an operator are excluded: those are expressions evaluated by the host, not fields
        // the provider supplies.
        string[] bound = [.. Regex.Matches(template, @"\$\{(?<token>[^}]+)\}")
            .Select(m => m.Groups["token"].Value.Trim())
            .Where(token => !token.StartsWith('$'))
            .Where(token => Regex.IsMatch(token, @"^[A-Za-z_][A-Za-z0-9_]*$"))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        Assert.NotEmpty(bound);

        // The data contract's property names, camelCased the way the serializer options do it.
        string[] supplied = [.. Regex.Matches(
                CardSource(),
                @"public\s+\w[\w<>?]*\s+(?<name>\w+)\s*\{\s*get;\s*init;\s*\}")
            .Select(m => m.Groups["name"].Value)
            .Select(name => char.ToLowerInvariant(name[0]) + name[1..])
            .Distinct(StringComparer.Ordinal)];

        Assert.NotEmpty(supplied);

        string[] unsatisfied = [.. bound.Except(supplied, StringComparer.Ordinal)];

        Assert.True(
            unsatisfied.Length == 0,
            "The template binds fields the data document does not supply: "
                + string.Join(", ", unsatisfied)
                + ". Supplied: " + string.Join(", ", supplied.Order(StringComparer.Ordinal)));
    }

    [Fact]
    public void The_data_document_is_serialized_with_camel_case_naming()
    {
        // The pairing above compares camelCased property names, which is only the right comparison
        // if the serializer actually camelCases them. Removing the naming policy would make that
        // test pass and every bound field render empty, so the policy is asserted directly.
        Assert.Contains(
            "PropertyNamingPolicy = JsonNamingPolicy.CamelCase",
            CardSource(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Every_verb_the_template_offers_is_one_the_provider_handles()
    {
        // An action whose verb reaches OnActionInvoked and matches nothing is a button that does
        // nothing when pressed, with no error anywhere. The reverse — a handled verb no card
        // offers — is harmless, so only this direction is enforced.
        string[] offered = [.. Regex.Matches(TemplateJson(), @"""verb""\s*:\s*""(?<verb>[^""]+)""")
            .Select(m => m.Groups["verb"].Value)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];

        Assert.NotEmpty(offered);

        string verbsSource = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "Cards", "WidgetVerbs.cs"));

        string[] declared = [.. Regex.Matches(
                verbsSource,
                @"public\s+const\s+string\s+\w+\s*=\s*""(?<verb>[^""]+)""")
            .Select(m => m.Groups["verb"].Value)];

        Assert.NotEmpty(declared);

        string[] unhandled = [.. offered.Except(declared, StringComparer.Ordinal)];

        Assert.True(
            unhandled.Length == 0,
            "The card offers verbs the provider does not declare: " + string.Join(", ", unhandled));
    }

    [Fact]
    public void Only_Action_Execute_is_used_and_never_Action_OpenUrl()
    {
        // Section 3 requires Action.Execute for every action and forbids Action.OpenUrl in v1. The
        // difference is where validation happens: Action.Execute returns to the provider, which can
        // check the snapshot generation and validate a link against the Outlook-host allowlist
        // before anything opens. Action.OpenUrl hands a URL straight to the shell with the provider
        // never consulted, which would put a Graph webLink into card JSON and open it unchecked.
        string template = TemplateJson();

        Assert.DoesNotContain("Action.OpenUrl", template, StringComparison.Ordinal);
        Assert.Contains("Action.Execute", template, StringComparison.Ordinal);
    }

    [Fact]
    public void The_card_withholds_message_details_once_the_snapshot_passes_the_stale_bound()
    {
        // Section 8 requires that 24 hours without a successful refresh hides message details rather
        // than presenting old subjects as current. CoordinationBounds.StaleDetailSuppression carried
        // that number from the start and nothing consulted it, because until the card rendered mail
        // there was no detail to withhold. This asserts the rule has an implementation and that it
        // derives the bound from the shared constant rather than from a second hard-coded 24.
        string source = CardSource();

        Assert.Contains(
            "CoordinationBounds.StaleDetailSuppression",
            source,
            StringComparison.Ordinal);

        Assert.DoesNotContain("TimeSpan.FromHours(24)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Reduced_disclosure_withholds_message_rows_without_withholding_the_counts()
    {
        // Both reduced states — the 24-hour stale bound and CountsOnly — hide message details and
        // keep the counts. The first version of this card got both wrong in the same way: it
        // replaced the unread-count headline with a status word, which turned "hide the details"
        // into "hide the mailbox" and, in counts-only, removed the very thing that mode is named
        // for. DeliveryWorker withholds the payload only for signed-out, so counts-only genuinely
        // has a snapshot to count.
        //
        // Enforced at the point the decision is made: the message-row list is the only thing either
        // suppression may remove, so the row gate names both conditions and the snapshot gate names
        // neither.
        string source = CardSource();

        Assert.Contains(
            "state.Mode == DisclosureMode.Full && !detailsAreStale",
            source,
            StringComparison.Ordinal);

        // The snapshot must be read for every mode that is offered a payload, not for Full alone.
        // Gating deserialization on Full is what discarded the counts-only counts.
        Assert.Contains(
            "state.Mode != DisclosureMode.SignedOut",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_render_path_never_reads_the_cached_message_link()
    {
        // Section 17 requires that no link or message identifier appear in Adaptive Card JSON: the
        // per-message action carries a slot index and the snapshot generation instead. The strongest
        // form of that guarantee is a render path that never touches the link at all, so it cannot
        // reach card JSON even through a future edit that forgets why.
        //
        // Deliberately distinct from the approved-field check below, which scans every provider file
        // for the Graph property name. This one is about the model property on MessagePreview, which
        // is legitimately named in the provider when the message-open slice lands — but never here.
        Assert.DoesNotContain(".WebLink", CardSource(), StringComparison.Ordinal);
    }

    [Fact]
    public void No_provider_source_names_a_mailbox_field_outside_the_approved_set()
    {
        // The approved fields are sender, subject, received time, and read state. Everything here
        // is either explicitly forbidden by section 11 or a Graph property that would carry
        // message content, and none of them has any business in a provider that has no Graph client
        // yet. Checking now is what stops the first one from arriving unnoticed alongside the code
        // that does.
        (string Term, Regex Pattern)[] prohibited =
        [
            ("bodyPreview", new Regex("bodyPreview")),
            ("uniqueBody", new Regex("uniqueBody")),
            ("toRecipients", new Regex("toRecipients")),
            ("ccRecipients", new Regex("[cb]cRecipients")),
            ("attachments", new Regex(@"\battachments\b")),
            ("webLink", new Regex("webLink")),

            // Mail.Read, but not Mail.ReadBasic, which is the one approved scope and will
            // legitimately appear here in slice 2. A plain substring check would reject it.
            ("Mail.Read", new Regex(@"Mail\.Read(?!Basic)")),
        ];

        var offenders = new List<string>();

        foreach (string file in RepositorySources.ProviderSourceFiles())
        {
            // Deliberately the raw text, comments and string literals included. Stripping either
            // would defeat the check: a Graph $select list is a string literal, and that is exactly
            // where a forbidden property would first appear. The cost is that provider comments
            // must describe these fields in words rather than naming them, which the delivery
            // sink's CustomState comment does.
            string code = File.ReadAllText(file);

            foreach ((string term, Regex pattern) in prohibited)
            {
                if (pattern.IsMatch(code))
                {
                    offenders.Add($"{Path.GetFileName(file)}: {term}");
                }
            }
        }

        Assert.True(
            offenders.Count == 0,
            "The provider names mailbox fields outside the approved sender/subject/received/read "
                + "set: " + string.Join(", ", offenders));
    }
}
