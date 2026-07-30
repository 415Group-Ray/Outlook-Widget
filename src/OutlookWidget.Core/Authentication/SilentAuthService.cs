using Microsoft.Identity.Client;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Authentication;

/// <summary>
/// Silent-only token acquisition. The provider's sole authentication capability.
/// </summary>
/// <remarks>
/// <para>
/// <b>Silent-only is a capability boundary, not a policy the caller opts into.</b> This type exposes
/// one method and it cannot show UI. Section 7 requires the provider to fail closed and never invoke
/// an interactive API, and the enforcement is layered: MSAL falls back to a browser only on
/// interactive acquisition, this type never performs one, the interactive API does not appear
/// anywhere in the core, and source-level checks fail the build if it ever does. A provider holding
/// only this type has nothing to call that could open a window.
/// </para>
/// <para>
/// <b>It never throws for an authentication reason.</b> Every MSAL failure becomes a
/// <see cref="TokenAcquisitionResult"/>, because this runs in a background COM server the user did not
/// start: an escaping exception would kill the provider and leave the host displaying whatever it last
/// cached, with no card explaining why. Argument and programming errors still throw.
/// </para>
/// <para>
/// The companion uses this too, and first. Microsoft's integration guidance is to attempt silent
/// acquisition before any interactive prompt, so the companion's flow is this service followed by its
/// own interactive service only when this reports interaction is required.
/// </para>
/// </remarks>
public sealed class SilentAuthService
{
    private readonly IPublicClientApplication _client;
    private readonly IOperationalLogger _logger;

    public SilentAuthService(IPublicClientApplication client, IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>
    /// Attempts to acquire an access token without any user interaction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Two account sources, in the documented order.</b> The cached account comes first, because it
    /// is the account the user actually chose in the companion. Only if none is cached does this fall
    /// back to <see cref="PublicClientApplication.OperatingSystemAccount"/>, the account the machine is
    /// signed in to — which Microsoft recommends as the fallback and which is a real convenience on a
    /// domain-joined device, but is a guess about intent rather than a recorded choice. If the user
    /// signs the widget into a different mailbox than their Windows account, the cached account is the
    /// correct one and the fallback would silently show the wrong mailbox, so the order matters.
    /// </para>
    /// <para>
    /// The cached account is only discoverable because the shared token cache is attached in
    /// <see cref="BrokerClient"/>. Without it this method would fall through to the operating-system
    /// account on every provider start regardless of what the companion had done.
    /// </para>
    /// </remarks>
    public async Task<TokenAcquisitionResult> AcquireAsync(
        CancellationToken cancellationToken = default)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();

        // Declared outside the try so the failure path can report it too: the count is most useful
        // exactly when acquisition failed, because that is when selecting the wrong account is the
        // likeliest explanation.
        int cachedAccounts = 0;

        try
        {
            (IAccount account, cachedAccounts) = await FindAccountAsync().ConfigureAwait(false);

            AuthenticationResult result = await _client
                .AcquireTokenSilent(AuthenticationOptions.Scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.Record(
                OperationalEventId.SilentTokenAcquired,
                OperationalOutcome.Success,
                System.Diagnostics.Stopwatch.GetElapsedTime(started),
                recordCount: cachedAccounts);

            return TokenAcquisitionResult.Acquired(result.AccessToken, result.ExpiresOn);
        }
        catch (Exception e) when (e is MsalException or OperationCanceledException)
        {
            TokenAcquisitionStatus status = AuthenticationFailures.Classify(
                e,
                AuthenticationPhase.Silent);

            _logger.Record(
                EventFor(status),
                AuthenticationFailures.ToOutcome(status),
                System.Diagnostics.Stopwatch.GetElapsedTime(started),
                recordCount: cachedAccounts);

            return TokenAcquisitionResult.Unavailable(status);
        }
    }

    /// <summary>
    /// The account to attempt silent acquisition for, and how many the cache held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>GetAccountsAsync</c> can itself fail when the broker is unavailable, and that failure is
    /// deliberately not caught here. It propagates to <see cref="AcquireAsync"/>, whose handler
    /// classifies it as broker-unavailable. Catching it here and returning "no account cached" would
    /// treat a broken broker as an empty cache and produce an interaction-required card for a problem
    /// that signing in cannot fix.
    /// </para>
    /// <para>
    /// <b>Selecting the first cached account is provisional, and it is a known limitation rather than a
    /// considered choice.</b> With one account — the v1 shape, single user and single mailbox — it is
    /// correct. With more than one it is arbitrary: MSAL guarantees no ordering, so this can pick an
    /// account other than the one an interactive sign-in just selected. The companion would then report
    /// <c>Acquired</c> while the provider retried a stale account and stayed at interaction-required, and
    /// once Graph exists the same ambiguity could read the wrong mailbox.
    /// </para>
    /// <para>
    /// The fix is the one section 4 already specifies — record the selected MSAL home-account identifier
    /// and acquire for that account — and its home is the account-lifecycle work that also brings logout
    /// and account switching, alongside the state those need. Adding a fourth ad-hoc state file for it
    /// here would have to be unpicked then. So the count is returned and logged instead: a
    /// <c>recordCount</c> above 1 is the signal that this limitation is live on a machine, which turns a
    /// silent wrong-account failure into a diagnosable one.
    /// </para>
    /// </remarks>
    private async Task<(IAccount Account, int CachedCount)> FindAccountAsync()
    {
        List<IAccount> accounts =
            (await _client.GetAccountsAsync().ConfigureAwait(false)).ToList();

        IAccount account = accounts.Count > 0
            ? accounts[0]
            : PublicClientApplication.OperatingSystemAccount;

        return (account, accounts.Count);
    }

    /// <summary>
    /// The operational event one status is recorded as.
    /// </summary>
    /// <remarks>
    /// The event enum already distinguishes the broker-unavailable case from the UI-required case,
    /// which is what makes the two diagnosable apart in the log without recording anything about the
    /// account. Statuses with no dedicated event are recorded as UI-required with a non-success
    /// outcome, because that is what they mean to a caller: no token, this pass.
    /// </remarks>
    private static OperationalEventId EventFor(TokenAcquisitionStatus status) =>
        status == TokenAcquisitionStatus.BrokerUnavailable
            ? OperationalEventId.SilentTokenBrokerUnavailable
            : OperationalEventId.SilentTokenUiRequired;
}
