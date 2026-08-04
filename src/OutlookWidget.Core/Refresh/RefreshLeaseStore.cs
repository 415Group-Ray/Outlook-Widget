using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>Why a lease claim ended the way it did.</summary>
public enum LeaseClaimStatus
{
    /// <summary>This process now owns the lease.</summary>
    Claimed,

    /// <summary>Another process holds a live lease. Skip the duplicate refresh.</summary>
    HeldByPeer,

    /// <summary>
    /// The bounded mutex wait elapsed, meaning a peer is stuck inside a critical section.
    /// Skip this refresh rather than proceeding unsynchronized.
    /// </summary>
    MutexTimedOut,

    /// <summary>The operation's deadline expired before the wait completed.</summary>
    Cancelled,
}

/// <summary>The outcome of a claim attempt.</summary>
public readonly record struct LeaseClaim(LeaseClaimStatus Status, Guid InstanceId)
{
    public bool IsClaimed => Status == LeaseClaimStatus.Claimed;
}

/// <summary>
/// Reads and writes the refresh lease record under brief, synchronous holds of the
/// mutation mutex.
/// </summary>
/// <remarks>
/// Nothing is held while a refresh runs. Claiming means: take the mutex, see whether a
/// live unexpired lease exists, write your own if not, release. Releasing means: take the
/// mutex, clear the record if you still own it, release. Both operations are short,
/// synchronous, and single-threaded, which is the only shape a thread-affine
/// <see cref="Mutex"/> supports.
/// </remarks>
public sealed class RefreshLeaseStore
{
    private readonly CoordinationPaths _paths;
    private readonly MutationMutex _mutex;
    private readonly ISystemClock _clock;
    private readonly IOperationalLogger _logger;

    public RefreshLeaseStore(
        CoordinationPaths paths,
        MutationMutex mutex,
        ISystemClock? clock = null,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(mutex);

        _paths = paths;
        _mutex = mutex;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>
    /// Step 2 of the refresh algorithm: claim the lease, or discover that a peer holds it.
    /// </summary>
    public LeaseClaim TryClaim(CancellationToken cancellationToken = default)
    {
        using MutationLock heldLock = _mutex.Acquire(cancellationToken);

        switch (heldLock.Outcome)
        {
            case MutationLockOutcome.TimedOut:
                _logger.Record(OperationalEventId.RefreshLeaseClaimTimedOut, OperationalOutcome.Timeout);
                return new LeaseClaim(LeaseClaimStatus.MutexTimedOut, Guid.Empty);

            case MutationLockOutcome.Cancelled:
                return new LeaseClaim(LeaseClaimStatus.Cancelled, Guid.Empty);
        }

        LeaseRecord? existing = ReadRecord();

        if (existing is not null && existing.IsLive(_clock))
        {
            _logger.Record(OperationalEventId.RefreshSkippedLeaseHeld, OperationalOutcome.Skipped);
            return new LeaseClaim(LeaseClaimStatus.HeldByPeer, existing.OwnerInstanceId);
        }

        if (existing is not null)
        {
            // Either the owner died, or the record is from a previous boot session. Expiry
            // alone reclaims it: no AbandonedMutexException dependence and no watchdog.
            _logger.Record(OperationalEventId.RefreshLeaseReclaimedExpired, OperationalOutcome.Recovered);
        }

        var instanceId = Guid.NewGuid();
        var record = new LeaseRecord
        {
            OwnerProcessId = Environment.ProcessId,
            OwnerInstanceId = instanceId,
            // Expiry uses the lease horizon, deliberately longer than the async deadline,
            // because the commit that follows the awaited work is not cancellable.
            ExpiresAtTicks = _clock.TickCount64 + (long)CoordinationBounds.LeaseHorizon.TotalMilliseconds,
            BootStamp = BootSessionStamp.Current(_clock),
        };

        if (!TryWriteRecord(record))
        {
            // Could not write. Treat as not claimed rather than proceeding
            // unsynchronized; the next trigger retries.
            return new LeaseClaim(LeaseClaimStatus.MutexTimedOut, Guid.Empty);
        }

        _logger.Record(OperationalEventId.RefreshLeaseClaimed, OperationalOutcome.Success);
        return new LeaseClaim(LeaseClaimStatus.Claimed, instanceId);
    }

    /// <summary>
    /// Step 9: clear the lease if this instance still owns it. The end of the refresh
    /// transaction, and it happens before any widget delivery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A wait timeout here leaves the record alone. Expiry reclaims it, so a failed clear
    /// degrades to a delayed reclaim rather than a lost lease — which is why this method
    /// reports the timeout but does not treat it as an error the caller must handle.
    /// </para>
    /// <para>
    /// <b>It must not throw at all, for the same reason.</b> This runs inside the refresh
    /// transaction's <c>finally</c>, so an exception raised here replaces whatever was already
    /// propagating and reports the wrong failure. Process shutdown makes that reachable: the refresh
    /// worker's Dispose is bounded, so an abandoned refresh can reach this after the mutex it needs
    /// has been disposed. A disposed mutex is the same situation as a wedged one — nothing can be
    /// released, expiry handles it — so it is recorded and swallowed rather than allowed to mask.
    /// </para>
    /// </remarks>
    public void Clear(Guid instanceId)
    {
        if (instanceId == Guid.Empty)
        {
            return;
        }

        // No cancellation token. This runs in a finally, and a cancelled refresh must
        // still release its lease; passing the expired deadline's token here would make
        // cancellation the reason the lease leaked.
        MutationLock heldLock;

        try
        {
            heldLock = _mutex.Acquire();
        }
        catch (ObjectDisposedException)
        {
            _logger.Record(OperationalEventId.RefreshLeaseClearTimedOut, OperationalOutcome.Failed);
            return;
        }

        using (heldLock)
        {
            ClearOwnedRecord(instanceId, heldLock);
        }
    }

    private void ClearOwnedRecord(Guid instanceId, in MutationLock heldLock)
    {
        if (!heldLock.IsHeld)
        {
            _logger.Record(OperationalEventId.RefreshLeaseClearTimedOut, OperationalOutcome.Timeout);
            return;
        }

        LeaseRecord? existing = ReadRecord();

        if (existing is null || !existing.IsOwnedBy(instanceId))
        {
            // Already reclaimed by a peer after expiry, or replaced. Deleting it would
            // remove that peer's live lease, so do nothing.
            return;
        }

        try
        {
            File.Delete(_paths.LeaseFilePath);
            _logger.Record(OperationalEventId.RefreshLeaseCleared, OperationalOutcome.Success);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.RefreshLeaseClearTimedOut, OperationalOutcome.Failed);
        }
    }

    /// <summary>
    /// Whether a live lease exists right now, for the "Refresh already in progress"
    /// indicator. A lock-free read: readers and state mutators never wait on the lease.
    /// </summary>
    public bool IsLeaseLive() => ReadRecord() is { } record && record.IsLive(_clock);

    /// <summary>
    /// Reads the record without any lock. Callers that will mutate it hold the mutex
    /// already; callers that only display it must not be made to wait.
    /// </summary>
    private LeaseRecord? ReadRecord()
    {
        try
        {
            using var stream = new FileStream(
                _paths.LeaseFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            return LeaseRecord.TryParse(reader.ReadToEnd());
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Unreadable is treated as absent, the opposite of the tombstone's direction
            // and for the opposite reason: an unreadable lease that read as live would
            // wedge refreshing permanently, while treating it as absent costs at most one
            // duplicate request that the generation compare already handles.
            return null;
        }
    }

    private bool TryWriteRecord(LeaseRecord record)
    {
        _paths.EnsureCreated();

        try
        {
            // Written under the mutex, so a plain overwrite is safe. Readers open with
            // FileShare.Delete and tolerate an absent or partial file by treating it as
            // absent, so no temp-and-replace dance is warranted for a record that is
            // reconstructed on every claim.
            File.WriteAllText(_paths.LeaseFilePath, record.Serialize());
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
