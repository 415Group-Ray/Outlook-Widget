using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>What caused a refresh attempt.</summary>
public enum RefreshTrigger
{
    SignIn,
    AccountSwitch,
    Activation,
    ActiveTimer,
    ManualAction,
    SettingsChange,
}

/// <summary>How the refresh transaction ended.</summary>
public enum RefreshOutcome
{
    /// <summary>Fetched, validated, committed, and the generation advanced.</summary>
    Committed,

    /// <summary>
    /// Committed state changed while I/O was in flight, so the result was discarded and the
    /// current committed state is the render source.
    /// </summary>
    Discarded,

    /// <summary>A live lease exists elsewhere; this duplicate request was skipped.</summary>
    SkippedLeaseHeld,

    /// <summary>Inside the manual-refresh debounce window.</summary>
    SkippedDebounce,

    /// <summary>A peer is stuck in a critical section, so this refresh did not proceed.</summary>
    SkippedContention,

    /// <summary>The 20-second async deadline expired. Nothing was committed.</summary>
    DeadlineExceeded,

    /// <summary>The fetch failed. The prior snapshot stands.</summary>
    FetchFailed,

    /// <summary>The commit was attempted and failed. The prior snapshot stands.</summary>
    CommitFailed,

    /// <summary>Cancelled by the caller.</summary>
    Cancelled,
}

/// <summary>Whether a delivery was requested, and nothing more.</summary>
/// <remarks>
/// Delivery <em>outcome</em> is recorded by <see cref="DeliveryWorker"/>, not here.
/// "Refresh succeeded, delivery slow or failed" is a real and distinguishable state, and
/// conflating the two would hide a host problem behind an apparently failing refresh.
/// </remarks>
public enum DeliveryRequestOutcome
{
    /// <summary>Not applicable, because nothing was committed.</summary>
    NotRequested,

    /// <summary>The pending marker was set, or the state-changed event served as the request.</summary>
    Requested,
}

/// <summary>
/// The result of one refresh transaction, with refresh and delivery as separate fields.
/// </summary>
public readonly record struct RefreshResult(
    RefreshOutcome Outcome,
    DeliveryRequestOutcome Delivery,
    long Generation,
    TimeSpan Duration)
{
    public bool IsCommitted => Outcome == RefreshOutcome.Committed;
}

/// <summary>
/// What a refresh fetched: the bytes to protect and commit, plus a bounded count for
/// metadata-free logging.
/// </summary>
public readonly record struct RefreshPayload(byte[] State, int RecordCount);

/// <summary>
/// Performs the awaited part of a refresh: silent token acquisition, the concurrent Graph
/// requests, and validation.
/// </summary>
/// <remarks>
/// Kept behind a seam so the coordination subsystem is testable and shippable before the
/// Graph client exists — the plan makes coordination the first vertical slice precisely
/// because its correctness must be established rather than discovered later.
/// </remarks>
public interface IRefreshFetcher
{
    /// <summary>
    /// Fetches new state, observing <paramref name="cancellationToken"/> at every awaited
    /// step. Returns <see langword="null"/> when there is nothing to commit.
    /// </summary>
    Task<RefreshPayload?> FetchAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Requests a widget delivery pass without supplying content.
/// </summary>
public interface IDeliveryRequester
{
    /// <summary>Sets the coalescing pending marker. Returns immediately.</summary>
    void RequestDelivery();
}

/// <summary>
/// In the companion process, the state-changed event <em>is</em> the delivery request.
/// </summary>
/// <remarks>
/// The companion must never call the widget host. This implementation therefore does
/// nothing: <see cref="StateCommitCoordinator"/> has already signalled the state-changed
/// event, the provider's listener picks it up, and its worker performs the pass. Making that
/// explicit as a type is clearer than a null check at the call site, and it means a future
/// edit that gives the companion a real delivery path has to delete a type whose
/// documentation says not to.
/// </remarks>
public sealed class SignalOnlyDeliveryRequester : IDeliveryRequester
{
    public void RequestDelivery()
    {
        // Intentionally empty. See the remarks.
    }
}

/// <summary>
/// Runs the refresh transaction: lease claim through lease clear, with widget delivery
/// deliberately outside it.
/// </summary>
/// <remarks>
/// <para>
/// The step numbering in this type's implementation follows the plan's refresh algorithm
/// directly, and the ordering of steps 9 through 11 is load-bearing. The lease is cleared
/// <em>before</em> delivery is requested, which is what makes delivery post-transactional:
/// <c>UpdateWidget</c> is unbounded, and a slow host would otherwise drag the operation past
/// the lease horizon, at which point a peer could claim the lease while its owner was still
/// nominally mid-operation — the exact race the horizon exists to prevent.
/// </para>
/// <para>
/// Clearing early is safe. Once the snapshot is committed and its generation incremented, a
/// peer that claims the lease finds fresh data and skips under the activation-staleness rule,
/// and if it refreshes anyway the generation compare handles it. Nothing about correctness
/// depends on holding the lease through rendering.
/// </para>
/// </remarks>
public sealed class RefreshCoordinator
{
    private readonly ProtectedCache _cache;
    private readonly RefreshLeaseStore _leases;
    private readonly StateCommitCoordinator _commits;
    private readonly IDeliveryRequester _delivery;
    private readonly ISystemClock _clock;
    private readonly IOperationalLogger _logger;

    private readonly Lock _debounceGate = new();
    private long _lastManualRefreshTicks = long.MinValue;

    public RefreshCoordinator(
        ProtectedCache cache,
        RefreshLeaseStore leases,
        StateCommitCoordinator commits,
        IDeliveryRequester delivery,
        ISystemClock? clock = null,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(leases);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(delivery);

        // Fail here rather than as a rare mid-commit lease reclaim in production.
        CoordinationBounds.Validate();

        _cache = cache;
        _leases = leases;
        _commits = commits;
        _delivery = delivery;
        _clock = clock ?? SystemClock.Instance;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>
    /// Whether a live lease exists, for the "Refresh already in progress" indicator. The
    /// indicator follows the lease, so its ceiling is the lease horizon rather than the async
    /// deadline — and it clears at commit, which is correct: the data is fresh at that point
    /// and only its rendering is still in flight.
    /// </summary>
    public bool IsRefreshInProgress() => _leases.IsLeaseLive();

    /// <summary>
    /// Runs one refresh transaction.
    /// </summary>
    /// <remarks>
    /// Step 1 of the plan's algorithm — render the last valid cache immediately — is the
    /// caller's, not this method's: the provider renders from cache before asking for a
    /// refresh at all, so a refresh never gates first paint.
    /// </remarks>
    public async Task<RefreshResult> RefreshAsync(
        IRefreshFetcher fetcher,
        RefreshTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(fetcher);

        long startTicks = _clock.TickCount64;
        _logger.Record(OperationalEventId.RefreshRequested, OperationalOutcome.Success);

        if (trigger == RefreshTrigger.ManualAction && IsWithinDebounce())
        {
            _logger.Record(OperationalEventId.RefreshSkippedDebounce, OperationalOutcome.Skipped);
            return Result(RefreshOutcome.SkippedDebounce, DeliveryRequestOutcome.NotRequested, 0, startTicks);
        }

        // Step 2: claim the lease. A bounded, synchronous mutex hold that writes an expiring
        // record and releases immediately. Nothing is held while the refresh runs.
        LeaseClaim claim = _leases.TryClaim(cancellationToken);

        switch (claim.Status)
        {
            case LeaseClaimStatus.HeldByPeer:
                return Result(RefreshOutcome.SkippedLeaseHeld, DeliveryRequestOutcome.NotRequested, 0, startTicks);

            case LeaseClaimStatus.MutexTimedOut:
                return Result(RefreshOutcome.SkippedContention, DeliveryRequestOutcome.NotRequested, 0, startTicks);

            case LeaseClaimStatus.Cancelled:
                return Result(RefreshOutcome.Cancelled, DeliveryRequestOutcome.NotRequested, 0, startTicks);
        }

        if (trigger == RefreshTrigger.ManualAction)
        {
            RecordManualRefresh();
        }

        RefreshOutcome outcome;
        long generation = 0;

        try
        {
            // Step 3: start the overall async deadline. One linked source, observed by every
            // awaited step. It does not bound the commit, which is synchronous and
            // deliberately non-cancellable once entered.
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(CoordinationBounds.AsyncDeadline);

            long capturedGeneration = _cache.ReadGeneration();

            RefreshPayload? payload;

            try
            {
                // Steps 3 to 5: token acquisition, the concurrent Graph GETs with their own
                // nested 10-second timeout, and validation. All outside every lock.
                payload = await fetcher.FetchAsync(deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested
                                                     && !cancellationToken.IsCancellationRequested)
            {
                _logger.Record(OperationalEventId.RefreshDeadlineExceeded, OperationalOutcome.Timeout);
                return Result(RefreshOutcome.DeadlineExceeded, DeliveryRequestOutcome.NotRequested, 0, startTicks);
            }
            catch (OperationCanceledException)
            {
                return Result(RefreshOutcome.Cancelled, DeliveryRequestOutcome.NotRequested, 0, startTicks);
            }
            catch (Exception e) when (e is IOException or TimeoutException or InvalidOperationException)
            {
                _logger.Record(OperationalEventId.GraphRequestFailed, OperationalOutcome.Failed);
                return Result(RefreshOutcome.FetchFailed, DeliveryRequestOutcome.NotRequested, 0, startTicks);
            }

            if (payload is not { } fetched)
            {
                return Result(RefreshOutcome.FetchFailed, DeliveryRequestOutcome.NotRequested, 0, startTicks);
            }

            // Steps 6 to 8: acquire the mutation mutex with the bounded wait, re-compare the
            // generation under it, replace atomically, increment, and release in a finally on
            // the acquiring thread. All synchronous, all inside StateCommitCoordinator.
            //
            // The deadline's token is passed so a refresh already past its deadline abandons
            // the *wait* immediately rather than sitting the full two seconds. Once the mutex
            // is acquired, cancellation no longer applies.
            StateCommitResult commit = _commits.CommitRefresh(
                new CommitSnapshotAction(_cache, fetched.State, capturedGeneration),
                deadline.Token);

            generation = commit.Generation;

            outcome = commit.Outcome switch
            {
                StateCommitOutcome.Committed => RefreshOutcome.Committed,
                StateCommitOutcome.Discarded => RefreshOutcome.Discarded,
                StateCommitOutcome.ContentionTimeout => RefreshOutcome.SkippedContention,
                StateCommitOutcome.Cancelled => RefreshOutcome.Cancelled,
                _ => RefreshOutcome.CommitFailed,
            };

            if (outcome == RefreshOutcome.Committed)
            {
                _logger.Record(
                    OperationalEventId.RefreshCompleted,
                    OperationalOutcome.Success,
                    recordCount: fetched.RecordCount);
            }
        }
        finally
        {
            // Step 9: clear the lease. This finally spans steps 2 through 9 only, so success,
            // discard, failure, timeout, and cancellation all reach it. A process killed
            // before it runs is covered by lease expiry.
            _leases.Clear(claim.InstanceId);
        }

        // Steps 10 and 11 are outside the transaction. The state-changed event was signalled
        // by the commit coordinator; delivery is only *requested* here. This coordinator never
        // calls the widget host — see IWidgetDeliverySink.
        DeliveryRequestOutcome delivery = DeliveryRequestOutcome.NotRequested;

        if (outcome == RefreshOutcome.Committed)
        {
            _delivery.RequestDelivery();
            delivery = DeliveryRequestOutcome.Requested;
        }

        // Step 12: record a metadata-free outcome with refresh and delivery as separate fields.
        return Result(outcome, delivery, generation, startTicks);
    }

    private bool IsWithinDebounce()
    {
        lock (_debounceGate)
        {
            long elapsed = _clock.TickCount64 - _lastManualRefreshTicks;
            return _lastManualRefreshTicks != long.MinValue
                   && elapsed < (long)CoordinationBounds.ManualRefreshDebounce.TotalMilliseconds;
        }
    }

    private void RecordManualRefresh()
    {
        lock (_debounceGate)
        {
            _lastManualRefreshTicks = _clock.TickCount64;
        }
    }

    private RefreshResult Result(
        RefreshOutcome outcome,
        DeliveryRequestOutcome delivery,
        long generation,
        long startTicks) =>
        new(outcome, delivery, generation, TimeSpan.FromMilliseconds(_clock.TickCount64 - startTicks));
}
