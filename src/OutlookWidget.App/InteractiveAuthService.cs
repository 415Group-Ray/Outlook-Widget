using Microsoft.Identity.Client;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.App;

/// <summary>
/// The companion's interactive sign-in. The only place in the product that may show authentication UI.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this lives in the companion and not in the core, which is a deviation from the plan's
/// project layout.</b> Section 12 lists <c>InteractiveAuthService</c> under
/// <c>OutlookWidget.Core</c>, and it is here instead. The reason is an invariant the plan states
/// elsewhere and states more strongly: the provider must have "no reference or code path to
/// <c>AcquireTokenInteractive</c>". The provider references the core. Had this type gone into the core,
/// the provider would link an assembly containing the interactive API, and the only thing standing
/// between a background COM server and an unowned authentication window would be a source grep over
/// provider files. Here, the provider does not reference this project at all, so the boundary is
/// enforced by the assembly reference graph — which is the strongest form available and does not
/// depend on a test remembering to look.
/// </para>
/// <para>
/// Everything genuinely shared still lives in the core: the broker configuration, the token cache, the
/// silent service, and the failure classifier. What is here is only the call the provider must never
/// make. A source-level test asserts this file is the single site of it.
/// </para>
/// <para>
/// <b>Silent first, then interactive.</b> Microsoft's WAM integration guidance is to attempt silent
/// acquisition and prompt only on failure, so a returning user who signs in again is not challenged
/// for credentials the broker already holds.
/// </para>
/// </remarks>
internal sealed class InteractiveAuthService
{
    private readonly IPublicClientApplication _client;
    private readonly SilentAuthService _silent;
    private readonly IOperationalLogger _logger;
    private readonly SelectedAccountStore _selectedAccounts;
    private readonly StateCommitCoordinator _commits;
    private readonly Func<string, IStateCommitAction> _publishSelection;

    /// <param name="client">The broker-enabled MSAL client.</param>
    /// <param name="commits">
    /// Performs the selection publication under the shared mutation mutex. Sign-in publishes through a
    /// commit rather than through the selection store directly, because the cached mailbox belongs to
    /// whichever account was previously selected and the two have to change together; see
    /// <c>CommitInteractiveSelectionAction</c>.
    /// </param>
    /// <param name="paths">Where coordination state lives.</param>
    /// <param name="cache">The committed mailbox snapshot, which publication may have to remove.</param>
    /// <param name="selectedAccounts">Where the chosen account is recorded.</param>
    /// <param name="logger">Metadata-free operational logging.</param>
    public InteractiveAuthService(
        IPublicClientApplication client,
        StateCommitCoordinator commits,
        CoordinationPaths paths,
        ProtectedCache cache,
        SelectedAccountStore selectedAccounts,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selectedAccounts);

        _client = client;
        _commits = commits;
        _selectedAccounts = selectedAccounts;
        _logger = logger ?? NullOperationalLogger.Instance;
        _silent = new SilentAuthService(client, _logger, selectedAccounts);
        _publishSelection = homeAccountId => new CommitInteractiveSelectionAction(
            paths,
            cache,
            selectedAccounts,
            homeAccountId,
            _logger);
    }

    /// <summary>
    /// Signs the user in, prompting only when a silent attempt cannot succeed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A broker-unavailable or approval-required silent outcome is returned as-is rather than escalated
    /// to a prompt. Neither is fixed by showing a dialog: the first is a machine problem, and the
    /// second needs an administrator. Prompting anyway would put the user through an authentication
    /// flow whose only possible ending is the same failure.
    /// </para>
    /// <para>
    /// The parent window is not passed here. It was supplied to the builder in
    /// <see cref="BrokerClient"/> as a delegate, which is what current documentation requires for
    /// broker use, and MSAL calls that delegate at acquisition time — by which point the companion
    /// window exists.
    /// </para>
    /// </remarks>
    public async Task<TokenAcquisitionResult> SignInAsync(CancellationToken cancellationToken = default)
    {
        TokenAcquisitionResult silent = await _silent.AcquireAsync(cancellationToken)
            .ConfigureAwait(false);

        // The silent shortcut requires a recorded selection, and that condition is the fix for a hole
        // the previous version's own comment denied. It claimed a retry would re-attempt a failed
        // write; it would not. With one account cached and the record missing, silent acquisition
        // succeeds and returned here — so the write was never reached again, the companion reported
        // Acquired with no selection on disk, and the state healed only by accident. Adding a second
        // account later then took the provider to interaction-required, having had a usable answer
        // available the whole time.
        //
        // Going interactive is the honest repair and the only one available. The alternative —
        // recording the single cached account without asking — writes a *guess* as though it were a
        // choice, and it would then be honoured as a choice once a second account exists. A prompt
        // asks the question whose answer is missing.
        if (silent.IsAcquired
            && _selectedAccounts.Read().Status == SelectedAccountStatus.Recorded)
        {
            return silent;
        }

        // Broker-unavailable and approval-required still return as-is: neither is fixed by a dialog,
        // and neither leaves anything to record.
        if (!silent.IsAcquired && !silent.IsResolvedBySigningIn)
        {
            return silent;
        }

        TokenAcquisitionResult acquired = await AcquireInteractivelyAsync(
                isSignIn: true,
                forceAccountSelection: false,
                cancellationToken)
            .ConfigureAwait(false);

        return acquired.IsAcquired && acquired.HomeAccountId is { Length: > 0 } homeAccountId
            ? Publish(acquired, homeAccountId)
            : acquired;
    }

    /// <summary>
    /// Commits the selected account, and the mailbox decision that goes with it, as one mutation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A failed publication fails the sign-in</b>, and that is not the obvious call — a token was
    /// genuinely issued. It is right because of what the rest of the system does with an unrecorded
    /// selection: silent acquisition refuses to guess when more than one account is cached, so an
    /// ignored failure leaves the provider deterministically at interaction-required while this process
    /// reports success. That is a sign-in the user is told worked and that can never converge.
    /// Reporting it as failed costs a retry, and the retry genuinely re-attempts the publication — see
    /// the silent-shortcut condition in <see cref="SignInAsync"/>, which is what makes that true rather
    /// than merely asserted.
    /// </para>
    /// <para>
    /// Nothing is lost by discarding the token: WAM holds the device-bound refresh token and MSAL's
    /// cache holds the account, so the next attempt acquires silently.
    /// </para>
    /// <para>
    /// <see cref="StateCommitCoordinator.CommitDisclosureChange"/> rather than
    /// <c>CommitRefresh</c>, for its two documented properties. It retries once on contention instead of
    /// abandoning, because unlike a refresh this has no next trigger to wait for; and it takes no
    /// cancellation token, because this is user-initiated and cancelling it on some ambient deadline is
    /// how a publication becomes a silent no-op.
    /// </para>
    /// </remarks>
    private TokenAcquisitionResult Publish(TokenAcquisitionResult acquired, string homeAccountId)
    {
        StateCommitResult commit = _commits.CommitDisclosureChange(
            _publishSelection(homeAccountId),
            OperationalEventId.SignInPublicationFailed);

        if (commit.IsCommitted)
        {
            return acquired;
        }

        LastFailure = SelectionNotRecorded;
        _logger.Record(OperationalEventId.SignInPublicationFailed, OperationalOutcome.Failed);

        return TokenAcquisitionResult.Unavailable(TokenAcquisitionStatus.Failed);
    }

    /// <summary>
    /// Always displays WAM's account picker and returns the selected identifier without publishing
    /// it. The account-switch coordinator owns publication because it must clear the prior mailbox
    /// snapshot and replace the selection together under the mutation mutex.
    /// </summary>
    public Task<TokenAcquisitionResult> SelectAccountAsync(
        CancellationToken cancellationToken = default) =>
        AcquireInteractivelyAsync(
            isSignIn: false,
            forceAccountSelection: true,
            cancellationToken);

    /// <summary>
    /// Performs the interactive acquisition and nothing else. Publication is the caller's, because the
    /// two callers publish differently: sign-in through <see cref="Publish"/>, and account switching
    /// through the coordinator that also owns its suppression ordering.
    /// </summary>
    /// <param name="isSignIn">
    /// Whether to record this as a sign-in in the operational log. False for account selection, whose
    /// own coordinator records the switch events.
    /// </param>
    private async Task<TokenAcquisitionResult> AcquireInteractivelyAsync(
        bool isSignIn,
        bool forceAccountSelection,
        CancellationToken cancellationToken)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            AcquireTokenInteractiveParameterBuilder request =
                _client.AcquireTokenInteractive(AuthenticationOptions.Scopes);

            if (forceAccountSelection)
            {
                request = request.WithPrompt(Prompt.SelectAccount);
            }

            AuthenticationResult result = await request.ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            // An account the broker will not identify cannot be published, and publishing is what makes
            // the provider able to ask for the right mailbox. Refused here rather than downstream,
            // because every publication path requires an identifier and none of them can invent one.
            if (result.Account?.HomeAccountId?.Identifier is not { Length: > 0 } homeAccountId)
            {
                LastFailure = SelectionNotRecorded;

                if (isSignIn)
                {
                    _logger.Record(
                        OperationalEventId.SignInCompleted,
                        OperationalOutcome.Failed,
                        System.Diagnostics.Stopwatch.GetElapsedTime(started));
                }

                return TokenAcquisitionResult.Unavailable(TokenAcquisitionStatus.Failed);
            }

            if (isSignIn)
            {
                _logger.Record(
                    OperationalEventId.SignInCompleted,
                    OperationalOutcome.Success,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }

            return TokenAcquisitionResult.Acquired(
                result.AccessToken,
                result.ExpiresOn,
                homeAccountId);
        }
        catch (Exception e) when (e is MsalException or OperationCanceledException)
        {
            TokenAcquisitionStatus status = AuthenticationFailures.Classify(
                e,
                AuthenticationPhase.Interactive);

            // Captured for the companion's own diagnostic line, not logged. Categories only — see
            // AuthenticationFailures.Describe.
            LastFailure = AuthenticationFailures.Describe(e);

            if (isSignIn)
            {
                _logger.Record(
                    OperationalEventId.SignInCompleted,
                    AuthenticationFailures.ToOutcome(status),
                    System.Diagnostics.Stopwatch.GetElapsedTime(started));
            }

            return TokenAcquisitionResult.Unavailable(status);
        }
    }

    /// <summary>
    /// A bounded description of the last interactive failure, or <see langword="null"/> if none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Exists because a status word alone proved insufficient to diagnose a real failure: a tenant
    /// consent block arrived carrying none of the <c>AADSTS</c> codes the classifier knew about, and it
    /// reported as a generic failure with no way to discover which signal it actually carried.
    /// </para>
    /// <para>
    /// Companion-only, and never routed through the operational logger, whose API deliberately has
    /// nowhere to put a string. The provider cannot reach this type at all.
    /// </para>
    /// </remarks>
    public string? LastFailure { get; private set; }

    /// <summary>
    /// Why a sign-in that acquired a token is still reported as failed.
    /// </summary>
    /// <remarks>
    /// A fixed category, not a message. There is no exception to describe here — the failure is that the
    /// local publication did not commit — and the same rule applies as everywhere else in this file: the
    /// companion may show a category, never a path, an account, or an exception's own text.
    /// </remarks>
    internal const string SelectionNotRecorded = "SelectedAccountNotRecorded";
}
