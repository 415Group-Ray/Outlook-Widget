using Microsoft.Identity.Client;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
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

        return CompanionWindow.Run(
            report,
            () => SignInAsync(state, configuration),
            () => SwitchAccountAsync(state, configuration),
            () => SignOutAsync(state, configuration),
            () => Task.FromResult(ClearInterruptedOperations(state)));
    }

    private static async Task<string> SwitchAccountAsync(
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        if (!state.IsResolved)
        {
            return "Cannot switch accounts safely: this process has no package identity. Launch the "
                   + "installed package rather than the build output.";
        }

        if (!configuration.IsLoaded)
        {
            return $"Cannot switch accounts: the Entra registration configuration is "
                   + $"{configuration.Status}. The package must ship a valid "
                   + $"{AuthenticationConfiguration.FileName}.";
        }

        CoordinationPaths paths = state.Paths!;
        AuthenticationOptions options = configuration.Options!;
        paths.EnsureCreated();

        try
        {
            IOperationalLogger logger = NullOperationalLogger.Instance;

            _client ??= await BrokerClient
                .CreateAsync(options, paths, () => CompanionWindow.Handle)
                .ConfigureAwait(false);

            var selectedAccounts = new SelectedAccountStore(paths, options, logger);
            var cache = new ProtectedCache(paths, logger);
            var tombstones = new DisclosureTombstoneStore(paths, logger);
            using var mutation = new MutationMutex(paths.MutationMutexName, logger);
            var commits = new StateCommitCoordinator(paths, mutation, logger);
            var service = new InteractiveAuthService(
                _client, commits, paths, cache, selectedAccounts, logger);
            var accountSwitch = new AccountSwitchCoordinator(
                tombstones,
                commits,
                paths,
                cache,
                selectedAccounts,
                logger);

            AccountSwitchResult result = await accountSwitch
                .SwitchAsync(async () =>
                {
                    TokenAcquisitionResult selection = await service
                        .SelectAccountAsync()
                        .ConfigureAwait(false);

                    return new AccountSelectionResult(
                        selection.Status,
                        selection.HomeAccountId);
                })
                .ConfigureAwait(false);

            string failure = service.LastFailure is null
                ? string.Empty
                : $" Signals: {service.LastFailure}.";

            return result.Outcome switch
            {
                AccountSwitchOutcome.Switched =>
                    "Account-switch result: Switched\r\n\r\n"
                    + "The prior mailbox snapshot was cleared before the new selected identifier "
                    + "was committed. A running provider was signalled best-effort and will refresh "
                    + "only the newly selected mailbox.",

                AccountSwitchOutcome.StateCommitFailed =>
                    "Could not commit the selected account. Message details remain hidden by an "
                    + "interrupted-operation marker. Use Clear interrupted operations to lift "
                    + "suppression, then retry the switch. The prior selection is retained when "
                    + "the commit cannot finish. "
                    + $"Commit outcome: {result.CommitOutcome}.{failure}",

                AccountSwitchOutcome.SuppressionClearFailed =>
                    "The selected account committed, but its interrupted-operation marker could "
                    + "not be removed. The prior mailbox snapshot is cleared; use Clear interrupted "
                    + "operations to finish the local cleanup.",

                _ =>
                    $"Account selection did not complete ({result.SelectionStatus}). Message "
                    + "details remain hidden by an interrupted-operation marker. Use Clear "
                    + "interrupted operations to restore the prior display before retrying."
                    + failure,
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return "Account switching failed before the new selection could be committed. Message "
                   + "details remain hidden if suppression had already begun. Signals: "
                   + AuthenticationFailures.Describe(e);
        }
    }

    private static async Task<string> SignOutAsync(
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        if (!state.IsResolved)
        {
            return "Cannot sign out safely: this process has no package identity. Launch the "
                   + "installed package rather than the build output.";
        }

        if (!configuration.IsLoaded)
        {
            return $"Cannot sign out: the Entra registration configuration is {configuration.Status}. "
                   + $"The package must ship a valid {AuthenticationConfiguration.FileName}.";
        }

        CoordinationPaths paths = state.Paths!;
        AuthenticationOptions options = configuration.Options!;
        paths.EnsureCreated();

        try
        {
            IOperationalLogger logger = NullOperationalLogger.Instance;

            _client ??= await BrokerClient
                .CreateAsync(options, paths, () => CompanionWindow.Handle)
                .ConfigureAwait(false);

            var selectedAccounts = new SelectedAccountStore(paths, options, logger);
            var cache = new ProtectedCache(paths, logger);
            var tombstones = new DisclosureTombstoneStore(paths, logger);
            using var mutation = new MutationMutex(paths.MutationMutexName, logger);
            var commits = new StateCommitCoordinator(paths, mutation, logger);
            var action = new CommitSignedOutStateAction(paths, cache, selectedAccounts, logger);
            var signOut = new SignOutCoordinator(tombstones, commits, action, logger);

            SignOutResult result = await signOut
                .SignOutAsync(() => RemoveSelectedAccountAsync(_client, selectedAccounts))
                .ConfigureAwait(false);

            return result.Outcome switch
            {
                SignOutOutcome.SignedOut =>
                    "Sign-out result: SignedOut\r\n\r\n"
                    + "The account was removed from this app's MSAL cache, cached mailbox data and "
                    + "the selected identifier were cleared. A running provider was signalled "
                    + "best-effort and every provider re-reads durable state on activation. This does "
                    + "not remove the Windows account or an identity-provider browser session.",

                SignOutOutcome.StateCommitFailed =>
                    "Could not complete sign-out. Message details remain hidden by an interrupted-"
                    + "operation marker. Use Clear interrupted operations to restore the prior "
                    + "display, then try sign-out again. "
                    + $"Commit outcome: {result.CommitOutcome}.",

                SignOutOutcome.SuppressionClearFailed =>
                    "Sign-out committed, but its interrupted-operation marker could not be removed. "
                    + "The account and cached mailbox state are cleared; use Clear interrupted "
                    + "operations to finish the local cleanup.",

                _ =>
                    "Could not remove the account from this app's MSAL cache. Message details remain "
                    + "hidden by an interrupted-operation marker. Use Clear interrupted operations "
                    + "to restore the prior display, then try sign-out again.",
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return "Sign-out failed before local state could be committed. Message details remain "
                   + "hidden if suppression had already begun. Signals: "
                   + AuthenticationFailures.Describe(e);
        }
    }

    private static string ClearInterruptedOperations(PackagedStateResult state)
    {
        if (!state.IsResolved)
        {
            return "Cannot clear interrupted operations safely: this process has no package identity. "
                   + "Launch the installed package rather than the build output.";
        }

        var tombstones = new DisclosureTombstoneStore(state.Paths!);
        DisclosureRecoveryResult recovery = tombstones.ClearAllOrphansWithResult();

        if (recovery.Status == DisclosureRecoveryStatus.Unreadable)
        {
            return "Recovery result: Unknown\r\n\r\nThe suppression directory could not be read, "
                   + "so no cleanup success is being claimed. Message details remain hidden; "
                   + "retry once. If this persists, use the provider-recycle steps in "
                   + "troubleshooting; do not unpin the widget.";
        }

        int remaining = tombstones.CountSuppressionFiles();

        return remaining switch
        {
            0 => $"Recovery result: Cleared\r\n\r\nRemoved {recovery.RemovedCount} interrupted-operation "
                 + "marker(s). A running provider was signalled best-effort; every provider re-reads "
                 + "durable state on activation.",
            -1 => "Recovery result: Unknown\r\n\r\nThe suppression directory could not be read. "
                   + "Message details remain hidden; retry once. If this persists, use the "
                   + "provider-recycle steps in troubleshooting; do not unpin the widget.",
            _ => $"Recovery result: Incomplete\r\n\r\nRemoved {recovery.RemovedCount} interrupted-operation "
                 + $"marker(s); {remaining} active or unreadable marker(s) remain. Message details "
                 + "remain hidden.",
        };
    }

    private static async Task RemoveSelectedAccountAsync(
        IPublicClientApplication client,
        SelectedAccountStore selectedAccounts)
    {
        SelectedAccountResult selected = selectedAccounts.Read();
        List<IAccount> cached = (await client.GetAccountsAsync().ConfigureAwait(false)).ToList();

        if (selected is
            { Status: SelectedAccountStatus.Recorded, HomeAccountId: { Length: > 0 } homeAccountId })
        {
            IAccount? account = cached.FirstOrDefault(
                candidate => string.Equals(
                    candidate.HomeAccountId?.Identifier,
                    homeAccountId,
                    StringComparison.Ordinal));

            if (account is not null)
            {
                await client.RemoveAsync(account).ConfigureAwait(false);
            }

            return;
        }

        // A missing or unreadable selection cannot identify one safe account. Removing every account
        // cached for this public-client registration is the fail-closed app-local logout; it does not
        // remove the corresponding Windows accounts from WAM.
        foreach (IAccount account in cached)
        {
            await client.RemoveAsync(account).ConfigureAwait(false);
        }
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
        AuthenticationOptions options = configuration.Options!;
        paths.EnsureCreated();

        TokenAcquisitionResult result;
        string? failureDetail;

        try
        {
            IOperationalLogger logger = NullOperationalLogger.Instance;

            _client ??= await BrokerClient
                .CreateAsync(options, paths, () => CompanionWindow.Handle)
                .ConfigureAwait(false);

            // The cache and the commit coordinator are here because publishing the selected account is
            // a state commit rather than a file write: a sign-in that lands on a different account than
            // the one recorded has to remove the previous account's snapshot in the same critical
            // section. See CommitInteractiveSelectionAction.
            using var mutation = new MutationMutex(paths.MutationMutexName, logger);

            var service = new InteractiveAuthService(
                _client,
                new StateCommitCoordinator(paths, mutation, logger),
                paths,
                new ProtectedCache(paths, logger),
                new SelectedAccountStore(paths, options, logger),
                logger);

            result = await service.SignInAsync().ConfigureAwait(false);
            failureDetail = service.LastFailure;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // Building the client can fail before InteractiveAuthService exists — a broker whose native
            // runtime will not initialise, or an unreadable shared token cache — so nothing downstream has
            // classified it. Left unhandled it escaped to CompanionWindow's outer catch and displayed
            // "Sign-in failed unexpectedly: <type>", which cannot tell a broker problem from anything else.
            //
            // The provider already got this right: SilentAuthProbe wraps its own client construction and
            // classifies it. The companion is the user-facing process and was the one without it.
            TokenAcquisitionStatus status =
                AuthenticationFailures.Classify(e, AuthenticationPhase.Interactive);

            result = TokenAcquisitionResult.Unavailable(status);
            failureDetail = AuthenticationFailures.Describe(e);

            // Deliberately returns here rather than falling through to the publish block below. Nothing
            // was attempted against the tenant, so there is no authorization outcome to record and no
            // change for a provider to hear about — and the record must never be written for a failure
            // that is not a consent decision.
            return Describe(result, paths, failureDetail, providerNotified: false);
        }

        // Record a terminal authorization outcome before signalling, so a provider woken by the signal
        // finds the record already written rather than racing it.
        //
        // ApprovalRequired is the one state the provider cannot reach on its own: its classifier is
        // phase-aware and maps consent failures during *silent* acquisition to InteractionRequired,
        // deliberately, because self-consent may still be available. Only this process learns the
        // difference, so only this process can publish it. Without that, the knowledge died here and a
        // pinned card kept telling the user to sign in when signing in could never work.
        if (result.IsAcquired)
        {
            AuthorizationStateStore.Clear(paths);
        }
        else if (result.Status == TokenAcquisitionStatus.ApprovalRequired)
        {
            AuthorizationStateStore.Write(
                paths, options, result.Status, DateTimeOffset.UtcNow);
        }

        // Section 3's division of labour is that the companion commits state and signals while the
        // provider delivers; this is the signal half. Without it the ordinary flow does not converge: a
        // pinned widget rendering "sign in required" launches this app, the user signs in, and the
        // provider is still the same process holding its original result because it lives until the last
        // widget is unpinned.
        //
        // Signalled for both outcomes that changed something a provider would render differently. A
        // cancelled or transient failure is deliberately excluded — nothing changed, and a signal that
        // carries no change is how listeners learn to distrust signals.
        bool published = result.IsAcquired
                         || result.Status == TokenAcquisitionStatus.ApprovalRequired;

        bool providerNotified = published && StateChangeSignal.Raise(paths);

        return Describe(result, paths, failureDetail, providerNotified);
    }

    /// <summary>
    /// Turns one acquisition outcome into the gate-8 report.
    /// </summary>
    /// <remarks>
    /// The token itself is never shown, and neither is the account. What is shown is the status, the
    /// expiry, and — on success — the fact that the broker now holds a token the provider can acquire
    /// silently, which gate 9 confirmed it does.
    /// </remarks>
    private static string Describe(
        TokenAcquisitionResult result,
        CoordinationPaths paths,
        string? failureDetail,
        bool providerNotified)
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
                          + "below holds the account metadata the provider needs to find it.");
                lines.Add(paths.TokenCacheFilePath);
                lines.Add(string.Empty);

                lines.Add(providerNotified
                    ? "A running provider was notified and will re-acquire, so a pinned widget "
                      + "converges without being unpinned."
                    : "No provider is listening, which is normal when this app was opened from Start "
                      + "rather than from the widget. A provider probes on its own start, so nothing "
                      + "is lost.");
                break;

            case TokenAcquisitionStatus.ApprovalRequired:
                lines.Add("Consent could not be self-granted, so tenant policy requires an "
                          + "administrator to approve Mail.ReadBasic for this registration. This is "
                          + "the authorization state section 8 requires to be distinguishable from a "
                          + "Graph 403 — it is not a retryable failure.");
                lines.Add(string.Empty);

                // Tenant-neutral on purpose. This text runs against whatever tenant the build is
                // configured for, and a policy that blocks self-consent can be any of several — user
                // consent disabled outright, restricted to verified publishers, or a Microsoft-managed
                // policy with its own allowlist. Naming one of them here would be a confident wrong
                // diagnosis on every tenant that used a different one. The reference tenant's specific
                // setting is a measurement and belongs in docs/phase0-evidence.md, not in shipped copy.
                lines.Add("Which policy is responsible is worth checking rather than assuming: a "
                          + "permission not marked as requiring admin consent can still be withheld by "
                          + "the tenant's user-consent settings, and the registration's own \"admin "
                          + "consent required\" column reports the organization default rather than the "
                          + "effective policy.");
                lines.Add(string.Empty);
                lines.Add("An administrator can grant consent for this registration in Entra ID under "
                          + "Enterprise applications, or approve a request raised from the dialog.");
                break;

            case TokenAcquisitionStatus.BrokerUnavailable:
                lines.Add("The Windows authentication broker could not be used. Signing in again will "
                          + "not help, and the provider cannot work around it: it has no path that does "
                          + "not go through the broker.");
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
