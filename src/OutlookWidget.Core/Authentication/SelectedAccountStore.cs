using System.Security.Cryptography;
using System.Text.Json;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Authentication;

/// <summary>Why a read of the recorded account selection ended as it did.</summary>
public enum SelectedAccountStatus
{
    /// <summary>
    /// Nothing has been recorded for this registration. A fresh install, or state written before the
    /// selection existed. The caller may fall back to its prior behaviour.
    /// </summary>
    Absent,

    /// <summary>A selection was read and is usable.</summary>
    Recorded,

    /// <summary>
    /// A record exists and could not be trusted — unreadable, malformed, or carrying no identifier.
    /// <b>The caller must not fall back.</b> See <see cref="SelectedAccountStore.Read"/> for why this
    /// is not folded into <see cref="Absent"/>.
    /// </summary>
    Unreadable,
}

/// <summary>The outcome of one selection read.</summary>
/// <param name="Status">Why the read ended as it did.</param>
/// <param name="HomeAccountId">
/// The identifier, present only on <see cref="SelectedAccountStatus.Recorded"/>.
/// </param>
public readonly record struct SelectedAccountResult(
    SelectedAccountStatus Status,
    string? HomeAccountId);

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
/// <b>What it holds:</b> an opaque MSAL home-account identifier — a directory object ID and a tenant
/// ID — plus the registration it is scoped to. There is no mailbox content, no user principal name,
/// no display name, and no token.
/// </para>
/// <para>
/// <b>DPAPI-protected, because section 4 step 6 requires it.</b> An earlier version of this file was
/// not, and argued that an opaque directory object ID needs no protection — which is probably true and
/// is beside the point. The plan is the approved source of truth and states that the selected
/// home-account and tenant identifiers are recorded in DPAPI-protected state, so a deviation would
/// have needed an approved scope decision rather than a paragraph of reasoning in a code comment. The
/// protection costs a few lines and removes a contradiction between two sources that both govern this
/// file. It is inside the package store, so uninstall removes it either way.
/// </para>
/// </remarks>
public sealed class SelectedAccountStore
{
    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Additional DPAPI entropy, distinct from the snapshot's.
    /// </summary>
    /// <remarks>
    /// Not a secret — it ships in the binary — but it binds the ciphertext to this specific use, so a
    /// blob from elsewhere in the user's profile cannot be substituted and silently unprotected here.
    /// Deliberately not the same value <c>ProtectedCache</c> uses: sharing it would make a snapshot and
    /// a selection record interchangeable to the decryptor.
    /// </remarks>
    private static readonly byte[] Entropy = "OutlookWidget.SelectedAccount.v1"u8.ToArray();

    private readonly CoordinationPaths _paths;
    private readonly AuthenticationOptions _options;
    private readonly IOperationalLogger _logger;
    private readonly IDataProtector _protector;

    public SelectedAccountStore(
        CoordinationPaths paths,
        AuthenticationOptions options,
        IOperationalLogger? logger = null)
        : this(paths, options, logger, CurrentUserDataProtector.Instance)
    {
    }

    internal SelectedAccountStore(
        CoordinationPaths paths,
        AuthenticationOptions options,
        IOperationalLogger? logger,
        IDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(protector);

        _paths = paths;
        _options = options;
        _logger = logger ?? NullOperationalLogger.Instance;
        _protector = protector;
    }

    /// <summary>
    /// Records the account an interactive sign-in selected.
    /// </summary>
    /// <returns>
    /// Whether the selection was persisted, and the caller must not ignore it.
    /// <see langword="false"/> leaves exactly what a fresh install leaves, so nothing downstream can
    /// tell the two apart after the fact. Two defences, and both are needed:
    /// <c>SilentAuthService.Select</c> refuses to guess whenever no selection is present and more than
    /// one account is cached, which also covers state written before this record existed; and the
    /// companion reports the sign-in as failed rather than succeeding into a state that can never
    /// converge.
    /// </returns>
    public bool Write(string homeAccountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(homeAccountId);

        try
        {
            // Selection writes participate in the same mutex as snapshot commits. A refresh commit
            // re-reads this record while holding that mutex; without the matching writer lock, an
            // account switch could land between that read and the cache replace and still allow the
            // previous account's Graph result to commit.
            using var mutex = new MutationMutex(_paths.MutationMutexName, _logger);
            using MutationLock heldLock = mutex.Acquire();

            if (!heldLock.IsHeld)
            {
                _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Timeout);
                return false;
            }

            Directory.CreateDirectory(_paths.RootDirectory);

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(
                new AccountRecord
                {
                    HomeAccountId = homeAccountId,
                    TenantId = _options.TenantId,
                    ClientId = _options.ClientId,
                });

            File.WriteAllBytes(_paths.SelectedAccountFilePath, _protector.Protect(json, Entropy));
            return true;
        }
        catch (Exception e) when (e is IOException
                                     or UnauthorizedAccessException
                                     or CryptographicException)
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return false;
        }
    }

    /// <summary>
    /// Reads the recorded selection, distinguishing "there is none" from "it could not be read".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Absent and unreadable are deliberately <em>not</em> the same answer, and an earlier version
    /// of this method said they were by analogy to <c>AuthorizationStateStore</c>.</b> That analogy
    /// was wrong, because the two stores fail in opposite directions. There, wrongly claiming
    /// approval-required asserts an administrator is needed and withdraws a retry the user may simply
    /// want, so an unreadable record must yield no refinement. Here, treating an unreadable record as
    /// absent sends the caller down the first-cached-account fallback — which on a machine with more
    /// than one account reads a <em>different mailbox</em> and looks exactly like success.
    /// </para>
    /// <para>
    /// So this one fails closed, which is the direction invariant 5 requires whenever the harm is
    /// disclosure rather than inconvenience. A corrupt or transiently unreadable file costs a sign-in
    /// prompt; the alternative costs the wrong person's mail on screen.
    /// </para>
    /// <para>
    /// A record written for a <em>different</em> registration is genuinely
    /// <see cref="SelectedAccountStatus.Absent"/> rather than unreadable — it is a perfectly good
    /// record about something else, and this registration has no selection. It is not deleted: state
    /// lives under package identity, which does not change when <c>authentication.json</c> is
    /// repointed, and a read has no business mutating state it may not own. A record that <em>is</em>
    /// ours and carries no identifier is malformed, so it fails closed like any other unreadable one.
    /// </para>
    /// </remarks>
    public SelectedAccountResult Read()
    {
        try
        {
            byte[] json = _protector.Unprotect(
                File.ReadAllBytes(_paths.SelectedAccountFilePath),
                Entropy);

            AccountRecord? record = JsonSerializer.Deserialize<AccountRecord>(json, ReadOptions);

            if (record is null)
            {
                return new SelectedAccountResult(SelectedAccountStatus.Unreadable, null);
            }

            if (record.TenantId != _options.TenantId || record.ClientId != _options.ClientId)
            {
                return new SelectedAccountResult(SelectedAccountStatus.Absent, null);
            }

            return string.IsNullOrWhiteSpace(record.HomeAccountId)
                ? new SelectedAccountResult(SelectedAccountStatus.Unreadable, null)
                : new SelectedAccountResult(SelectedAccountStatus.Recorded, record.HomeAccountId);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // The only genuinely absent case: nothing has ever been recorded here. Separated from the
            // catch below because that one is the fail-closed path and this one is a fresh install.
            return new SelectedAccountResult(SelectedAccountStatus.Absent, null);
        }
        catch (Exception e) when (e is IOException
                                     or UnauthorizedAccessException
                                     or JsonException
                                     or ArgumentException
                                     or CryptographicException)
        {
            // CryptographicException belongs here rather than anywhere gentler: a blob that will not
            // unprotect is a record that exists and cannot be trusted, which is precisely the
            // fail-closed case.
            _logger.Record(OperationalEventId.CacheReadFailed, OperationalOutcome.Failed);
            return new SelectedAccountResult(SelectedAccountStatus.Unreadable, null);
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
