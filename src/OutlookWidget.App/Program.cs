using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Launching;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Packaging;

namespace OutlookWidget.App;

/// <summary>The action displayed by the privacy button and the value that action commits.</summary>
internal readonly record struct PrivacyToggleAction(bool DesiredHideValue, string Caption);

/// <summary>
/// Starts the single-instance WinUI companion and composes its reviewed operations.
/// </summary>
/// <remarks>
/// The custom entry point is load-bearing. WinUI apps are multi-instance by default, while disclosure
/// operations in this companion must be serialized in one process. The instance key is claimed before
/// XAML starts; a later activation is redirected to and foregrounds the existing window.
/// </remarks>
internal static partial class Program
{
    private const string InstanceKey = "OutlookWidget.Companion";
    private const uint Infinite = 0xFFFFFFFF;

    private static DispatcherQueue? _dispatcher;
    private static Action? _activateMainWindow;
    private static Func<IntPtr> _parentWindow = static () => IntPtr.Zero;

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
        WinRT.ComWrappersSupport.InitializeComWrappers();

        if (RedirectToExistingInstance())
        {
            return 0;
        }

        Application.Start(initialization =>
        {
            _dispatcher = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(
                new DispatcherQueueSynchronizationContext(_dispatcher));
            _ = new App();
        });

        return 0;
    }

    internal static MainWindow CreateMainWindow(string[] args)
    {
        PackagedStateResult state = PackagedState.Locate();
        AuthenticationConfigurationResult configuration = AuthenticationConfiguration.Load();

        var commands = new CompanionCommands(
            () => SignInAsync(state, configuration),
            () => SwitchAccountAsync(state, configuration),
            () => SignOutAsync(state, configuration),
            () => Task.FromResult(ClearInterruptedOperations(state)),
            desiredHide => Task.FromResult(TogglePrivacySetting(state, desiredHide)),
            () => Task.FromResult(ShowDiagnostics(state)),
            () => Task.FromResult(TestOutlook(state)),
            () => NextPrivacyToggleAction(state));

        var window = new MainWindow(BuildStatusReport(args, state, configuration), commands);
        _parentWindow = () => window.Handle;
        return window;
    }

    internal static void SetActivationHandler(Action activateMainWindow)
    {
        ArgumentNullException.ThrowIfNull(activateMainWindow);
        _activateMainWindow = activateMainWindow;
    }

    private static bool RedirectToExistingInstance()
    {
        AppActivationArguments activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        AppInstance mainInstance = AppInstance.FindOrRegisterForKey(InstanceKey);

        if (mainInstance.IsCurrent)
        {
            mainInstance.Activated += OnActivated;
            return false;
        }

        RedirectActivation(activation, mainInstance);
        return true;
    }

    private static void OnActivated(object? sender, AppActivationArguments args)
    {
        _dispatcher?.TryEnqueue(() => _activateMainWindow?.Invoke());
    }

    /// <summary>
    /// Redirects on a worker and waits with a COM-aware STA wait, following the Windows App SDK's
    /// single-instance guidance. A plain Task.Wait on this thread can deadlock the redirection.
    /// </summary>
    private static void RedirectActivation(
        AppActivationArguments activation,
        AppInstance mainInstance)
    {
        using var completed = new EventWaitHandle(false, EventResetMode.ManualReset);
        Exception? failure = null;

        _ = Task.Run(async () =>
        {
            try
            {
                await mainInstance.RedirectActivationToAsync(activation);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException
                                                and not StackOverflowException)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        WaitForRedirection(completed.SafeWaitHandle.DangerousGetHandle());

        if (failure is null)
        {
            try
            {
                using Process process = Process.GetProcessById((int)mainInstance.ProcessId);
                _ = SetForegroundWindow(process.MainWindowHandle);
            }
            catch (Exception exception) when (exception is ArgumentException
                                                 or InvalidOperationException)
            {
                // The primary process may have exited after accepting redirection. A new launch can
                // register the now-free key; this secondary has no state of its own to preserve.
            }
        }
    }

    private static unsafe void WaitForRedirection(IntPtr completedEvent)
    {
        _ = CoWaitForMultipleObjects(0, Infinite, 1, &completedEvent, out _);
    }

    /// <summary>
    /// What the privacy toggle would do if pressed right now: the value it would store, and the
    /// caption that describes it.
    /// </summary>
    /// <remarks>
    /// <b>One function, because a caption and an action computed separately can disagree — and
    /// did.</b> The button was created with a fixed caption and only relabelled after an operation
    /// finished, so reopening the companion with the setting already on showed "Hide message
    /// details" over a click that would reveal them. A label that lies about its effect is worse
    /// than no label, and the only reliable fix is for one place to decide both.
    /// </remarks>
    /// <summary>
    /// Decides that action from the stored setting.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Branches on the read status, not on the substituted value.</b> An unreadable file reads
    /// back as <c>HideMessageDetails = true</c> so that a renderer ignoring the status still
    /// withholds — but that substitution is a rendering decision, not a record of what anyone
    /// chose. Negating it asks to <em>show</em> details, which would repair a damaged file by
    /// explicitly enabling disclosure on the strength of a value nobody stored. Unknown therefore
    /// offers to hide: it is the fail-closed direction, and writing it repairs the file in the safe
    /// direction rather than the unsafe one.
    /// </para>
    /// <para>
    /// Read on every call rather than cached, because the provider shares this file and a remembered
    /// answer would drift.
    /// </para>
    /// </remarks>
    private static PrivacyToggleAction NextPrivacyToggleAction(PackagedStateResult state)
    {
        const string Hide = "Hide message details";
        const string Show = "Show message details";

        if (!state.IsResolved)
        {
            // No identity means no settings to read, and the button will refuse when pressed.
            return new PrivacyToggleAction(DesiredHideValue: true, Hide);
        }

        SettingsReadResult stored = new WidgetSettingsStore(state.Paths!).Read();

        if (stored.Status == SettingsReadStatus.Unreadable)
        {
            return new PrivacyToggleAction(DesiredHideValue: true, Hide);
        }

        return stored.Settings.HideMessageDetails
            ? new PrivacyToggleAction(DesiredHideValue: false, Show)
            : new PrivacyToggleAction(DesiredHideValue: true, Hide);
    }

    /// <summary>
    /// Applies "hide message details" through the coordinator that owns the ordering.
    /// </summary>
    /// <remarks>
    /// <paramref name="desired"/> is the action paired with the caption currently displayed by the
    /// window. It is deliberately not re-read here: shared state can change after the button is
    /// labelled, and recomputing would let a button that still says "Hide" perform "Show".
    /// </remarks>
    private static CompanionOperationResult TogglePrivacySetting(
        PackagedStateResult state,
        bool desired)
    {
        if (!state.IsResolved)
        {
            return CompanionOperationResult.Failure(
                "Cannot change the privacy setting: this process has no package identity. Launch "
                + "the installed app rather than the executable directly.");
        }

        CoordinationPaths paths = state.Paths!;
        var settings = new WidgetSettingsStore(paths);
        var logger = new FileOperationalLogger(paths);

        var coordinator = new SettingsChangeCoordinator(
            paths,
            settings,
            new DisclosureTombstoneStore(paths, logger),
            logger);

        SettingsChangeResult result = coordinator.Apply(new WidgetSettings
        {
            HideMessageDetails = desired,
        });

        string report = DescribeSettingsChange(result, desired);
        return result.Outcome is SettingsChangeOutcome.Applied or SettingsChangeOutcome.Unchanged
            ? CompanionOperationResult.Success(report)
            : CompanionOperationResult.Failure(report);
    }

    /// <summary>
    /// Turns a settings-change result into the report line, including the parts that are easy to
    /// present as success and are not.
    /// </summary>
    private static string DescribeSettingsChange(SettingsChangeResult result, bool desired)
    {
        string intent = desired ? "Message details are now hidden." : "Message details are now shown.";

        string outcome = result.Outcome switch
        {
            SettingsChangeOutcome.Applied => intent,

            SettingsChangeOutcome.Unchanged =>
                "The setting already had that value; nothing was written.",

            // Reported as a failure even when the widget is left showing what was asked for: the
            // stored preference did not change, so the next thing to read it will disagree.
            SettingsChangeOutcome.WriteFailed =>
                "The setting could not be saved.",

            SettingsChangeOutcome.SuppressionClearFailed =>
                "The setting was saved, but this operation's suppression marker could not be "
                + "removed. Use Clear interrupted operations.",

            _ => "The setting change reported an outcome this build does not recognise.",
        };

        // Stated separately from the outcome, because they answer different questions: one is what
        // happened to the stored preference, the other is what the widget is currently showing.
        string visible = result.DetailsRemainHidden
            ? " Message details remain hidden on the widget."
            : string.Empty;

        // Null means nothing was written, so there was nothing to deliver. False is worth saying:
        // the setting is committed and a running widget may not have noticed yet.
        string delivery = result.ProviderNotified == false
            ? " The running widget was not notified and may show the previous setting until it "
              + "next refreshes."
            : string.Empty;

        return outcome + visible + delivery;
    }

    /// <summary>
    /// Opens the diagnostics log with the shell.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The log is metadata-free by construction — <c>IOperationalLogger</c> has no string parameter
    /// — so handing it to the default text handler discloses nothing about a mailbox. This is the
    /// surface that replaces the widget card's diagnostic block. The original Win32 control was
    /// measured before the block was removed; the WinUI replacement still needs its own installed-
    /// package visual check.
    /// </para>
    /// <para>
    /// <c>UseShellExecute</c>, because the file has no meaning as an executable and the point is to
    /// open it in whatever the user reads text with.
    /// </para>
    /// </remarks>
    private static CompanionOperationResult ShowDiagnostics(PackagedStateResult state)
    {
        if (!state.IsResolved)
        {
            return CompanionOperationResult.Failure(
                "Cannot open diagnostics: this process has no package identity.");
        }

        string path = state.Paths!.DiagnosticsLogFilePath;

        if (!File.Exists(path))
        {
            // Not a failure. Nothing has been logged yet, which is the ordinary state of a fresh
            // install before the provider has run.
            return CompanionOperationResult.Success("No diagnostics have been recorded yet.");
        }

        try
        {
            using Process? opened = Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
            });

            return CompanionOperationResult.Success("Opened the diagnostics log.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // The path is reported rather than the exception, which can name a handler or a shell
            // error string. Somewhere to look beats a message that cannot be acted on.
            return CompanionOperationResult.Failure(
                "The diagnostics log could not be opened. It is at: " + path);
        }
    }

    private static async Task<CompanionOperationResult> SwitchAccountAsync(
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        if (!state.IsResolved)
        {
            return CompanionOperationResult.Failure(
                "Cannot switch accounts safely: this process has no package identity. Launch the "
                + "installed package rather than the build output.");
        }

        if (!configuration.IsLoaded)
        {
            return CompanionOperationResult.Failure(
                $"Cannot switch accounts: the Entra registration configuration is "
                + $"{configuration.Status}. The package must ship a valid "
                + $"{AuthenticationConfiguration.FileName}.");
        }

        CoordinationPaths paths = state.Paths!;
        AuthenticationOptions options = configuration.Options!;
        paths.EnsureCreated();

        try
        {
            IOperationalLogger logger = new FileOperationalLogger(paths);

            _client ??= await BrokerClient
                .CreateAsync(options, paths, _parentWindow)
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

            string report = result.Outcome switch
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

            return result.Outcome == AccountSwitchOutcome.Switched
                ? CompanionOperationResult.Success(report)
                : CompanionOperationResult.Failure(report);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return CompanionOperationResult.Failure(
                "Account switching failed before the new selection could be committed. Message "
                + "details remain hidden if suppression had already begun. Signals: "
                + AuthenticationFailures.Describe(e));
        }
    }

    private static async Task<CompanionOperationResult> SignOutAsync(
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        if (!state.IsResolved)
        {
            return CompanionOperationResult.Failure(
                "Cannot sign out safely: this process has no package identity. Launch the "
                + "installed package rather than the build output.");
        }

        if (!configuration.IsLoaded)
        {
            return CompanionOperationResult.Failure(
                $"Cannot sign out: the Entra registration configuration is {configuration.Status}. "
                + $"The package must ship a valid {AuthenticationConfiguration.FileName}.");
        }

        CoordinationPaths paths = state.Paths!;
        AuthenticationOptions options = configuration.Options!;
        paths.EnsureCreated();

        try
        {
            IOperationalLogger logger = new FileOperationalLogger(paths);

            _client ??= await BrokerClient
                .CreateAsync(options, paths, _parentWindow)
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

            string report = result.Outcome switch
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

            return result.Outcome == SignOutOutcome.SignedOut
                ? CompanionOperationResult.Success(report)
                : CompanionOperationResult.Failure(report);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            return CompanionOperationResult.Failure(
                "Sign-out failed before local state could be committed. Message details remain "
                + "hidden if suppression had already begun. Signals: "
                + AuthenticationFailures.Describe(e));
        }
    }

    private static CompanionOperationResult ClearInterruptedOperations(PackagedStateResult state)
    {
        if (!state.IsResolved)
        {
            return CompanionOperationResult.Failure(
                "Cannot clear interrupted operations safely: this process has no package identity. "
                + "Launch the installed package rather than the build output.");
        }

        var tombstones = new DisclosureTombstoneStore(state.Paths!);
        DisclosureRecoveryResult recovery = tombstones.ClearAllOrphansWithResult();

        if (recovery.Status == DisclosureRecoveryStatus.Unreadable)
        {
            return CompanionOperationResult.Failure(
                "Recovery result: Unknown\r\n\r\nThe suppression directory could not be read, "
                + "so no cleanup success is being claimed. Message details remain hidden; "
                + "retry once. If this persists, use the provider-recycle steps in "
                + "troubleshooting; do not unpin the widget.");
        }

        int remaining = tombstones.CountSuppressionFiles();

        string report = remaining switch
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

        return remaining == 0
            ? CompanionOperationResult.Success(report)
            : CompanionOperationResult.Failure(report);
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
    /// Runs on a thread-pool thread; see <see cref="MainWindow"/>. Every result contains a
    /// human-readable status, never a token, an account, or an exception message.
    /// </remarks>
    private static async Task<CompanionOperationResult> SignInAsync(
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        if (!state.IsResolved)
        {
            return CompanionOperationResult.Failure(
                "Cannot sign in: this process has no package identity, so there is nowhere "
                + "inside the package store to keep the token cache. Launch the installed "
                + "package rather than the build output.");
        }

        if (!configuration.IsLoaded)
        {
            return CompanionOperationResult.Failure(
                $"Cannot sign in: the Entra registration configuration is {configuration.Status}. "
                + $"The package must ship a valid {AuthenticationConfiguration.FileName}.");
        }

        CoordinationPaths paths = state.Paths!;
        AuthenticationOptions options = configuration.Options!;
        paths.EnsureCreated();

        TokenAcquisitionResult result;
        string? failureDetail;

        try
        {
            IOperationalLogger logger = new FileOperationalLogger(paths);

            _client ??= await BrokerClient
                .CreateAsync(options, paths, _parentWindow)
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
            // classified it. Left unhandled it escaped to MainWindow's outer catch and displayed
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
    private static CompanionOperationResult Describe(
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

        string report = string.Join(Environment.NewLine, lines);
        return result.IsAcquired
            ? CompanionOperationResult.Success(report)
            : CompanionOperationResult.Failure(report);
    }

    private static CompanionOperationResult TestOutlook(PackagedStateResult state)
    {
        IOperationalLogger logger = state.IsResolved
            ? new FileOperationalLogger(state.Paths!)
            : NullOperationalLogger.Instance;

        OutlookLaunchResult result = new OutlookLauncher(logger).Launch();

        string report = result.IsSuccess
            ? $"New Outlook accepted the launch request via {result.Strategy}."
            : "New Outlook could not be opened through either supported launch path. The widget "
              + "does not fall back to Classic Outlook.";

        return result.IsSuccess
            ? CompanionOperationResult.Success(report)
            : CompanionOperationResult.Failure(report);
    }

    private static string BuildStatusReport(
        string[] args,
        PackagedStateResult state,
        AuthenticationConfigurationResult configuration)
    {
        var lines = new List<string>
        {
            "The companion is ready.",
            string.Empty,
        };

        // The provider passes an argument when it launches this, so gate 6 can distinguish
        // "the widget action started the companion" from the user starting it from Start.
        if (args.Length > 0)
        {
            lines.Add("Opened from the widget.");
            lines.Add(string.Empty);
        }

        // Both the unpackaged case and a failed query run through PackagedState, the same guarded
        // composition the provider uses. This probe is allowed to *report* that it is unpackaged —
        // that is half its purpose — but it must not be the place where a second, unguarded
        // combination of "identity may be null" and "Resolve accepts null" gets written.
        if (state.Status == PackagedStateStatus.IdentityQueryFailed)
        {
            lines.Add("Package identity: unavailable.");
            lines.Add("State cannot be located safely, so every account and settings action is disabled by its operation guard.");
            return string.Join(Environment.NewLine, lines);
        }

        if (state.Status == PackagedStateStatus.Unpackaged)
        {
            lines.Add("Package identity: missing.");
            lines.Add("Launch Outlook Inbox Widget from Start or the widget rather than running its build output.");
            lines.Add(string.Empty);
            lines.Add("No coordination path was resolved and nothing was read or written.");
            return string.Join(Environment.NewLine, lines);
        }

        string packageFamilyName = state.PackageFamilyName!;

        CoordinationPaths paths = state.Paths!;
        bool insidePackageStore = paths.RootDirectory.Contains(
            Path.Combine("Packages", packageFamilyName),
            StringComparison.OrdinalIgnoreCase);

        lines.Add("Package: " + (insidePackageStore ? "Installed" : "State location needs attention"));
        lines.Add($"Microsoft 365 configuration: {configuration.Status}");
        lines.Add("Permission: Mail.ReadBasic only");

        if (configuration.IsLoaded)
        {
            AuthenticationOptions options = configuration.Options!;
            IOperationalLogger logger = new FileOperationalLogger(paths);
            SelectedAccountResult selected = new SelectedAccountStore(paths, options, logger).Read();

            string accountStatus = selected.Status switch
            {
                SelectedAccountStatus.Recorded => "Signed in",
                SelectedAccountStatus.SignedOut => "Signed out",
                SelectedAccountStatus.Unreadable => "Needs attention — sign in again",
                _ => "Not signed in",
            };

            lines.Add("Account: " + accountStatus);

            if (AuthorizationStateStore.TryRead(paths, options)
                == TokenAcquisitionStatus.ApprovalRequired)
            {
                lines.Add("Consent: Administrator approval required");
            }

            CacheReadResult cached = new ProtectedCache(paths, logger).Read();
            MailboxSnapshot? snapshot = cached.IsSuccess && cached.Payload is not null
                ? MailboxSnapshot.TryDeserialize(cached.Payload)
                : null;

            lines.Add(snapshot is null
                ? "Last successful refresh: None"
                : "Last successful refresh: "
                  + snapshot.RefreshedAtUtc.ToLocalTime().ToString("g", System.Globalization.CultureInfo.CurrentCulture));
        }

        lines.Add(string.Empty);
        lines.Add("Use Test New Outlook to verify the supported client can accept a launch request.");
        lines.Add("Show diagnostics opens the bounded, metadata-free local log.");

        return string.Join(Environment.NewLine, lines);
    }

    [LibraryImport("ole32.dll")]
    private static unsafe partial uint CoWaitForMultipleObjects(
        uint flags,
        uint milliseconds,
        ulong handleCount,
        IntPtr* handles,
        out uint index);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr window);
}
