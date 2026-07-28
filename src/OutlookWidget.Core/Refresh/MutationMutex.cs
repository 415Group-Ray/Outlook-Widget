using System.Diagnostics.CodeAnalysis;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// Why a mutation-mutex acquisition attempt ended.
/// </summary>
public enum MutationLockOutcome
{
    /// <summary>Ownership was taken cleanly.</summary>
    Acquired,

    /// <summary>
    /// Ownership was taken, but the previous owner was killed without releasing.
    /// The caller owns the mutex and must additionally treat protected state as
    /// suspect.
    /// </summary>
    AcquiredAbandoned,

    /// <summary>
    /// The bounded wait elapsed without ownership. A peer is stuck inside a critical
    /// section; the caller's defined timeout behaviour applies.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The wait was abandoned because the operation's deadline had already expired.
    /// Nothing is owned, so abandoning cost nothing.
    /// </summary>
    Cancelled,
}

/// <summary>
/// A cross-process named mutex guarding local state commits, wrapped so that every
/// acquisition is bounded and every release happens on the acquiring thread.
/// </summary>
/// <remarks>
/// <para>
/// <b>Thread affinity is the reason this type exists.</b>
/// <see cref="System.Threading.Mutex"/> requires the releasing thread to be the
/// acquiring thread. An <c>await</c> continuation is not guaranteed to resume on the
/// thread that suspended, so a named mutex held across awaited WAM or Graph work can
/// fail to release even inside a correct <c>try/finally</c> — leaving the mutex held
/// until process exit and deadlocking cross-process state coordination.
/// </para>
/// <para>
/// The mitigation is structural rather than careful: the critical section this mutex
/// guards is entirely synchronous local work — DPAPI protect and unprotect, temp-file
/// write, atomic replace, generation increment — with no suspension point between
/// acquire and release. <see cref="MutationLock"/> verifies the affinity at release
/// so a future edit that introduces an <c>await</c> fails loudly and locally instead
/// of intermittently and remotely.
/// </para>
/// <para>
/// <b>Every acquisition is bounded.</b> The parameterless
/// <c>WaitOne()</c> overload is prohibited throughout this project: it waits
/// indefinitely, so a peer wedged inside a critical section would hang the caller
/// with no recovery path, which is precisely the failure the lock-free read design
/// exists to prevent.
/// </para>
/// </remarks>
public sealed class MutationMutex : IDisposable
{
    private readonly Mutex _mutex;
    private readonly IOperationalLogger _logger;
    private bool _disposed;

    /// <summary>
    /// Creates or opens the named mutex. The name is scoped to the package and the
    /// current user, matching the DPAPI <c>CurrentUser</c> scope of the state it
    /// guards: two different Windows users have separate protected state and must not
    /// serialize against each other.
    /// </summary>
    public MutationMutex(string name, IOperationalLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(logger);

        _logger = logger;

        // Local\ rather than Global\: the scope is one interactive user's state, and
        // Global\ would additionally require privileges this package does not request.
        _mutex = new Mutex(initiallyOwned: false, name: $@"Local\{name}");
    }

    /// <summary>
    /// Attempts to take ownership within <see cref="CoordinationBounds.MutexWait"/>,
    /// abandoning the wait early if <paramref name="cancellationToken"/> is already
    /// signalled or becomes signalled during the wait.
    /// </summary>
    /// <remarks>
    /// The cancellation applies to the <em>wait</em> only. Once ownership is taken the
    /// critical section runs to completion: cancelling between the temp write and the
    /// atomic replace is exactly how a half-committed state and a stranded release
    /// would occur.
    /// </remarks>
    public MutationLock Acquire(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Check cancellation before waiting, and do not rely on WaitAny to notice it.
        //
        // WaitAny returns the *lowest* signalled index, so with an uncontended mutex at
        // index 0 it would report a clean acquisition even when the cancellation handle at
        // index 1 was already signalled. That is not merely an unobserved cancellation: it
        // would let a refresh whose async deadline had already blown proceed to commit, and
        // the lease-horizon arithmetic assumes the commit begins inside the budget. A
        // deadline-expired refresh must abandon and retry on its next trigger, keeping the
        // prior snapshot, rather than commit late against a lease that may be near expiry.
        if (cancellationToken.IsCancellationRequested)
        {
            return MutationLock.NotAcquired(MutationLockOutcome.Cancelled);
        }

        // Waiting on the mutex and the cancellation handle together is what lets a
        // refresh already past its deadline abandon the wait instead of sitting the
        // full two seconds. Nothing is owned yet, so abandoning costs nothing.
        //
        // A residual race remains and is deliberately accepted: if cancellation fires at the
        // same moment the mutex becomes free, WaitAny may report the acquisition. The caller
        // then proceeds, which is correct — once ownership is taken the critical section must
        // run to completion, and its lateness is bounded by the two-second wait.
        WaitHandle[] handles = cancellationToken.CanBeCanceled
            ? [_mutex, cancellationToken.WaitHandle]
            : [_mutex];

        int signalled;
        bool abandoned = false;

        try
        {
            signalled = WaitHandle.WaitAny(handles, CoordinationBounds.MutexWait);
        }
        catch (AbandonedMutexException e)
        {
            // Critical: this exception means the wait *succeeded* and this thread now
            // owns the mutex. Treating it as a failure would leak ownership until
            // process exit. The previous owner was killed mid-commit, so protected
            // state is suspect and the caller must validate it.
            if (e.Mutex is null && e.MutexIndex != 0)
            {
                // Defensive: an abandoned handle that is not ours should not be
                // possible with this handle set, and silently assuming ownership
                // would be worse than failing.
                throw;
            }

            signalled = 0;
            abandoned = true;
        }

        if (signalled == WaitHandle.WaitTimeout)
        {
            _logger.Record(OperationalEventId.MutationLockWaitTimedOut, OperationalOutcome.Timeout);
            return MutationLock.NotAcquired(MutationLockOutcome.TimedOut);
        }

        if (signalled != 0)
        {
            // The cancellation handle was signalled first; nothing is owned.
            return MutationLock.NotAcquired(MutationLockOutcome.Cancelled);
        }

        if (abandoned)
        {
            _logger.Record(OperationalEventId.MutationLockAbandonedByPeer, OperationalOutcome.Recovered);
        }

        return MutationLock.Owned(
            _mutex,
            abandoned ? MutationLockOutcome.AcquiredAbandoned : MutationLockOutcome.Acquired,
            _logger);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _mutex.Dispose();
    }
}

/// <summary>
/// Ownership of the mutation mutex for the duration of one synchronous critical
/// section. Dispose on the acquiring thread, from a <c>finally</c>.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately a <c>ref struct</c>. That is not a micro-optimization — it is the
/// enforcement mechanism. A <c>ref struct</c> cannot be captured in a closure, stored
/// in a field, boxed, or held across an <c>await</c>, so the compiler rejects the
/// exact shapes that would violate <see cref="System.Threading.Mutex"/> thread
/// affinity. The alternative, a comment asking future readers not to do those things,
/// fails silently the first time someone does.
/// </para>
/// <para>
/// <c>using</c> works through the pattern-based dispose that <c>ref struct</c>
/// supports; the runtime thread check in <see cref="Dispose"/> remains as a backstop
/// for a hop the compiler cannot see, such as an explicit thread switch inside a
/// synchronous call.
/// </para>
/// </remarks>
public ref struct MutationLock
{
    private Mutex? _mutex;
    private readonly IOperationalLogger? _logger;
    private readonly int _acquiringThreadId;
    private bool _released;

    private MutationLock(Mutex? mutex, MutationLockOutcome outcome, IOperationalLogger? logger)
    {
        _mutex = mutex;
        Outcome = outcome;
        _logger = logger;
        _acquiringThreadId = mutex is null ? 0 : Environment.CurrentManagedThreadId;
        _released = false;
    }

    internal static MutationLock Owned(Mutex mutex, MutationLockOutcome outcome, IOperationalLogger logger) =>
        new(mutex, outcome, logger);

    internal static MutationLock NotAcquired(MutationLockOutcome outcome) =>
        new(null, outcome, null);

    /// <summary>How the acquisition attempt ended.</summary>
    public MutationLockOutcome Outcome { get; }

    /// <summary>
    /// Whether this thread owns the mutex. True for both a clean acquisition and an
    /// abandoned one, because an abandoned mutex <em>is</em> owned by the waiter.
    /// </summary>
    public readonly bool IsHeld =>
        Outcome is MutationLockOutcome.Acquired or MutationLockOutcome.AcquiredAbandoned;

    /// <summary>
    /// Whether the previous owner died mid-commit, so protected state is suspect and
    /// must be validated before it is trusted.
    /// </summary>
    public readonly bool StateIsSuspect => Outcome == MutationLockOutcome.AcquiredAbandoned;

    /// <summary>
    /// Releases ownership, verifying that the release happens on the acquiring thread.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The release is attempted from a different thread than the acquisition. That can
    /// only mean an <c>await</c>, a <c>Task.Run</c>, or a scheduler hop was introduced
    /// inside the critical section, which violates the invariant this whole design
    /// rests on. Failing here converts a rare, remote, hard-to-diagnose deadlock into
    /// an immediate and obvious defect.
    /// </exception>
    public void Dispose()
    {
        if (_released || _mutex is null)
        {
            return;
        }

        _released = true;

        int currentThreadId = Environment.CurrentManagedThreadId;
        if (currentThreadId != _acquiringThreadId)
        {
            // Deliberately not swallowed. Releasing from the wrong thread throws
            // ApplicationException from the runtime anyway; this message says why.
            _logger?.Record(OperationalEventId.MutationLockAffinityViolated, OperationalOutcome.Failed);
            throw new InvalidOperationException(
                $"The mutation mutex was acquired on thread {_acquiringThreadId} and is " +
                $"being released on thread {currentThreadId}. System.Threading.Mutex is " +
                "thread-affine, so a critical section must contain no await, no Task.Run, " +
                "and no scheduler hop.");
        }

        Mutex mutex = _mutex;
        _mutex = null;
        mutex.ReleaseMutex();
    }

    /// <summary>
    /// Throws if the lock is not held. Used at the top of a critical section so a
    /// caller that forgets to check <see cref="IsHeld"/> cannot proceed to mutate
    /// state unprotected.
    /// </summary>
    [SuppressMessage("Design", "CA1065:Do not raise exceptions in unexpected locations",
        Justification = "Explicit guard method; failing here prevents an unprotected mutation.")]
    public readonly void ThrowIfNotHeld()
    {
        if (!IsHeld)
        {
            throw new InvalidOperationException(
                $"The mutation mutex is not held (outcome: {Outcome}). No state may be mutated.");
        }
    }
}
