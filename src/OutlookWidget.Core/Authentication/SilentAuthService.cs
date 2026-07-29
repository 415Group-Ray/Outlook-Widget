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

        try
        {
            IAccount account = await FindAccountAsync().ConfigureAwait(false);

            AuthenticationResult result = await _client
                .AcquireTokenSilent(AuthenticationOptions.Scopes, account)
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);

            _logger.Record(
                OperationalEventId.SilentTokenAcquired,
                OperationalOutcome.Success,
                System.Diagnostics.Stopwatch.GetElapsedTime(started));

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
                System.Diagnostics.Stopwatch.GetElapsedTime(started));

            return TokenAcquisitionResult.Unavailable(status);
        }
    }

    /// <summary>
    /// The account to attempt silent acquisition for.
    /// </summary>
    /// <remarks>
    /// <c>GetAccountsAsync</c> can itself fail when the broker is unavailable, and that failure is
    /// deliberately not caught here. It propagates to <see cref="AcquireAsync"/>, whose handler
    /// classifies it as broker-unavailable. Catching it here and returning "no account cached" would
    /// treat a broken broker as an empty cache and produce an interaction-required card for a problem
    /// that signing in cannot fix.
    /// </remarks>
    private async Task<IAccount> FindAccountAsync()
    {
        IEnumerable<IAccount> accounts = await _client.GetAccountsAsync().ConfigureAwait(false);

        return accounts.FirstOrDefault() ?? PublicClientApplication.OperatingSystemAccount;
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
