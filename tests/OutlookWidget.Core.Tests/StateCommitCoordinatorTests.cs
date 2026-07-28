using System.Text;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Per-caller timeout behaviour under a wedged peer. The difference between the refresh path
/// and the disclosure path is the point: one may silently keep the prior snapshot, the other
/// must never silently no-op.
/// </summary>
public sealed class StateCommitCoordinatorTests
{
    private static byte[] Payload(string content) => Encoding.UTF8.GetBytes(content);

    [Fact]
    public void A_refresh_commit_under_a_wedged_peer_keeps_the_prior_snapshot_and_commits_nothing()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("prior"));

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(20),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        StateCommitResult result = fixture.Commits.CommitRefresh(
            new CommitSnapshotAction(fixture.Cache, Payload("new"), expectedGeneration: 1),
            CancellationToken.None);

        Assert.Equal(StateCommitOutcome.ContentionTimeout, result.Outcome);

        // One attempt only. A refresh has nothing to lose by waiting for its next trigger, and
        // retrying would push the refresh transaction past the budget the lease horizon covers.
        Assert.Equal(1, result.Attempts);

        Assert.Equal("prior", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
        Assert.Equal(1, fixture.Cache.ReadGeneration());
    }

    [Fact]
    public void A_disclosure_commit_under_a_wedged_peer_retries_once_then_reports_explicit_failure()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("previous account"));

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(30),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        StateCommitResult result = fixture.Commits.CommitDisclosureChange(
            new ClearStateAction(fixture.Cache));

        // Explicit failure, never a value a caller could mistake for success. A logout whose
        // commit was skipped and reported as done would leave the previous account's subjects
        // on screen — a privacy failure, not a staleness annoyance.
        Assert.Equal(StateCommitOutcome.ContentionTimeout, result.Outcome);
        Assert.False(result.IsCommitted);

        // Retried exactly once before giving up.
        Assert.Equal(2, result.Attempts);

        Assert.True(fixture.Logger.Saw(
            Diagnostics.OperationalEventId.SignOutFailed,
            Diagnostics.OperationalOutcome.Timeout));
    }

    [Fact]
    public void A_blocked_disclosure_commit_still_hides_details_because_the_tombstone_came_first()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("previous account subjects"));

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(30),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        // The logout sequence, in order. Suppression genuinely goes first, on a path that needs
        // no mutex, so that a timeout leaves safety intact rather than requiring a signal that
        // cannot be sent.
        DisclosureSuppression suppression = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        StateCommitResult result = fixture.Commits.CommitDisclosureChange(
            new ClearStateAction(fixture.Cache));

        Assert.Equal(StateCommitOutcome.ContentionTimeout, result.Outcome);

        // The commit failed, so the tombstone stays and the provider fails closed. This is the
        // case that a commit-only design gets wrong: with no generation increment and no
        // state-changed event, the provider would receive no signal at all and would keep
        // rendering the prior valid cache.
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.False(suppression.IsCleared);

        // The failed operation is over, but its marker must remain fail-closed. Completing the
        // handle unregisters only the live-operation guard so that a later, explicit recovery
        // action in this same process can remove the interrupted operation.
        suppression.CompleteWithoutClearing();
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());

        // The snapshot is still there — nothing was committed — which is precisely why the
        // tombstone rather than the cache has to be authoritative in this window.
        Assert.NotNull(fixture.Cache.Read().Payload);
    }

    [Fact]
    public void A_successful_disclosure_commit_clears_its_own_tombstone_after_the_commit()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("previous account"));

        DisclosureSuppression suppression = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());

        StateCommitResult result = fixture.Commits.CommitDisclosureChange(
            new ClearStateAction(fixture.Cache));

        Assert.True(result.IsCommitted);

        // Only now, with committed state itself saying signed-out, is it safe to lift the
        // override. Committed state is authoritative from this point.
        suppression.CommitAndClear();

        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.Null(fixture.Cache.Read().Payload);
    }

    [Fact]
    public void A_superseded_refresh_commit_is_discarded_rather_than_failed()
    {
        using var fixture = new CoordinationFixture();

        long captured = fixture.Cache.ReadGeneration();
        fixture.SeedState(Payload("newer state"));

        StateCommitResult result = fixture.Commits.CommitRefresh(
            new CommitSnapshotAction(fixture.Cache, Payload("stale"), captured),
            CancellationToken.None);

        // Discarded is the correct outcome for a superseded refresh, and it is deliberately a
        // different category from a failure: nothing went wrong, the result simply lost a race
        // it was always allowed to lose.
        Assert.Equal(StateCommitOutcome.Discarded, result.Outcome);
        Assert.Equal("newer state", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
    }

    [Fact]
    public void An_already_expired_deadline_cancels_the_commit_before_acquisition()
    {
        using var fixture = new CoordinationFixture();

        using var expired = new CancellationTokenSource();
        expired.Cancel();

        StateCommitResult result = fixture.Commits.CommitRefresh(
            new CommitSnapshotAction(fixture.Cache, Payload("late"), expectedGeneration: null),
            expired.Token);

        Assert.Equal(StateCommitOutcome.Cancelled, result.Outcome);
        Assert.Equal(CacheReadStatus.Absent, fixture.Cache.Read().Status);
    }

    [Fact]
    public void A_disclosure_commit_ignores_ambient_cancellation()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("previous account"));

        // CommitDisclosureChange takes no token by design. These operations are user-initiated
        // and privacy-relevant; cancelling one because some ambient deadline expired is how a
        // sign-out becomes a silent no-op. The compiler enforces this: there is no overload to
        // pass a token to.
        StateCommitResult result = fixture.Commits.CommitDisclosureChange(
            new ClearStateAction(fixture.Cache));

        Assert.True(result.IsCommitted);
    }

    [Fact]
    public void A_commit_after_an_abandoned_mutex_succeeds_and_cleans_up_orphaned_temporary_state()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("committed before the crash"));

        // A temp file left behind by a process killed between its write and its replace.
        File.WriteAllBytes(fixture.Paths.StateTempFilePath, [0xDE, 0xAD]);

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(60),
            workingDirectory: fixture.Root,
            releaseOnExit: false);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));
        peer.Kill();

        StateCommitResult result = fixture.Commits.CommitDisclosureChange(
            new ClearStateAction(fixture.Cache));

        // Abandonment must not crash and must not block recovery: the exception means ownership
        // was acquired, so the operation proceeds after validating state.
        Assert.True(result.IsCommitted);
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.MutationLockAbandonedByPeer));

        // No orphaned temporary state remains, so a later commit cannot pick up a half-written
        // file. The temp path is also consumed by the commit itself, so the backup path is the
        // one that proves the explicit cleanup ran.
        Assert.False(File.Exists(fixture.Paths.StateTempFilePath));
        Assert.False(File.Exists(fixture.Paths.StateBackupFilePath));
    }
}
