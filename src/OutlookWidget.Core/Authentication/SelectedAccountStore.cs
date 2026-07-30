using System.Text.Json;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Authentication;

/// <summary>
/// Records which account the user actually chose, so silent acquisition stops guessing.
/// </summary>
/// <remarks>
/// <para>
/// <b>This closes a limitation the plan calls a prerequisite for gate 10 rather than cleanup.</b>
/// Without it <see cref="SilentAuthService"/> selects the <em>first</em> cached account, and MSAL
/// guarantees no ordering. With one account that is correct, and v1 is one user with one mailbox. With
/// more than one it is arbitrary — the provider could retry a stale account and stay at
/// interaction-required while the companion reported success, and once mail is being read the same
/// ambiguity reads the wrong mailbox. Reading mail for an arbitrarily chosen account is worse than
/// not reading it.
/// </para>
/// <para>
/// <b>Its own file, not the snapshot and not the authorization record.</b> The snapshot is DPAPI
/// state that logout and account switch clear, and this value has to survive that: silent acquisition
/// needs to know which account to ask for <em>before</em> any refresh has ever succeeded, which is
/// exactly the fresh-install and post-logout case. The authorization record answers a question about
/// consent, and a file that answers two unrelated questions is one that gets cleared for the wrong
/// reason.
/// </para>
/// <para>
/// <b>Deliberately not DPAPI-protected, and the distinction from the snapshot is the point.</b> This
/// holds an opaque MSAL home-account identifier — a directory object ID and a tenant ID — plus the
/// registration it is scoped to. There is no mailbox content, no user principal name, no display
/// name, and no token. The snapshot protects the same identifier only because it rides alongside
/// senders and subject lines, which is the thing that needed protecting. Encrypting this file would
/// imply a protection requirement that does not exist and tell a later reader that something
/// sensitive lives here. It is inside the package store, so uninstall removes it.
/// </para>
/// </remarks>
public sealed class SelectedAccountStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly CoordinationPaths _paths;
    private readonly AuthenticationOptions _options;
    private readonly IOperationalLogger _logger;

    public SelectedAccountStore(
        CoordinationPaths paths,
        AuthenticationOptions options,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        _paths = paths;
        _options = options;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>
    /// Records the account an interactive sign-in selected.
    /// </summary>
    /// <remarks>
    /// A write failure is swallowed into an operational category rather than failing the sign-in the
    /// user just completed. The cost of losing it is that silent acquisition falls back to the
    /// first-account behaviour, which is the state the product was already in — worse, not wrong.
    /// </remarks>
    public void Write(string homeAccountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeAccountId);

        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);

            string json = JsonSerializer.Serialize(
                new AccountRecord
                {
                    HomeAccountId = homeAccountId,
                    TenantId = _options.TenantId,
                    ClientId = _options.ClientId,
                });

            File.WriteAllText(_paths.SelectedAccountFilePath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
        }
    }

    /// <summary>
    /// The recorded identifier, or <see langword="null"/> when there is none for this registration.
    /// </summary>
    /// <remarks>
    /// <b>Absent and unreadable are the same answer</b>, as they are for the authorization record and
    /// for the same reason: the caller's fallback is the behaviour the product already had, so a
    /// transient read failure degrades rather than breaks. A record written for a <em>different</em>
    /// registration is treated as absent, not deleted — state lives under package identity, which does
    /// not change when <c>authentication.json</c> is repointed, and a read has no business mutating
    /// state it may not own.
    /// </remarks>
    public string? TryRead()
    {
        try
        {
            AccountRecord? record = JsonSerializer.Deserialize<AccountRecord>(
                File.ReadAllText(_paths.SelectedAccountFilePath),
                ReadOptions);

            if (record is null
                || record.TenantId != _options.TenantId
                || record.ClientId != _options.ClientId)
            {
                return null;
            }

            return string.IsNullOrWhiteSpace(record.HomeAccountId) ? null : record.HomeAccountId;
        }
        catch (Exception e) when (e is IOException
                                     or UnauthorizedAccessException
                                     or JsonException
                                     or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Removes the record. Safe when none exists.</summary>
    /// <remarks>
    /// Belongs to logout and account switch, which are not built yet. It exists now because a store
    /// that can be written and never cleared is one whose removal gets forgotten at exactly the moment
    /// it matters — a logout that leaves the previous account recorded would have the provider keep
    /// asking for a mailbox the user just signed out of.
    /// </remarks>
    public void Clear()
    {
        try
        {
            File.Delete(_paths.SelectedAccountFilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
        }
    }

    private sealed class AccountRecord
    {
        public string? HomeAccountId { get; init; }

        /// <summary>
        /// The registration this selection belongs to. Default (empty) on a record written before
        /// these fields existed, which therefore matches no real registration and is ignored.
        /// </summary>
        public Guid TenantId { get; init; }

        public Guid ClientId { get; init; }
    }
}
