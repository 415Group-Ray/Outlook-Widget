using Microsoft.Identity.Client;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Packaging;

namespace OutlookWidget.App;

/// <summary>
/// A minimal packaged companion, sufficient to prove Phase 0 gate 1, the companion-activation half of
/// the packaging work, and the interactive half of authentication.
/// </summary>
/// <remarks>
/// <para>
/// This is not the companion described in section 3. It has no settings, no diagnostics page, and no
/// WinUI. What it now does have is a real top-level window and real brokered sign-in, because gate 8
/// cannot be measured without either: WAM requires a parent window handle, and the provider cannot
/// acquire a token silently until the broker holds one for this registration.
/// </para>
/// <para>
/// It also reports two facts that are cheap here and awkward to establish later: whether the process
/// has package identity at all, and where the packaged per-user local data directory actually resolves
/// to. The second matters because the whole coordination design assumed
/// <c>LocalApplicationData</c> is redirected into the package's own store when running packaged, and
/// measurement on this machine showed it is not.
/// </para>
/// <para>
/// Since the provider exists, this probe is also how the widget action that launches the companion is
/// observed: gate 6 passes when clicking the widget's action makes this window appear.
/// </para>
/// </remarks>
internal static class Program
{
    /// <summary>
    /// The MSAL client, built on first use.
    /// </summary>
    /// <remarks>
    /// Deliberately not built at startup. Constructing it touches the broker and opens the shared token
    /// cache, and a companion the user opened to read its diagnostic report should not be doing either
    /// until they ask to sign in. Serialization is provided by the window, which permits one sign-in at
    /// a time.
    /// </remarks>
    private static IPublicClientApplication? _client;

    [STAThread]
    private static int Main(string[] args)
    {
        PackagedStateResult state = PackagedState.Locate();
        AuthenticationConfigurationResult configuration = AuthenticationConfiguration.Load();

        string report = BuildIdentityReport(args, state, configuration);

        return CompanionWindow.Run(report, () => SignInAsync(state, configuration));
    }

    /// <summary>
    /// Signs in and describes the outcome, for gate 8.
    /// </summary>
    /// <remarks>
    /// Runs on a thread-pool thread; see <see cref="CompanionWindow"/>. Every return value is a
    /// human-readable status, never a token, an account, or an exception message.
    /// </remarks>
    private static async Task<string> SignInAsync(
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        if (!state.IsResolved)
        {
            return "Cannot sign in: this process has no package identity, so there is nowhere "
                   + "inside the package store to keep the token cache. Launch the installed "
                   + "package rather than the build output.";
        }

        if (!configuration.IsLoaded)
        {
            return $"Cannot sign in: the Entra registration configuration is {configuration.Status}. "
                   + $"The package must ship a valid {AuthenticationConfiguration.FileName}.";
        }

        CoordinationPaths paths = state.Paths!;
        paths.EnsureCreated();

        _client ??= await BrokerClient
            .CreateAsync(configuration.Options!, paths, () => CompanionWindow.Handle)
            .ConfigureAwait(false);

        var service = new InteractiveAuthService(_client);
        TokenAcquisitionResult result = await service.SignInAsync().ConfigureAwait(false);

        return Describe(result, paths, service.LastFailure);
    }

    /// <summary>
    /// Turns one acquisition outcome into the gate-8 report.
    /// </summary>
    /// <remarks>
    /// The token itself is never shown, and neither is the account. What is shown is the status, the
    /// expiry, and — on success — the fact that the broker now holds a token the provider can attempt
    /// to acquire silently, which is the precondition gate 9 was waiting on.
    /// </remarks>
    private static string Describe(
        TokenAcquisitionResult result,
        CoordinationPaths paths,
        string? failureDetail)
    {
        var lines = new List<string>
        {
            "Interactive sign-in result: " + result.Status,
            string.Empty,
        };

        // Categories only — exception type, MSAL error code, AADSTS numbers. Shown because a status
        // word alone was not enough to diagnose a real tenant consent block.
        if (failureDetail is not null)
        {
            lines.Add("Signals: " + failureDetail);
            lines.Add(string.Empty);
        }

        switch (result.Status)
        {
            case TokenAcquisitionStatus.Acquired:
                lines.Add("A token was acquired for Mail.ReadBasic.");
                lines.Add($"Expires: {result.ExpiresOn:u}");
                lines.Add(string.Empty);

                // This used to assert that self-consent had therefore succeeded without an
                // administrator step. It cannot: a token acquired after an administrator granted
                // consent is indistinguishable here from one acquired by self-consent, so the claim
                // was false on the very first tenant that needed admin consent. Which consent path
                // was exercised is a fact about the tenant, and only the tenant can report it.
                lines.Add("This does NOT establish that self-consent works. An acquisition looks "
                          + "identical whether the user consented themselves or an administrator "
                          + "granted consent beforehand. Check the registration's consent state to "
                          + "know which happened.");
                lines.Add(string.Empty);
                lines.Add("The broker now holds a token for this registration, and the shared cache "
                          + "below holds the account metadata the provider needs to find it. Gate 9 "
                          + "is now measurable: pin the widget and read the large card's config line.");
                lines.Add(paths.TokenCacheFilePath);
                break;

            case TokenAcquisitionStatus.ApprovalRequired:
                lines.Add("Consent could not be self-granted, so tenant policy requires an "
                          + "administrator to approve Mail.ReadBasic for this registration. This is "
                          + "the authorization state section 8 requires to be distinguishable from a "
                          + "Graph 403 — it is not a retryable failure.");
                lines.Add(string.Empty);
                lines.Add("Measured on this tenant: the user-consent setting is \"Let Microsoft "
                          + "manage your consent settings\" with mail-client consent enabled, which "
                          + "permits user consent to mail permissions only for a fixed list of "
                          + "Microsoft-chosen applications. A registration of one's own is not on it "
                          + "and cannot be added, so self-consent is unavailable here regardless of "
                          + "how permissive the setting reads.");
                break;

            case TokenAcquisitionStatus.BrokerUnavailable:
                lines.Add("The Windows authentication broker could not be used. Signing in again will "
                          + "not help. This is the outcome that would make gate 9 unprovable, because "
                          + "the provider has no path that does not go through the broker.");
                break;

            case TokenAcquisitionStatus.Cancelled:
                lines.Add("The sign-in dialog was dismissed, or consent was declined. Nothing was "
                          + "changed; press the button again to retry.");
                lines.Add(string.Empty);

                // The classifier cannot tell these apart, so the copy has to. The broker reports a
                // dismissed Approval-required dialog as an ordinary cancellation, which means a
                // policy block and a closed window are indistinguishable from the outcome alone —
                // and only one of them is worth retrying.
                lines.Add("If the dialog said \"Approval required\" rather than asking you to "
                          + "consent, retrying will show it again: tenant policy is withholding "
                          + "consent, and an administrator has to grant it for this registration. "
                          + "The broker reports that dismissal as an ordinary cancellation, so this "
                          + "status cannot distinguish the two cases.");
                break;

            case TokenAcquisitionStatus.InteractionRequired:
                lines.Add("Interactive acquisition still reported that interaction is required, which "
                          + "should not happen on this path and is worth recording as a gate-8 "
                          + "anomaly rather than a transient failure.");
                break;

            case TokenAcquisitionStatus.NoConfiguration:
                lines.Add("No usable Entra registration, so no request was attempted.");
                break;

            default:
                lines.Add("Sign-in failed. This is usually transient — a network or service problem — "
                          + "rather than a configuration or policy one.");
                break;
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string BuildIdentityReport(
        string[] args,
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        var lines = new List<string>
        {
            "Phase 0 packaging and authentication probe. This is not the real companion app.",
            string.Empty,
            "Press Sign in to exercise brokered WAM sign-in and self-consent to Mail.ReadBasic.",
            "Nothing touches the broker or the token cache until you do.",
            string.Empty,
        };

        // The provider passes an argument when it launches this, so gate 6 can distinguish
        // "the widget action started the companion" from the user starting it from Start.
        if (args.Length > 0)
        {
            lines.Add($"Launched with argument: {string.Join(' ', args)}");
            lines.Add(string.Empty);
        }

        // Both the unpackaged case and a failed query run through PackagedState, the same guarded
        // composition the provider uses. This probe is allowed to *report* that it is unpackaged —
        // that is half its purpose — but it must not be the place where a second, unguarded
        // combination of "identity may be null" and "Resolve accepts null" gets written.
        if (state.Status == PackagedStateStatus.IdentityQueryFailed)
        {
            lines.Add("Package identity: QUERY FAILED.");
            lines.Add("State location cannot be determined safely. Nothing was read or written.");
            return string.Join(Environment.NewLine, lines);
        }

        if (state.Status == PackagedStateStatus.Unpackaged)
        {
            lines.Add("Package identity: NONE — running unpackaged.");
            lines.Add(
                "If this appears after installing the MSIX, the app was launched from its build "
                + "output rather than through the installed package.");
            lines.Add(string.Empty);
            lines.Add("No coordination state path was resolved and nothing was created. The real "
                      + "companion will refuse to run in this state for the same reason the provider "
                      + "does: state outside the package store survives uninstall.");
            return string.Join(Environment.NewLine, lines);
        }

        string packageFamilyName = state.PackageFamilyName!;

        lines.Add("Package identity: present.");

        // The full name is diagnostic only. It carries the version, so it must never place state.
        try
        {
            lines.Add($"Package full name: {PackageIdentity.TryGetFullName()}");
        }
        catch (PackageIdentityException)
        {
            lines.Add("Package full name: unavailable (family name resolved, so state is placed).");
        }

        lines.Add($"Package family:    {packageFamilyName}");
        lines.Add(string.Empty);

        // The family name, not LocalApplicationData, is what places packaged state. Measurement
        // on this machine showed LocalApplicationData is NOT redirected for a packaged full-trust
        // desktop app, so state located that way would survive uninstall — contradicting the
        // product's own privacy claim. CoordinationPaths places it explicitly instead.
        CoordinationPaths paths = state.Paths!;
        lines.Add("Coordination state root:");
        lines.Add(paths.RootDirectory);
        lines.Add(string.Empty);

        bool insidePackageStore = paths.RootDirectory.Contains(
            Path.Combine("Packages", packageFamilyName),
            StringComparison.OrdinalIgnoreCase);

        lines.Add(insidePackageStore
            ? "Inside the package store, so uninstall removes it. This is what section 11 "
              + "promises about cached mailbox data."
            : "WARNING: outside the package store. Cached mailbox data would survive "
              + "uninstall, contradicting the stated privacy behaviour.");

        lines.Add(string.Empty);

        // Status only, never the tenant or client ID. Neither is a secret, but a diagnostic report
        // the user may paste into a support thread is not the place for them either.
        lines.Add($"Entra registration configuration: {configuration.Status}.");

        if (configuration.IsLoaded)
        {
            lines.Add("Requested scope: " + string.Join(", ", AuthenticationOptions.Scopes)
                      + " (compile-time constant; configuration cannot widen it).");
            lines.Add("Shared token cache:");
            lines.Add(paths.TokenCacheFilePath);
        }

        lines.Add(string.Empty);
        lines.Add($"Bounds in force: mutex wait {CoordinationBounds.MutexWait.TotalSeconds:0}s, "
                  + $"async deadline {CoordinationBounds.AsyncDeadline.TotalSeconds:0}s, "
                  + $"lease horizon {CoordinationBounds.LeaseHorizon.TotalSeconds:0}s.");

        return string.Join(Environment.NewLine, lines);
    }
}
