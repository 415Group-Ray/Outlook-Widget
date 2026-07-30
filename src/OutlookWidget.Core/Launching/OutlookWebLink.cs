namespace OutlookWidget.Core.Launching;

/// <summary>
/// Which links this product will accept from Graph and later hand to the system browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>One allowlist, two call sites, and that is the point of putting it here.</b> Section 11 states
/// that HTTPS links are accepted only from expected Outlook hosts and that other schemes and
/// unexpected hosts are rejected; section 3 separately requires the provider to validate a cached
/// link against the Outlook-host allowlist before asking the launcher to open it. Two checks written
/// twice would be two lists that drift, so both consume this one.
/// </para>
/// <para>
/// <b>Checked at ingest as well as at launch, deliberately.</b> The launch-time check is the one that
/// actually prevents navigation, so this is defence in depth rather than the primary control — but
/// section 8 step 5 requires URLs to be validated at the response boundary, and a snapshot on disk
/// holding a link to somewhere unexpected is worth not having even if nothing would follow it.
/// </para>
/// <para>
/// <b>Exact host equality, not a suffix match.</b> A suffix rule is the classic way to write this
/// wrong: <c>endsWith("outlook.office.com")</c> also accepts <c>evil-outlook.office.com</c>, and the
/// version with a leading dot still needs care about case and trailing dots. Graph returns one of a
/// small set of documented hosts, so equality answers the question exactly and cannot be subverted by
/// a hostname that merely ends the right way.
/// </para>
/// <para>
/// <b>An unlisted host drops the link; it does not fail the message or the snapshot.</b> That matters
/// because this list is derived from Microsoft's documented Outlook on the web hosts rather than
/// measured against every cloud this product might one day run in. If a tenant's Graph returns a host
/// not listed here, the consequence is a message that renders without an open action — recoverable,
/// visible, and safe — rather than a refused response or a link somewhere unexpected.
/// </para>
/// </remarks>
public static class OutlookWebLink
{
    /// <summary>
    /// The documented Outlook on the web hosts, across the clouds a single tenant may live in.
    /// </summary>
    /// <remarks>
    /// Commercial first, then the sovereign clouds. <c>outlook.live.com</c> is included because a
    /// consumer host arriving would otherwise silently drop a working link; it is not an invitation to
    /// support personal accounts, which remain out of v1 scope for reasons that have nothing to do
    /// with this list.
    /// </remarks>
    private static readonly string[] AllowedHosts =
    [
        "outlook.office.com",
        "outlook.office365.com",
        "outlook.live.com",
        "outlook.office365.us",
        "outlook.office365.cn",
    ];

    /// <summary>
    /// Whether a link may be cached and, later, opened.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> only for an absolute HTTPS URI whose host is one of the documented
    /// Outlook hosts. Everything else — a relative URI, another scheme, an unexpected host, an
    /// unparseable string — is <see langword="false"/>.
    /// </returns>
    public static bool IsAllowed(string? link) =>
        Uri.TryCreate(link, UriKind.Absolute, out Uri? uri) && IsAllowed(uri);

    /// <inheritdoc cref="IsAllowed(string?)"/>
    public static bool IsAllowed(Uri? uri)
    {
        if (uri is null || uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        foreach (string host in AllowedHosts)
        {
            // OrdinalIgnoreCase because hostnames are case-insensitive; Uri.Host is already
            // normalised and punycode-encoded, so a lookalike in another script cannot match here by
            // rendering similarly.
            if (string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
