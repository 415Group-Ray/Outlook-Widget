using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Models;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// One synchronous state mutation, performed while the mutation mutex is held.
/// </summary>
/// <remarks>
/// An interface rather than a delegate because <see cref="MutationLock"/> is a
/// <c>ref struct</c> and cannot be a lambda parameter. That restriction is welcome: it
/// means every commit action is a named type whose body is visibly synchronous, rather
/// than an inline closure that a later edit could quietly make <c>async</c>.
/// </remarks>
public interface IStateCommitAction
{
    /// <summary>
    /// Performs the mutation. Must be entirely synchronous: no <c>await</c>, no
    /// <c>Task.Run</c>, no scheduler hop.
    /// </summary>
    CacheCommitResult Execute(in MutationLock heldLock);
}

/// <summary>Commits a protected snapshot, conditional on the generation not having moved.</summary>
public sealed class CommitSnapshotAction(ProtectedCache cache, byte[] payload, long? expectedGeneration)
    : IStateCommitAction
{
    public CacheCommitResult Execute(in MutationLock heldLock) =>
        cache.Commit(heldLock, payload, expectedGeneration);
}

/// <summary>
/// Commits a mailbox snapshot only while the account selected by the companion still matches the
/// account whose token produced it.
/// </summary>
public sealed class CommitMailboxSnapshotAction : IStateCommitAction
{
    private readonly ProtectedCache _cache;
    private readonly byte[] _payload;
    private readonly long? _expectedGeneration;
    private readonly SelectedAccountStore _selectedAccounts;
    private readonly string _expectedHomeAccountId;
    private readonly IOperationalLogger _logger;

    public CommitMailboxSnapshotAction(
        ProtectedCache cache,
        byte[] payload,
        long? expectedGeneration,
        SelectedAccountStore selectedAccounts,
        string expectedHomeAccountId,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentNullException.ThrowIfNull(selectedAccounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(expectedHomeAccountId);

        _cache = cache;
        _payload = payload;
        _expectedGeneration = expectedGeneration;
        _selectedAccounts = selectedAccounts;
        _expectedHomeAccountId = expectedHomeAccountId;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    public CacheCommitResult Execute(in MutationLock heldLock)
    {
        heldLock.ThrowIfNotHeld();

        SelectedAccountResult selected = _selectedAccounts.Read();

        if (selected.Status != SelectedAccountStatus.Recorded
            || !string.Equals(
                selected.HomeAccountId,
                _expectedHomeAccountId,
                StringComparison.Ordinal))
        {
            _logger.Record(
                OperationalEventId.RefreshDiscardedStateChanged,
                OperationalOutcome.Discarded);

            return new CacheCommitResult(
                CacheCommitStatus.GenerationMismatch,
                _cache.ReadGeneration());
        }

        return _cache.Commit(heldLock, _payload, _expectedGeneration);
    }
}

/// <summary>
/// Clears protected state unconditionally, advancing the generation. Used by logout,
/// account switch, and explicit cache-clear, each of which is itself authoritative and so
/// must not be refused because a concurrent refresh moved the generation.
/// </summary>
public sealed class ClearStateAction(ProtectedCache cache) : IStateCommitAction
{
    public CacheCommitResult Execute(in MutationLock heldLock)
    {
        if (heldLock.StateIsSuspect)
        {
            // A process was killed mid-commit. Remove its orphaned temporary files before
            // writing, so a later commit cannot pick up a half-written temp file.
            cache.RemoveOrphanedTemporaryFiles(heldLock);
        }

        return cache.Clear(heldLock);
    }
}

/// <summary>
/// Commits the durable local signed-out state: no selected identifier, no stale authorization
/// outcome, and no mailbox snapshot. The selected identifier is replaced last: if authorization or
/// cache mutation fails, a retry can still target only the account the user chose instead of falling
/// back to removing every account in this application's MSAL cache. The coordinator publishes the
/// state-changed signal only after this whole action succeeds.
/// </summary>
public sealed class CommitSignedOutStateAction : IStateCommitAction
{
    private readonly CoordinationPaths _paths;
    private readonly ProtectedCache _cache;
    private readonly SelectedAccountStore _selectedAccounts;
    private readonly IOperationalLogger _logger;

    public CommitSignedOutStateAction(
        CoordinationPaths paths,
        ProtectedCache cache,
        SelectedAccountStore selectedAccounts,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selectedAccounts);

        _paths = paths;
        _cache = cache;
        _selectedAccounts = selectedAccounts;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    public CacheCommitResult Execute(in MutationLock heldLock)
    {
        heldLock.ThrowIfNotHeld();

        if (heldLock.StateIsSuspect)
        {
            _cache.RemoveOrphanedTemporaryFiles(heldLock);
        }

        if (!AuthorizationStateStore.TryClear(_paths, _logger))
        {
            return new CacheCommitResult(CacheCommitStatus.Failed, _cache.ReadGeneration());
        }

        CacheCommitResult cleared = _cache.Clear(heldLock);

        if (!cleared.IsSuccess)
        {
            return cleared;
        }

        return _selectedAccounts.MarkSignedOut(heldLock)
            ? cleared
            : new CacheCommitResult(CacheCommitStatus.Failed, cleared.Generation);
    }
}

/// <summary>
/// Commits the local account-switch boundary: stale authorization and prior-account mailbox state
/// are cleared before the newly selected identifier is published. The identifier is replaced last,
/// so a partial failure retains the prior complete selection for an explicit retry while the
/// disclosure tombstone keeps all message details hidden.
/// </summary>
public sealed class CommitAccountSwitchStateAction : IStateCommitAction
{
    private readonly CoordinationPaths _paths;
    private readonly ProtectedCache _cache;
    private readonly SelectedAccountStore _selectedAccounts;
    private readonly string _homeAccountId;
    private readonly IOperationalLogger _logger;

    public CommitAccountSwitchStateAction(
        CoordinationPaths paths,
        ProtectedCache cache,
        SelectedAccountStore selectedAccounts,
        string homeAccountId,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selectedAccounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeAccountId);

        _paths = paths;
        _cache = cache;
        _selectedAccounts = selectedAccounts;
        _homeAccountId = homeAccountId;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    public CacheCommitResult Execute(in MutationLock heldLock)
    {
        heldLock.ThrowIfNotHeld();

        if (heldLock.StateIsSuspect)
        {
            _cache.RemoveOrphanedTemporaryFiles(heldLock);
        }

        if (!AuthorizationStateStore.TryClear(_paths, _logger))
        {
            return new CacheCommitResult(CacheCommitStatus.Failed, _cache.ReadGeneration());
        }

        CacheCommitResult cleared = _cache.Clear(heldLock);

        if (!cleared.IsSuccess)
        {
            return cleared;
        }

        return _selectedAccounts.ReplaceSelection(heldLock, _homeAccountId)
            ? cleared
            : new CacheCommitResult(CacheCommitStatus.Failed, cleared.Generation);
    }
}

/// <summary>
/// Publishes the account an interactive sign-in selected, removing the cached mailbox first unless
/// that cache can be positively shown to hold nothing belonging to another account.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because replacing the selected identifier on its own is not safe, and that is
/// exactly what sign-in used to do.</b> The companion's sign-in path wrote the new identifier
/// through the selection store and touched nothing else, so a sign-in that landed on a different
/// account than the one already recorded left the previous account's snapshot committed and
/// unsuppressed. <c>CommitMailboxSnapshotAction</c> stops a <em>new</em> fetch for the prior account
/// from committing, and <c>ProviderRefreshWorker</c> correctly reports the mismatch as stale — but
/// neither removes the snapshot that is already there, and the provider's delivery pass renders
/// committed state rather than waiting for a refresh. The result was the previous mailbox on screen
/// under the new account's selection.
/// </para>
/// <para>
/// <b>The decision is made under the lock, not before it.</b> An unlocked "does the cache belong to
/// someone else" check could be answered before a refresh scoped to the <em>prior</em> selection
/// commits, and that refresh is legitimate right up to the moment the identifier changes. Reading the
/// cache inside the critical section is what makes "no snapshot for another account survives this
/// identifier change" true rather than likely.
/// </para>
/// <para>
/// <b>Why this needs no disclosure tombstone, unlike <see cref="CommitAccountSwitchStateAction"/>.</b>
/// Invariant 4 covers disclosure-<em>reducing</em> operations, and the distinction is intent rather
/// than mechanism. A logout means "stop showing this mailbox", so a failed commit must still hide it,
/// which is why suppression is published before the attempt. This operation means "use this account",
/// and the clear and the replacement happen together in one critical section — so a failure leaves the
/// complete prior state, in which the prior account's mail is the correct thing to show for the prior
/// account's selection. There is nothing to fail closed about. The account-switch path publishes
/// suppression for a different reason again: it covers the unbounded interactive picker, during which
/// the user has already declared they are leaving the current mailbox.
/// </para>
/// <para>
/// <b>Both mutations advance nothing when they are unnecessary.</b> Re-signing in to the account
/// already recorded keeps its snapshot, so an expired token does not blank the widget and the card does
/// not report a cache clear that did not happen.
/// </para>
/// </remarks>
public sealed class CommitInteractiveSelectionAction : IStateCommitAction
{
    private readonly CoordinationPaths _paths;
    private readonly ProtectedCache _cache;
    private readonly SelectedAccountStore _selectedAccounts;
    private readonly string _homeAccountId;
    private readonly IOperationalLogger _logger;

    public CommitInteractiveSelectionAction(
        CoordinationPaths paths,
        ProtectedCache cache,
        SelectedAccountStore selectedAccounts,
        string homeAccountId,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selectedAccounts);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeAccountId);

        _paths = paths;
        _cache = cache;
        _selectedAccounts = selectedAccounts;
        _homeAccountId = homeAccountId;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    public CacheCommitResult Execute(in MutationLock heldLock)
    {
        heldLock.ThrowIfNotHeld();

        if (heldLock.StateIsSuspect)
        {
            _cache.RemoveOrphanedTemporaryFiles(heldLock);
        }

        // A successful sign-in retires any approval-required record: consent that was refused before
        // has plainly stopped being refused. Cleared first, so a failure below cannot leave the record
        // gone and the identifier unchanged in the other order.
        if (!AuthorizationStateStore.TryClear(_paths, _logger))
        {
            return new CacheCommitResult(CacheCommitStatus.Failed, _cache.ReadGeneration());
        }

        if (!MayHoldForeignSnapshot())
        {
            long generation = _cache.ReadGeneration();

            // Nothing to remove, so the snapshot and its generation are left exactly as they are. The
            // identifier still replaces atomically, and a reader that compares generations correctly
            // concludes that committed mailbox state did not change.
            return _selectedAccounts.ReplaceSelection(heldLock, _homeAccountId)
                ? new CacheCommitResult(CacheCommitStatus.Success, generation)
                : new CacheCommitResult(CacheCommitStatus.Failed, generation);
        }

        CacheCommitResult cleared = _cache.Clear(heldLock);

        if (!cleared.IsSuccess)
        {
            return cleared;
        }

        // The identifier is replaced last for the reason it is everywhere else: a partial failure must
        // retain the prior complete selection so a retry stays scoped to one account.
        return _selectedAccounts.ReplaceSelection(heldLock, _homeAccountId)
            ? cleared
            : new CacheCommitResult(CacheCommitStatus.Failed, cleared.Generation);
    }

    /// <summary>
    /// Whether the committed cache might hold a snapshot for an account other than the one being
    /// published.
    /// </summary>
    /// <remarks>
    /// Answers <see langword="true"/> on every uncertainty, per invariant 5. Only
    /// <see cref="CacheReadStatus.Absent"/> and <see cref="CacheReadStatus.Cleared"/> positively
    /// establish that there is nothing to remove; a transiently unreadable file may perfectly well
    /// hold the previous account's mail and become readable again a moment later, so it is cleared
    /// rather than trusted. A payload that will not deserialise cannot be attributed to any account
    /// either, so it is treated as foreign — clearing an unusable snapshot costs a refetch of data
    /// that could not have been rendered anyway.
    /// </remarks>
    private bool MayHoldForeignSnapshot()
    {
        CacheReadResult read = _cache.Read();

        switch (read.Status)
        {
            case CacheReadStatus.Absent:
            case CacheReadStatus.Cleared:
                return false;

            case CacheReadStatus.Success when read.Payload is { } payload:
                MailboxSnapshot? snapshot = MailboxSnapshot.TryDeserialize(payload);

                return snapshot is null
                       || !string.Equals(
                           snapshot.HomeAccountId,
                           _homeAccountId,
                           StringComparison.Ordinal);

            default:
                return true;
        }
    }
}

/// <summary>How a bounded commit attempt ended.</summary>
public enum StateCommitOutcome
{
    /// <summary>Committed. The generation advanced and the state-changed event was signalled.</summary>
    Committed,

    /// <summary>
    /// Committed state moved while this operation's I/O was in flight, so the result was
    /// discarded. Not a failure: the correct outcome for a superseded refresh.
    /// </summary>
    Discarded,

    /// <summary>
    /// The bounded mutex wait elapsed on every permitted attempt. A peer is stuck inside a
    /// critical section.
    /// </summary>
    ContentionTimeout,

    /// <summary>
    /// The mutex was acquired but the commit action did not finish. Its own ordering must preserve
    /// enough identity for a safe retry; independently persisted components may already reflect the
    /// requested change, while the disclosure tombstone remains authoritative until recovery.
    /// </summary>
    CommitFailed,

    /// <summary>The deadline expired before the mutex was acquired. Nothing was owned.</summary>
    Cancelled,
}

/// <summary>The result of a bounded commit, including how many attempts it took.</summary>
public readonly record struct StateCommitResult(
    StateCommitOutcome Outcome,
    long Generation,
    int Attempts)
{
    public bool IsCommitted => Outcome == StateCommitOutcome.Committed;
}

/// <summary>
/// Performs state commits under the bounded mutation mutex, with the per-caller timeout
/// behaviour the plan requires.
/// </summary>
/// <remarks>
/// <para>
/// The difference between the two entry points is the whole point of this type, and it is a
/// difference in kind rather than in degree.
/// </para>
/// <para>
/// <see cref="CommitRefresh"/> treats a timeout as ordinary contention. Abandon the commit,
/// keep the prior snapshot, record the timeout category, retry on the next approved trigger.
/// Nothing is lost, because the snapshot is reconstructible.
/// </para>
/// <para>
/// <see cref="CommitDisclosureChange"/> must not silently no-op. A logout whose commit was
/// skipped would leave the previous account's subjects on screen, which is a privacy failure
/// rather than a stale-data annoyance. It retries once, and if the second attempt also times
/// out it reports explicit failure so the caller can tell the user, rather than returning
/// something a caller might read as success. Suppression of message details in that window
/// does not depend on this mutex at all — that is the disclosure tombstone's job, written
/// before the attempt.
/// </para>
/// </remarks>
public sealed class StateCommitCoordinator
{
    private readonly CoordinationPaths _paths;
    private readonly MutationMutex _mutex;
    private readonly IOperationalLogger _logger;

    public StateCommitCoordinator(
        CoordinationPaths paths,
        MutationMutex mutex,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mutex);

        _paths = paths;
        _mutex = mutex;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>
    /// Commits a refresh result. One attempt; a timeout is contention and the prior snapshot
    /// stands.
    /// </summary>
    public StateCommitResult CommitRefresh(IStateCommitAction action, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(action);

        StateCommitResult result = Attempt(action, attemptNumber: 1, cancellationToken);

        if (result.Outcome == StateCommitOutcome.ContentionTimeout)
        {
            // Deliberately not retried. A refresh has nothing to lose by waiting for its
            // next trigger, and retrying would extend the refresh transaction past the
            // budget the lease horizon was chosen to cover.
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Timeout);
        }

        return result;
    }

    /// <summary>
    /// Commits a disclosure-reducing change. Retries once on contention, then reports
    /// explicit failure.
    /// </summary>
    /// <remarks>
    /// No cancellation token. These operations are user-initiated and privacy-relevant;
    /// cancelling one because some ambient deadline expired is how a sign-out becomes a
    /// silent no-op.
    /// </remarks>
    public StateCommitResult CommitDisclosureChange(
        IStateCommitAction action,
        OperationalEventId failureEvent)
    {
        ArgumentNullException.ThrowIfNull(action);

        StateCommitResult first = Attempt(action, attemptNumber: 1, CancellationToken.None);

        if (first.Outcome != StateCommitOutcome.ContentionTimeout)
        {
            return first;
        }

        StateCommitResult second = Attempt(action, attemptNumber: 2, CancellationToken.None);

        if (second.Outcome == StateCommitOutcome.ContentionTimeout)
        {
            // The caller must surface this to the user and must not report success. The
            // tombstone written before the attempt keeps details hidden meanwhile.
            _logger.Record(failureEvent, OperationalOutcome.Timeout);
        }

        return second;
    }

    private StateCommitResult Attempt(
        IStateCommitAction action,
        int attemptNumber,
        CancellationToken cancellationToken)
    {
        using MutationLock heldLock = _mutex.Acquire(cancellationToken);

        switch (heldLock.Outcome)
        {
            case MutationLockOutcome.TimedOut:
                return new StateCommitResult(StateCommitOutcome.ContentionTimeout, 0, attemptNumber);

            case MutationLockOutcome.Cancelled:
                return new StateCommitResult(StateCommitOutcome.Cancelled, 0, attemptNumber);
        }

        // From here to the release there is no await and no suspension point, which is what
        // keeps acquisition and release on one thread as Mutex requires. The critical
        // section is not cancellable: cancelling between the temp write and the atomic
        // replace is exactly how a half-committed state and a stranded release occur.
        CacheCommitResult commit = action.Execute(heldLock);

        StateCommitOutcome outcome = commit.Status switch
        {
            CacheCommitStatus.Success => StateCommitOutcome.Committed,
            CacheCommitStatus.GenerationMismatch => StateCommitOutcome.Discarded,
            _ => StateCommitOutcome.CommitFailed,
        };

        var result = new StateCommitResult(outcome, commit.Generation, attemptNumber);

        // Signal after the commit, still holding nothing that a listener needs. The event is
        // payload-free: it says only that committed state changed, and every listener
        // re-reads state for itself.
        if (result.IsCommitted)
        {
            SignalStateChanged();
        }

        return result;
    }

    /// <summary>
    /// Signals the package-user-wide state-changed event. Only ever called from here after a commit
    /// succeeded, because a signal without a generation change teaches listeners to
    /// distrust the signal.
    /// </summary>
    /// <remarks>
    /// The mechanism moved to <see cref="StateChangeSignal"/> so it exists once; the rule that
    /// <em>this</em> type signals only after a successful commit is unchanged and still enforced by the
    /// single call site above. No peer listening is tolerated there: committed state on disk is
    /// authoritative and the provider rechecks the generation on Activate and before rendering, so a
    /// missed signal delays rendering rather than losing the change.
    /// </remarks>
    private void SignalStateChanged() => StateChangeSignal.Raise(_paths);
}
