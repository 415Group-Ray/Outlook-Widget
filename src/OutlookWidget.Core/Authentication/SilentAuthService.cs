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
    private readonly SelectedAccountStore? _selectedAccounts;

    /// <param name="client">The broker-enabled MSAL client.</param>
    /// <param name="logger">Metadata-free operational logging.</param>
    /// <param name="selectedAccounts">
    /// Where the account the user actually chose is recorded. Optional so the type stays constructible
    /// without configuration, and supplied at every real call site: without it this falls back to
    /// selecting the first cached account, which is correct only on a machine with exactly one.
    /// </param>
    public SilentAuthService(
        IPublicClientApplication client,
        IOperationalLogger? logger = null,
        SelectedAccountStore? selectedAccounts = null)
    {
        ArgumentNullException.ThrowIfNull(client);

        _client = client;
        _logger = logger ?? NullOperationalLogger.Instance;
        _selectedAccounts = selectedAccounts;
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
            AccountSelection selection = await FindAccountAsync().ConfigureAwait(false);
            cachedAccounts = selection.CachedCount;

            if (selection.Account is not { } account)
            {
                // The recorded account is not in the cache. Falling back to another one is the exact
                // failure this selection exists to prevent, and it would be silent, so this refuses
                // instead: the remedy is a fresh interactive sign-in, which is what
                // InteractionRequired means.
                _logger.Record(
                    OperationalEventId.SilentTokenUiRequired,
                    OperationalOutcome.Failed,
                    System.Diagnostics.Stopwatch.GetElapsedTime(started),
                    recordCount: cachedAccounts);

                return TokenAcquisitionResult.Unavailable(TokenAcquisitionStatus.InteractionRequired);
            }

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

    /// <summary>The account to attempt silent acquisition for, and how many the cache held.</summary>
    /// <param name="Account">
    /// The account to ask for, or <see langword="null"/> when a selection was recorded and no cached
    /// account matches it. Null is a refusal, not an absence — see <see cref="FindAccountAsync"/>.
    /// </param>
    /// <param name="CachedCount">How many accounts the cache held, for the operational record.</param>
    private readonly record struct AccountSelection(IAccount? Account, int CachedCount);

    /// <summary>
    /// The account to attempt silent acquisition for.
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
    /// <b>Three cases, and the middle one is the whole reason this is not a one-liner.</b> When the
    /// companion has recorded which account the user chose, that account is the only acceptable answer:
    /// if it is present, use it; if it is <em>not</em>, return null so the caller reports
    /// interaction-required. Falling back to another cached account there would silently read a
    /// different mailbox, which is the failure the recorded selection exists to prevent, and it would
    /// look exactly like success.
    /// </para>
    /// <para>
    /// When nothing is recorded — a fresh install, or state written before the selection existed — the
    /// prior behaviour stands: first cached account, then
    /// <see cref="PublicClientApplication.OperatingSystemAccount"/>, which Microsoft recommends as the
    /// fallback and which is a real convenience on a domain-joined device. It remains a guess about
    /// intent rather than a recorded choice, which is why it is now the last resort rather than the
    /// normal path. The count is still returned and logged: a <c>recordCount</c> above 1 with no
    /// recorded selection identifies a machine where the guess is live.
    /// </para>
    /// </remarks>
    private async Task<AccountSelection> FindAccountAsync()
    {
        List<IAccount> accounts =
            (await _client.GetAccountsAsync().ConfigureAwait(false)).ToList();

        // No store configured at all is the same as no record: the fallback behaviour, which is what
        // a caller that never supplied one is asking for.
        SelectedAccountResult selection = _selectedAccounts?.Read()
                                          ?? new SelectedAccountResult(SelectedAccountStatus.Absent, null);

        return new AccountSelection(Select(accounts, selection), accounts.Count);
    }

    /// <summary>
    /// The selection rule itself, separated from the MSAL call so it can be tested directly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Pure and synchronous on purpose. Stubbing <see cref="IPublicClientApplication"/> to reach this
    /// logic would mean implementing a large interface to exercise a handful of lines, and the
    /// resulting test would be mostly stub. <see cref="IAccount"/> is three properties, so the rule is
    /// tested against real inputs instead.
    /// </para>
    /// <para>
    /// <b>Only <see cref="SelectedAccountStatus.Absent"/> permits the fallback.</b> That is the whole
    /// shape of this method: a recorded selection must be honoured exactly, and an
    /// <see cref="SelectedAccountStatus.Unreadable"/> record must not be downgraded into "no
    /// preference" — a corrupt or transiently unreadable file would otherwise send a multi-account
    /// machine to whichever account MSAL enumerates first and render a different mailbox.
    /// </para>
    /// </remarks>
    internal static IAccount? Select(IReadOnlyList<IAccount> accounts, SelectedAccountResult selection)
    {
        ArgumentNullException.ThrowIfNull(accounts);

        // Fails closed. The record exists and cannot be trusted, so there is no account this may
        // safely ask for; null becomes interaction-required in AcquireAsync.
        if (selection.Status == SelectedAccountStatus.Unreadable)
        {
            return null;
        }

        if (selection is { Status: SelectedAccountStatus.Recorded, HomeAccountId: { Length: > 0 } selectedId })
        {
            // No fallback on a miss either. Returning another account here would read a different
            // mailbox and look exactly like success.
            foreach (IAccount candidate in accounts)
            {
                if (string.Equals(
                        candidate.HomeAccountId?.Identifier,
                        selectedId,
                        StringComparison.Ordinal))
                {
                    return candidate;
                }
            }

            return null;
        }

        return accounts.Count > 0 ? accounts[0] : PublicClientApplication.OperatingSystemAccount;
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
