using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

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

    /// <summary>The mutex was acquired but the commit itself failed. Prior state is intact.</summary>
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
    public StateCommitResult CommitDisclosureChange(IStateCommitAction action)
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
            _logger.Record(OperationalEventId.SignOutFailed, OperationalOutcome.Timeout);
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
    /// Signals the package-user-wide state-changed event. Only ever called after a commit
    /// succeeded, because a signal without a generation change teaches listeners to
    /// distrust the signal.
    /// </summary>
    private void SignalStateChanged()
    {
        try
        {
            using var stateChanged = EventWaitHandle.OpenExisting(_paths.StateChangedEventName);
            stateChanged.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No peer is listening. Committed state on disk is authoritative and the
            // provider rechecks the generation on Activate and before rendering, so a
            // missed signal delays rendering rather than losing the change.
        }
    }
}
