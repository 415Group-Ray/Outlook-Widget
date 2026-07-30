using Microsoft.Identity.Client;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Diagnostics;

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

    public InteractiveAuthService(
        IPublicClientApplication client,
        SelectedAccountStore selectedAccounts,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(selectedAccounts);

        _client = client;
        _selectedAccounts = selectedAccounts;
        _logger = logger ?? NullOperationalLogger.Instance;
        _silent = new SilentAuthService(client, _logger, selectedAccounts);
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

        if (silent.IsAcquired || !silent.IsResolvedBySigningIn)
        {
            return silent;
        }

        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        try
        {
            AuthenticationResult result = await _client
                .AcquireTokenInteractive(AuthenticationOptions.Scopes)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            // The one moment the product knows which account the user picked, rather than inferring
            // it. Recorded before the result is returned, because the provider may re-probe on the
            // state-changed signal the caller raises immediately afterwards and would otherwise read a
            // file that is not there yet.
            //
            // **A failed write fails the sign-in**, and that is not the obvious call — a token was
            // genuinely issued. It is right because of what the rest of the system now does with an
            // unrecorded selection: silent acquisition refuses to guess when more than one account is
            // cached, so an ignored write failure leaves the provider deterministically at
            // interaction-required while this process reports success. That is a sign-in the user is
            // told worked and that can never converge. Reporting it as failed costs a retry, which is
            // also the remedy, since retrying re-attempts the write.
            //
            // Nothing is lost by discarding the token: WAM holds the device-bound refresh token and
            // MSAL's cache holds the account, so the next attempt acquires silently.
            if (result.Account?.HomeAccountId?.Identifier is not { Length: > 0 } homeAccountId
                || !_selectedAccounts.Write(homeAccountId))
            {
                LastFailure = SelectionNotRecorded;

                _logger.Record(
                    OperationalEventId.SignInCompleted,
                    OperationalOutcome.Failed,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started));

                return TokenAcquisitionResult.Unavailable(TokenAcquisitionStatus.Failed);
            }

            _logger.Record(
                OperationalEventId.SignInCompleted,
                OperationalOutcome.Success,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));

            return TokenAcquisitionResult.Acquired(result.AccessToken, result.ExpiresOn);
        }
        catch (Exception e) when (e is MsalException or OperationCanceledException)
        {
            TokenAcquisitionStatus status = AuthenticationFailures.Classify(
                e,
                AuthenticationPhase.Interactive);

            // Captured for the companion's own diagnostic line, not logged. Categories only — see
            // AuthenticationFailures.Describe.
            LastFailure = AuthenticationFailures.Describe(e);

            _logger.Record(
                OperationalEventId.SignInCompleted,
                AuthenticationFailures.ToOutcome(status),
                System.Diagnostics.Stopwatch.GetElapsedTime(started));

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
    /// A fixed category, not a message. There is no exception to describe here — the failure is that a
    /// local write did not happen — and the same rule applies as everywhere else in this file: the
    /// companion may show a category, never a path, an account, or an exception's own text.
    /// </remarks>
    internal const string SelectionNotRecorded = "SelectedAccountNotRecorded";
}
