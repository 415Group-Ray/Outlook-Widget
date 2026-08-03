using OutlookWidget.Core.Caching;
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

        // Owner PID 9999 is chosen to be almost certainly dead. A live owner is covered by the
        // test below.
        File.WriteAllText(
            Path.Combine(fixture.Paths.SuppressionDirectory, Guid.NewGuid().ToString("N") + ".suppress"),
            "2\n2026-07-28T12:00:00.0000000+00:00\n9999\n");

        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());

        int removed = fixture.Tombstones.ClearAllOrphans();

        Assert.Equal(1, removed);
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void Recovery_does_not_delete_the_marker_of_an_operation_still_in_flight()
    {
        using var fixture = new CoordinationFixture();

        // A logout is underway and has not yet resolved.
        DisclosureSuppression inFlight = fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        // Alongside it, a genuine orphan from a process that died.
        File.WriteAllText(
            Path.Combine(fixture.Paths.SuppressionDirectory, Guid.NewGuid().ToString("N") + ".suppress"),
            "1\n2026-07-28T12:00:00.0000000+00:00\n9999\n");

        Assert.Equal(2, fixture.Tombstones.CountSuppressionFiles());

        // The user clicks "clear interrupted operations" while the logout is still running.
        int removed = fixture.Tombstones.ClearAllOrphans();

        // Only the orphan goes. Deleting the live operation's marker would be the fail-closed
        // guarantee inverted by the very action meant to restore it: if that logout's commit then
        // timed out, the old snapshot would still hold the previous account's subjects with
        // nothing left suppressing them.
        Assert.Equal(1, removed);
        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.False(inFlight.IsCleared);

        // And the operation can still complete normally afterwards.
        inFlight.CommitAndClear();
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void Stores_for_the_same_path_share_the_active_operation_registry()
    {
        using var fixture = new CoordinationFixture();
        var recoveryStore = new DisclosureTombstoneStore(
            new CoordinationPaths(fixture.Paths.RootDirectory, fixture.Scope),
            fixture.Logger,
            fixture.Clock);

        DisclosureSuppression inFlight =
            fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        Assert.Equal(0, recoveryStore.ClearAllOrphans());
        Assert.Equal(1, recoveryStore.CountSuppressionFiles());
        Assert.Equal(DisclosureMode.SignedOut, recoveryStore.GetEffectiveMode());

        inFlight.CommitAndClear();
        Assert.Equal(DisclosureMode.Full, recoveryStore.GetEffectiveMode());
    }

    [Fact]
    public void A_completed_failure_keeps_suppression_but_becomes_eligible_for_explicit_recovery()
    {
        using var fixture = new CoordinationFixture();

        DisclosureSuppression failed =
            fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        failed.CompleteWithoutClearing();

        // Completing a failure must not itself re-disclose the previous snapshot.
        Assert.False(failed.IsCleared);
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());

        // It is no longer a live operation, so the same companion process can honor the user's
        // explicit "clear interrupted operations" action.
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());

        // Completion is terminal: an old handle cannot later delete a marker reused at the same
        // path or otherwise mutate recovered state.
        failed.CommitAndClear();
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public async Task Recovery_cannot_delete_a_marker_during_its_publication_window()
    {
        using var fixture = new CoordinationFixture();
        using var published = new ManualResetEventSlim(initialState: false);
        using var releasePublisher = new ManualResetEventSlim(initialState: false);
        Guid operationId = Guid.NewGuid();

        Task<DisclosureSuppression> publishing = Task.Run(() =>
            fixture.Tombstones.Suppress(
                DisclosureMode.SignedOut,
                operationId,
                afterMarkerPublished: () =>
                {
                    published.Set();
                    releasePublisher.Wait();
                }));

        Assert.True(published.Wait(TimeSpan.FromSeconds(5)));

        // Recovery starts after the marker is visible but before Suppress returns. It must wait
        // for the publication transaction, then observe the operation in the active registry.
        Task<int> recovery = Task.Run(fixture.Tombstones.ClearAllOrphans);
        await Task.Delay(100);
        Assert.False(recovery.IsCompleted);

        releasePublisher.Set();
        DisclosureSuppression suppression = await publishing;

        Assert.Equal(0, await recovery);
        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());

        suppression.CommitAndClear();
    }

    [Fact]
    public async Task Recovery_rechecks_operations_published_after_recovery_starts()
    {
        using var fixture = new CoordinationFixture();
        using var readyToEnumerate = new ManualResetEventSlim(initialState: false);
        using var continueRecovery = new ManualResetEventSlim(initialState: false);

        Task<int> recovery = Task.Run(() =>
            fixture.Tombstones.ClearAllOrphans(beforeEnumeration: () =>
            {
                readyToEnumerate.Set();
                continueRecovery.Wait();
            }));

        Assert.True(readyToEnumerate.Wait(TimeSpan.FromSeconds(5)));

        // Recovery has started but has not enumerated. Publish a live marker now. A stale snapshot
        // taken before this point would omit it and delete it when enumeration resumes.
        DisclosureSuppression suppression =
            fixture.Tombstones.Suppress(DisclosureMode.SignedOut);

        continueRecovery.Set();

        Assert.Equal(0, await recovery);
        Assert.Equal(1, fixture.Tombstones.CountSuppressionFiles());
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());

        suppression.CommitAndClear();
    }

    [Fact]
    public void Publication_failure_rolls_back_the_active_registration()
    {
        using var fixture = new CoordinationFixture();
        Guid operationId = Guid.NewGuid();
        string markerPath = Path.Combine(
            fixture.Paths.SuppressionDirectory,
            operationId.ToString("N") + ".suppress");
        string tempPath = markerPath + ".writing";

        // A directory at the temporary-file path forces publication to fail after registration.
        Directory.CreateDirectory(tempPath);

        Exception? failure = Record.Exception(() =>
            fixture.Tombstones.Suppress(DisclosureMode.CountsOnly, operationId));

        Assert.True(failure is IOException or UnauthorizedAccessException);

        Directory.Delete(tempPath);
        File.WriteAllText(
            markerPath,
            $"1\n2026-07-28T12:00:00.0000000+00:00\n{Environment.ProcessId}\n");

        // If rollback left the operation registered, recovery would preserve this synthetic
        // same-process orphan forever.
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public void Recovery_does_not_delete_a_marker_whose_owner_process_is_still_running()
    {
        using var fixture = new CoordinationFixture();

        // A genuinely separate live process must own the marker. Using this test's own PID would
        // instead exercise the "written by this process but no longer active" branch, which
        // correctly permits deletion — so it would prove the opposite of what is intended here.
        using var peer = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "powershell.exe",
            ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            File.WriteAllText(
                Path.Combine(fixture.Paths.SuppressionDirectory, Guid.NewGuid().ToString("N") + ".suppress"),
                $"2\n2026-07-28T12:00:00.0000000+00:00\n{peer.Id}\n");

            var other = new DisclosureTombstoneStore(fixture.Paths, fixture.Logger, fixture.Clock);

            int removed = other.ClearAllOrphans();

            // PID liveness is only ever used to DECLINE deletion, never to authorise it, because
            // it races PID reuse. Declining is the safe direction.
            Assert.Equal(0, removed);
            Assert.Equal(DisclosureMode.SignedOut, other.GetEffectiveMode());
        }
        finally
        {
            peer.Kill(entireProcessTree: true);
            peer.WaitForExit(5000);
        }
    }

    [Fact]
    public void Recovery_clears_a_marker_this_process_wrote_but_already_resolved()
    {
        using var fixture = new CoordinationFixture();

        DisclosureSuppression suppression = fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);
        Guid operationId = suppression.OperationId;

        // Simulate the file surviving after the operation left the active set — for instance a
        // delete that failed. It records this live process as owner, so the liveness guard alone
        // would refuse forever; the active-set check is what correctly allows it.
        suppression.CommitAndClear();
        File.WriteAllText(
            Path.Combine(fixture.Paths.SuppressionDirectory, operationId.ToString("N") + ".suppress"),
            $"1\n2026-07-28T12:00:00.0000000+00:00\n{Environment.ProcessId}\n");

        Assert.Equal(DisclosureMode.CountsOnly, fixture.Tombstones.GetEffectiveMode());

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
    public void A_failed_suppression_delete_can_be_retried_after_the_sharing_violation_clears()
    {
        using var fixture = new CoordinationFixture();
        DisclosureSuppression suppression = fixture.Tombstones.Suppress(DisclosureMode.CountsOnly);
        string path = Path.Combine(
            fixture.Paths.SuppressionDirectory,
            suppression.OperationId.ToString("N") + ".suppress");

        using (var blocker = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            suppression.CommitAndClear();

            Assert.False(suppression.IsCleared);
            // The exclusive handle also makes the marker temporarily unreadable, so the reader
            // fails closed to SignedOut. The key assertion is that suppression remains active and
            // the same handle can retry once the sharing violation clears.
            Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
            Assert.True(fixture.Logger.Saw(
                Diagnostics.OperationalEventId.DisclosureSuppressionCleared,
                Diagnostics.OperationalOutcome.Failed));
        }

        suppression.CommitAndClear();

        Assert.True(suppression.IsCleared);
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(0, fixture.Tombstones.CountSuppressionFiles());
    }

    [Fact]
    public void A_missing_suppression_directory_is_not_suppression()
    {
        using var fixture = new CoordinationFixture();

        Directory.Delete(fixture.Paths.SuppressionDirectory, recursive: true);

        // An absent directory is a first run, not an unreadable state. Treating it as
        // suppression would leave a fresh install permanently showing the signed-out card.
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(0, fixture.Tombstones.CountSuppressionFiles());
        Assert.Equal(
            DisclosureRecoveryStatus.DirectoryAbsent,
            fixture.Tombstones.ClearAllOrphansWithResult().Status);
    }

    [Fact]
    public void An_inaccessible_suppression_directory_fails_closed_instead_of_looking_absent()
    {
        using var fixture = new CoordinationFixture();
        var inaccessible = new DisclosureTombstoneStore(
            fixture.Paths,
            fixture.Logger,
            fixture.Clock,
            (_, _) => throw new UnauthorizedAccessException("Injected inaccessible directory."));

        Assert.Equal(DisclosureMode.SignedOut, inaccessible.GetEffectiveMode());
        Assert.Equal(-1, inaccessible.CountSuppressionFiles());
        Assert.Equal(
            new DisclosureRecoveryResult(DisclosureRecoveryStatus.Unreadable, 0),
            inaccessible.ClearAllOrphansWithResult());
        Assert.True(fixture.Logger.Saw(
            Diagnostics.OperationalEventId.DisclosureSuppressionEnumerationFailed,
            Diagnostics.OperationalOutcome.Failed));
    }
}
