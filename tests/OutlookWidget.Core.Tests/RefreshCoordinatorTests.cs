using System.Text;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The refresh transaction end to end: lease claim through lease clear, with delivery
/// deliberately outside it.
/// </summary>
public sealed class RefreshCoordinatorTests
{
    private static byte[] Payload(string content) => Encoding.UTF8.GetBytes(content);

    /// <summary>Records that delivery was requested, without delivering anything.</summary>
    private sealed class CountingDeliveryRequester : IDeliveryRequester
    {
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public void RequestDelivery() => Interlocked.Increment(ref _requests);
    }

    /// <summary>A fetcher whose behaviour each test dictates.</summary>
    private sealed class StubFetcher(Func<CancellationToken, Task<RefreshPayload?>> body) : IRefreshFetcher
    {
        public Task<RefreshPayload?> FetchAsync(CancellationToken cancellationToken) => body(cancellationToken);

        public static StubFetcher Returning(string content) =>
            new(_ => Task.FromResult<RefreshPayload?>(new RefreshPayload(Payload(content), 5)));
    }

    private static RefreshCoordinator Build(
        CoordinationFixture fixture,
        IDeliveryRequester delivery) =>
        new(fixture.Cache, fixture.Leases, fixture.Commits, delivery, fixture.Clock, fixture.Logger);

    [Fact]
    public async Task A_successful_refresh_commits_advances_the_generation_and_requests_delivery()
    {
        using var fixture = new CoordinationFixture();
        var delivery = new CountingDeliveryRequester();
        RefreshCoordinator coordinator = Build(fixture, delivery);

        RefreshResult result = await coordinator.RefreshAsync(
            StubFetcher.Returning("fresh"),
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.Committed, result.Outcome);
        Assert.Equal(DeliveryRequestOutcome.Requested, result.Delivery);
        Assert.Equal(1, result.Generation);
        Assert.Equal(1, delivery.Requests);
        Assert.Equal("fresh", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
    }

    [Fact]
    public async Task The_lease_is_cleared_before_delivery_is_requested()
    {
        using var fixture = new CoordinationFixture();

        // Observes the lease at the moment delivery is requested. This is the ordering that
        // makes delivery post-transactional: UpdateWidget is unbounded, and a slow host would
        // otherwise drag the operation past the lease horizon, letting a peer claim the lease
        // while its owner was still nominally mid-operation.
        var probe = new LeaseObservingRequester(fixture.Leases);
        RefreshCoordinator coordinator = Build(fixture, probe);

        RefreshResult result = await coordinator.RefreshAsync(
            StubFetcher.Returning("fresh"),
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.Committed, result.Outcome);
        Assert.True(probe.WasCalled);
        Assert.False(probe.LeaseWasLiveAtRequest);
    }

    private sealed class LeaseObservingRequester(RefreshLeaseStore leases) : IDeliveryRequester
    {
        public bool WasCalled { get; private set; }

        public bool LeaseWasLiveAtRequest { get; private set; }

        public void RequestDelivery()
        {
            WasCalled = true;
            LeaseWasLiveAtRequest = leases.IsLeaseLive();
        }
    }

    [Fact]
    public async Task A_duplicate_refresh_is_skipped_while_a_peer_holds_a_live_lease()
    {
        using var fixture = new CoordinationFixture();
        var delivery = new CountingDeliveryRequester();
        RefreshCoordinator coordinator = Build(fixture, delivery);

        // A peer's live lease, written directly so the test controls its expiry.
        var peerLease = new LeaseRecord
        {
            OwnerProcessId = 4242,
            OwnerInstanceId = Guid.NewGuid(),
            ExpiresAtTicks = fixture.Clock.TickCount64 + (long)CoordinationBounds.LeaseHorizon.TotalMilliseconds,
            BootStamp = BootSessionStamp.Current(fixture.Clock),
        };
        File.WriteAllText(fixture.Paths.LeaseFilePath, peerLease.Serialize());

        Assert.True(coordinator.IsRefreshInProgress());

        RefreshResult result = await coordinator.RefreshAsync(
            StubFetcher.Returning("duplicate"),
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.SkippedLeaseHeld, result.Outcome);
        Assert.Equal(DeliveryRequestOutcome.NotRequested, result.Delivery);
        Assert.Equal(0, delivery.Requests);

        // Readers and state mutations never wait on the lease, so nothing was committed and
        // nothing blocked.
        Assert.Equal(Caching.CacheReadStatus.Absent, fixture.Cache.Read().Status);
    }

    [Fact]
    public async Task An_expired_peer_lease_is_reclaimed_without_any_watchdog()
    {
        using var fixture = new CoordinationFixture();
        var delivery = new CountingDeliveryRequester();
        RefreshCoordinator coordinator = Build(fixture, delivery);

        // The owner was killed. It leaves an expired record, and expiry alone reclaims it — no
        // AbandonedMutexException dependence and no separate watchdog timer.
        var deadOwner = new LeaseRecord
        {
            OwnerProcessId = 4242,
            OwnerInstanceId = Guid.NewGuid(),
            ExpiresAtTicks = fixture.Clock.TickCount64 - 1,
            BootStamp = BootSessionStamp.Current(fixture.Clock),
        };
        File.WriteAllText(fixture.Paths.LeaseFilePath, deadOwner.Serialize());

        Assert.False(coordinator.IsRefreshInProgress());

        RefreshResult result = await coordinator.RefreshAsync(
            StubFetcher.Returning("after reclaim"),
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.Committed, result.Outcome);
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.RefreshLeaseReclaimedExpired));
    }

    [Fact]
    public async Task A_lease_from_a_prior_boot_session_is_expired_by_definition()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        var preReboot = new LeaseRecord
        {
            OwnerProcessId = 4242,
            OwnerInstanceId = Guid.NewGuid(),
            // Far in the future by tick value alone.
            ExpiresAtTicks = fixture.Clock.TickCount64 + 30_000,
            BootStamp = BootSessionStamp.Current(fixture.Clock),
        };
        File.WriteAllText(fixture.Paths.LeaseFilePath, preReboot.Serialize());

        fixture.Clock.SimulateReboot(downtime: TimeSpan.FromMinutes(2), resumeTicks: 4_000);

        RefreshResult result = await coordinator.RefreshAsync(
            StubFetcher.Returning("post reboot"),
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.Committed, result.Outcome);
    }

    [Fact]
    public async Task An_unparseable_lease_is_treated_as_absent_rather_than_wedging_refresh_forever()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        File.WriteAllText(fixture.Paths.LeaseFilePath, "{ not json");

        // The opposite direction from the disclosure tombstone, and for the opposite reason: an
        // unreadable lease that read as live would wedge refreshing permanently, while treating
        // it as absent costs at most one duplicate request that the generation compare handles.
        RefreshResult result = await coordinator.RefreshAsync(
            StubFetcher.Returning("recovered"),
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.Committed, result.Outcome);
    }

    [Fact]
    public async Task The_lease_is_cleared_on_every_exit_path_including_a_failed_fetch()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        var failing = new StubFetcher(_ => Task.FromResult<RefreshPayload?>(null));

        RefreshResult result = await coordinator.RefreshAsync(failing, RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.FetchFailed, result.Outcome);

        // The try/finally spans the claim through the clear, so success, discard, failure,
        // timeout, and cancellation all reach it. A stranded lease would block every refresh
        // until the horizon elapsed.
        Assert.False(coordinator.IsRefreshInProgress());
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.RefreshLeaseCleared));
    }

    [Fact]
    public async Task The_lease_is_cleared_when_the_fetch_throws()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        var throwing = new StubFetcher(_ => throw new IOException("network down"));

        RefreshResult result = await coordinator.RefreshAsync(throwing, RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.FetchFailed, result.Outcome);
        Assert.False(coordinator.IsRefreshInProgress());
    }

    [Fact]
    public async Task An_ordinary_network_failure_becomes_a_result_rather_than_an_exception()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("prior"));

        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        // HttpRequestException is the common case, not an exotic one: connection failures, DNS
        // failures, and unsuccessful responses all surface as it. It was previously not caught, so
        // a routine offline refresh threw out of RefreshAsync instead of returning the documented
        // FetchFailed — breaking the contract every caller relies on.
        var offline = new StubFetcher(_ =>
            throw new System.Net.Http.HttpRequestException("No such host is known."));

        RefreshResult result = await coordinator.RefreshAsync(offline, RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.FetchFailed, result.Outcome);
        Assert.Equal(DeliveryRequestOutcome.NotRequested, result.Delivery);

        // The cache is intact and the lease released, so the next trigger can retry.
        Assert.Equal("prior", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
        Assert.False(coordinator.IsRefreshInProgress());
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.GraphRequestFailed));
    }

    [Fact]
    public async Task A_nested_fetch_timeout_is_a_fetch_failure_not_caller_cancellation()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("prior"));

        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());
        var timedOut = new StubFetcher(_ =>
            throw new TaskCanceledException("The nested Graph request timed out."));

        RefreshResult result = await coordinator.RefreshAsync(
            timedOut,
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.FetchFailed, result.Outcome);
        Assert.Equal(DeliveryRequestOutcome.NotRequested, result.Delivery);
        Assert.Equal("prior", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
        Assert.False(coordinator.IsRefreshInProgress());
        Assert.True(fixture.Logger.Saw(
            Diagnostics.OperationalEventId.GraphRequestFailed,
            Diagnostics.OperationalOutcome.Timeout));
    }

    [Fact]
    public async Task A_fetch_that_outlives_the_async_deadline_commits_nothing_and_clears_the_lease()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("prior"));

        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        // Observes the deadline rather than ignoring it, which is what every awaited step must do.
        var slow = new StubFetcher(async token =>
        {
            await Task.Delay(CoordinationBounds.AsyncDeadline + TimeSpan.FromSeconds(5), token);
            return new RefreshPayload(Payload("too late"), 5);
        });

        RefreshResult result = await coordinator.RefreshAsync(slow, RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.DeadlineExceeded, result.Outcome);
        Assert.Equal(DeliveryRequestOutcome.NotRequested, result.Delivery);

        // The prior snapshot is intact.
        Assert.Equal("prior", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
        Assert.Equal(1, fixture.Cache.ReadGeneration());
        Assert.False(coordinator.IsRefreshInProgress());
    }

    [Fact]
    public async Task Caller_cancellation_is_reported_separately_from_a_deadline_expiry()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        using var cancelled = new CancellationTokenSource();

        var slow = new StubFetcher(async token =>
        {
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            return new RefreshPayload(Payload("never"), 5);
        });

        Task<RefreshResult> refresh = coordinator.RefreshAsync(slow, RefreshTrigger.Activation, cancelled.Token);
        await cancelled.CancelAsync();

        RefreshResult result = await refresh;

        // A caller-cancelled refresh and a refresh that blew its own deadline are different
        // operational categories, and conflating them would hide a slow tenant behind an
        // apparently cancelled request.
        Assert.Equal(RefreshOutcome.Cancelled, result.Outcome);
        Assert.False(coordinator.IsRefreshInProgress());
    }

    [Fact]
    public async Task An_internal_deadline_expiring_during_commit_wait_is_not_reported_as_caller_cancellation()
    {
        using var fixture = new CoordinationFixture();
        var delivery = new CountingDeliveryRequester();
        var coordinator = new RefreshCoordinator(
            fixture.Cache,
            fixture.Leases,
            fixture.Commits,
            delivery,
            fixture.Clock,
            fixture.Logger,
            asyncDeadline: TimeSpan.FromMilliseconds(100));

        MutexHoldingPeer? peer = null;

        try
        {
            var fetcher = new StubFetcher(_ =>
            {
                // Lease claim has completed before the fetcher runs. Take the mutation mutex now
                // so the commit wait, rather than the lease claim, crosses the internal deadline.
                peer = MutexHoldingPeer.Start(
                    fixture.Paths.MutationMutexName,
                    holdFor: TimeSpan.FromMilliseconds(500),
                    fixture.Root);
                peer.WaitUntilHolding(TimeSpan.FromSeconds(5));

                return Task.FromResult<RefreshPayload?>(
                    new RefreshPayload(Payload("too late"), 5));
            });

            RefreshResult result = await coordinator.RefreshAsync(
                fetcher,
                RefreshTrigger.Activation);

            Assert.Equal(RefreshOutcome.DeadlineExceeded, result.Outcome);
            Assert.Equal(DeliveryRequestOutcome.NotRequested, result.Delivery);
            Assert.Equal(0, delivery.Requests);
            Assert.Equal(Caching.CacheReadStatus.Absent, fixture.Cache.Read().Status);
            Assert.True(fixture.Logger.Saw(
                Diagnostics.OperationalEventId.RefreshDeadlineExceeded));
        }
        finally
        {
            peer?.Dispose();
        }
    }

    [Fact]
    public async Task A_refresh_superseded_while_its_io_was_in_flight_is_discarded()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        // A logout commits while the Graph request is outstanding.
        var racing = new StubFetcher(_ =>
        {
            fixture.SeedState(Payload("logged out"));
            return Task.FromResult<RefreshPayload?>(new RefreshPayload(Payload("mailbox data"), 5));
        });

        RefreshResult result = await coordinator.RefreshAsync(racing, RefreshTrigger.Activation);

        // The generation compare under the mutex is what stops an in-flight request resurrecting
        // data after logout.
        Assert.Equal(RefreshOutcome.Discarded, result.Outcome);
        Assert.Equal(DeliveryRequestOutcome.NotRequested, result.Delivery);
        Assert.Equal("logged out", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
    }

    [Fact]
    public async Task A_manual_refresh_within_the_debounce_window_is_skipped_without_claiming_a_lease()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        RefreshResult first = await coordinator.RefreshAsync(
            StubFetcher.Returning("first"),
            RefreshTrigger.ManualAction);
        Assert.Equal(RefreshOutcome.Committed, first.Outcome);

        RefreshResult second = await coordinator.RefreshAsync(
            StubFetcher.Returning("second"),
            RefreshTrigger.ManualAction);

        Assert.Equal(RefreshOutcome.SkippedDebounce, second.Outcome);
        Assert.Equal("first", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));

        // Past the debounce, the same action works.
        fixture.Clock.Advance(CoordinationBounds.ManualRefreshDebounce + TimeSpan.FromSeconds(1));

        RefreshResult third = await coordinator.RefreshAsync(
            StubFetcher.Returning("third"),
            RefreshTrigger.ManualAction);

        Assert.Equal(RefreshOutcome.Committed, third.Outcome);
    }

    [Fact]
    public async Task Non_manual_triggers_are_not_debounced()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        // The debounce protects against a user clicking repeatedly; an activation or a sign-in is
        // not the user hammering a button and must not be swallowed.
        await coordinator.RefreshAsync(StubFetcher.Returning("a"), RefreshTrigger.ManualAction);

        RefreshResult activation = await coordinator.RefreshAsync(
            StubFetcher.Returning("b"),
            RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.Committed, activation.Outcome);
    }

    [Fact]
    public async Task A_wedged_peer_makes_the_refresh_skip_rather_than_proceed_unsynchronized()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("prior"));

        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(20),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        RefreshResult result = await coordinator.RefreshAsync(
            StubFetcher.Returning("new"),
            RefreshTrigger.Activation);

        // The claim itself timed out, so no Graph work was even attempted. Proceeding without a
        // lease would mean two unsynchronized refreshes.
        Assert.Equal(RefreshOutcome.SkippedContention, result.Outcome);
        Assert.Equal("prior", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.RefreshLeaseClaimTimedOut));
    }

    [Fact]
    public async Task Only_one_of_many_concurrent_refreshes_commits()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        var slow = new StubFetcher(async token =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), token);
            return new RefreshPayload(Payload("winner"), 5);
        });

        RefreshResult[] results = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ =>
                coordinator.RefreshAsync(slow, RefreshTrigger.Activation)));

        // Single-flight per account: the lease is a record, not a held lock, but it still admits
        // exactly one refresh at a time.
        Assert.Equal(1, results.Count(r => r.Outcome == RefreshOutcome.Committed));
        Assert.Equal(7, results.Count(r => r.Outcome == RefreshOutcome.SkippedLeaseHeld));
    }

    [Fact]
    public async Task Refresh_continuations_on_other_threads_do_not_break_mutex_affinity()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        // Forces the continuation after the awaited fetch onto a different thread than the one
        // that claimed the lease. If any named primitive were held across the await, the release
        // would throw ApplicationException for un-owned release. It does not, because the
        // critical sections are synchronous and the lease is an expiring record.
        var hopping = new StubFetcher(async _ =>
        {
            await Task.Yield();

            // CancellationToken.None deliberately: the point of this hop is to move the
            // continuation to another thread, not to test cancellation.
            await Task.Run(() => Thread.Sleep(20), CancellationToken.None);
            return new RefreshPayload(Payload("after a thread hop"), 5);
        });

        RefreshResult result = await coordinator.RefreshAsync(hopping, RefreshTrigger.Activation);

        Assert.Equal(RefreshOutcome.Committed, result.Outcome);
        Assert.False(fixture.Logger.Saw(Diagnostics.OperationalEventId.MutationLockAffinityViolated));

        // And the mutex is genuinely free afterwards.
        using MutationLock after = fixture.Mutex.Acquire();
        Assert.True(after.IsHeld);
    }

    [Fact]
    public async Task The_refresh_in_progress_indicator_follows_the_lease_and_clears_at_commit()
    {
        using var fixture = new CoordinationFixture();
        RefreshCoordinator coordinator = Build(fixture, new CountingDeliveryRequester());

        using var insideFetch = new ManualResetEventSlim(false);
        using var releaseFetch = new ManualResetEventSlim(false);

        var gated = new StubFetcher(_ =>
        {
            insideFetch.Set();
            releaseFetch.Wait(TimeSpan.FromSeconds(10), CancellationToken.None);
            return Task.FromResult<RefreshPayload?>(new RefreshPayload(Payload("done"), 5));
        });

        Task<RefreshResult> refresh = Task.Run(
            () => coordinator.RefreshAsync(gated, RefreshTrigger.ManualAction),
            CancellationToken.None);

        Assert.True(insideFetch.Wait(TimeSpan.FromSeconds(5)));
        Assert.True(coordinator.IsRefreshInProgress());

        releaseFetch.Set();
        RefreshResult result = await refresh;

        Assert.Equal(RefreshOutcome.Committed, result.Outcome);

        // Clearing at commit is correct: the data is fresh at that point and only its rendering
        // is still in flight.
        Assert.False(coordinator.IsRefreshInProgress());
    }
}
