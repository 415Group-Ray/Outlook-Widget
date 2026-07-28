using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The fail-closed disclosure path. These are the privacy tests: a failure here is data on
/// screen that should not be, not a stale number.
/// </summary>
public sealed class DisclosureTombstoneTests
{
    [Fact]
    public void No_suppression_by_default()
    {
        using var fixture = new CoordinationFixture();

        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.False(fixture.Tombstones.IsSuppressed());
    }

    [Fact]
    public void Suppression_is_active_from_the_moment_it_is_written_and_needs_no_mutex()
    {
        using var fixture = new CoordinationFixture();

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(20),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        // The whole point: with the mutex held by a wedged peer, no commit, no generation
        // increment, and no state-changed event is possible. Suppression must work anyway,
        // because a wedged peer is exactly when failing closed matters most.
        DisclosureSuppression suppression = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.False(suppression.IsCleared);
    }

    [Fact]
    public void Suppression_persists_when_the_commit_fails_and_clears_only_on_explicit_success()
    {
        using var fixture = new CoordinationFixture();

        DisclosureSuppression suppression = fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);
        Assert.Equal(DisclosureMode.CountsOnly, fixture.Tombstones.GetEffectiveMode());

        // Nothing automatic clears it. A caller that simply walks away leaves suppression in
        // place, which is why DisclosureSuppression is deliberately not IDisposable: `using`
        // would clear on every exit path, including the failure paths.
        Assert.Equal(DisclosureMode.CountsOnly, fixture.Tombstones.GetEffectiveMode());

        suppression.CommitAndClear();

        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.True(suppression.IsCleared);
    }

    [Fact]
    public void Enabling_more_disclosure_is_rejected_as_a_suppression()
    {
        using var fixture = new CoordinationFixture();

        // Switching "hide message details" back off needs no tombstone and commits normally.
        // There is no safety argument for pre-emptively revealing more.
        Assert.Throws<ArgumentOutOfRangeException>(() => fixture.Tombstones.Suppress(DisclosureMode.Full));
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void An_unreadable_suppression_file_fails_closed_to_signed_out()
    {
        using var fixture = new CoordinationFixture();

        fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);

        // Garbage in the file. Unlike the lease record, whose unreadable state must be
        // ignorable, an unreadable tombstone must suppress: the harm here is disclosing data
        // that should be hidden.
        string file = Directory.GetFiles(fixture.Paths.SuppressionDirectory, "*.suppress").Single();
        File.WriteAllText(file, "corrupted");

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void An_empty_suppression_file_fails_closed_to_signed_out()
    {
        using var fixture = new CoordinationFixture();

        File.WriteAllText(
            Path.Combine(fixture.Paths.SuppressionDirectory, Guid.NewGuid().ToString("N") + ".suppress"),
            string.Empty);

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void A_file_claiming_full_disclosure_is_treated_as_signed_out()
    {
        using var fixture = new CoordinationFixture();

        // No legitimate suppression file can claim Full. A file that does is either corrupt or
        // an attempt to weaken suppression, and both fail closed.
        File.WriteAllText(
            Path.Combine(fixture.Paths.SuppressionDirectory, Guid.NewGuid().ToString("N") + ".suppress"),
            "0\n2026-07-28T12:00:00.0000000+00:00\n1234\n");

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void A_file_surviving_a_crash_keeps_suppression_active()
    {
        using var fixture = new CoordinationFixture();

        // Written by an operation whose process then died. No handle, no owner, nobody to
        // clear it — and that is correct: it fails closed until an explicit action.
        File.WriteAllText(
            Path.Combine(fixture.Paths.SuppressionDirectory, Guid.NewGuid().ToString("N") + ".suppress"),
            "2\n2026-07-28T12:00:00.0000000+00:00\n9999\n");

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());

        // A fresh store — as a restarted provider would construct — reaches the same conclusion,
        // because the state is on disk rather than in memory.
        var restarted = new DisclosureTombstoneStore(fixture.Paths, fixture.Logger, fixture.Clock);
        Assert.Equal(DisclosureMode.SignedOut, restarted.GetEffectiveMode());
    }

    [Fact]
    public void Orphans_are_cleared_only_by_an_explicit_action()
    {
        using var fixture = new CoordinationFixture();

        File.WriteAllText(
            Path.Combine(fixture.Paths.SuppressionDirectory, Guid.NewGuid().ToString("N") + ".suppress"),
            "2\n2026-07-28T12:00:00.0000000+00:00\n9999\n");

        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());

        int removed = fixture.Tombstones.ClearAllOrphans();

        Assert.Equal(1, removed);
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void Overlapping_operations_each_delete_only_their_own_file()
    {
        using var fixture = new CoordinationFixture();

        // Operation A: a logout, suppressing to signed-out.
        DisclosureSuppression a = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        // Operation B: an account switch beginning before A resolves.
        DisclosureSuppression b = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        Assert.NotEqual(a.OperationId, b.OperationId);
        Assert.Equal(2, fixture.Tombstones.CountSuppressionFiles());

        // A succeeds and clears. With a single shared file this is where the bug would be:
        // A's success would remove the suppression B still needs, and B's later timeout would
        // then re-disclose the previous account's subjects.
        a.CommitAndClear();

        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());

        // Only once B resolves too does suppression lift.
        b.CommitAndClear();
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void The_effective_mode_is_the_strongest_present_not_the_most_recently_written()
    {
        using var fixture = new CoordinationFixture();

        DisclosureSuppression strong = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        // A weaker suppression written afterwards must not weaken the stronger one. Precedence
        // is computed by the reader at read time, so there is no read-modify-write to lose.
        DisclosureSuppression weak = fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());

        // Clearing the weaker one leaves the stronger in force.
        weak.CommitAndClear();
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());

        // And clearing the stronger one, with nothing else present, lifts suppression.
        strong.CommitAndClear();
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void The_weaker_operation_finishing_first_still_leaves_the_stronger_mode_in_force()
    {
        using var fixture = new CoordinationFixture();

        // The mirror of the previous case: order of writing and order of clearing both vary,
        // and neither may determine the effective mode.
        DisclosureSuppression weak = fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);
        DisclosureSuppression strong = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        weak.CommitAndClear();

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());
    }

    [Fact]
    public async Task Concurrent_overlapping_operations_never_leave_zero_files_while_one_is_pending()
    {
        using var fixture = new CoordinationFixture();

        const int operations = 24;
        var suppressions = new DisclosureSuppression[operations];
        var observedZeroWhilePending = false;

        // Every operation writes, then a randomly interleaved subset clears, while a watcher
        // samples the effective mode. There is no interleaving in which suppression lifts while
        // an operation is still outstanding, because each file is owned by exactly one writer.
        Parallel.For(0, operations, i =>
        {
            suppressions[i] = fixture.Tombstones.Suppress(
                i % 3 == 0 ? DisclosureMode.CountsOnly : DisclosureMode.SignedOut);
        });

        using var watcherDone = new CancellationTokenSource();

        Task watcher = Task.Run(
            () =>
            {
                while (!watcherDone.Token.IsCancellationRequested)
                {
                    if (fixture.Tombstones.GetEffectiveMode() == DisclosureMode.Full)
                    {
                        Volatile.Write(ref observedZeroWhilePending, true);
                        return;
                    }
                }
            },
            CancellationToken.None);

        // Clear all but one. The remaining operation is still pending throughout.
        Parallel.For(1, operations, i => suppressions[i].CommitAndClear());

        await watcherDone.CancelAsync();
        await watcher;

        Assert.False(
            Volatile.Read(ref observedZeroWhilePending),
            "Suppression lifted while an operation was still pending.");

        Assert.Equal(DisclosureMode.CountsOnly, fixture.Tombstones.GetEffectiveMode());

        suppressions[0].CommitAndClear();
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void Clearing_twice_is_harmless_and_does_not_touch_another_operation_file()
    {
        using var fixture = new CoordinationFixture();

        DisclosureSuppression a = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);
        DisclosureSuppression b = fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);

        a.CommitAndClear();
        a.CommitAndClear();

        // B's file must still be there. Deletion is by own ID only, so there is no conditional
        // delete, no compare step, and therefore no window between check and act.
        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());
        Assert.Equal(DisclosureMode.CountsOnly, fixture.Tombstones.GetEffectiveMode());
        Assert.False(b.IsCleared);
    }

    [Fact]
    public void A_missing_suppression_directory_is_not_suppression()
    {
        using var fixture = new CoordinationFixture();

        Directory.Delete(fixture.Paths.SuppressionDirectory, recursive: true);

        // An absent directory is a first run, not an unreadable state. Treating it as
        // suppression would leave a fresh install permanently showing the signed-out card.
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }
}
