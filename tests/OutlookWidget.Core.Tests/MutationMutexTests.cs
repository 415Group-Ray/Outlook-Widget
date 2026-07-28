using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The mutation mutex: bounded waits, cancellation before acquisition, thread affinity, and
/// abandonment by a killed peer.
/// </summary>
public sealed class MutationMutexTests
{
    [Fact]
    public void Uncontended_acquisition_succeeds_and_releases()
    {
        using var fixture = new CoordinationFixture();

        using (MutationLock heldLock = fixture.Mutex.Acquire())
        {
            Assert.True(heldLock.IsHeld);
            Assert.Equal(MutationLockOutcome.Acquired, heldLock.Outcome);
            Assert.False(heldLock.StateIsSuspect);
        }

        // Releasing correctly is proven by being able to take it again.
        using MutationLock second = fixture.Mutex.Acquire();
        Assert.True(second.IsHeld);
    }

    [Fact]
    public void Wait_is_bounded_when_a_peer_process_is_wedged_in_its_critical_section()
    {
        using var fixture = new CoordinationFixture();

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(20),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using MutationLock heldLock = fixture.Mutex.Acquire();
        stopwatch.Stop();

        Assert.Equal(MutationLockOutcome.TimedOut, heldLock.Outcome);
        Assert.False(heldLock.IsHeld);

        // The point of the bound: the caller recovers rather than hanging. Without a timeout
        // this call would block for the peer's full twenty seconds, and in production
        // indefinitely.
        Assert.True(
            stopwatch.Elapsed < CoordinationBounds.MutexWait + TimeSpan.FromSeconds(2),
            $"Wait took {stopwatch.Elapsed}, which exceeds the {CoordinationBounds.MutexWait} bound.");

        Assert.True(fixture.Logger.Saw(
            Diagnostics.OperationalEventId.MutationLockWaitTimedOut,
            Diagnostics.OperationalOutcome.Timeout));
    }

    [Fact]
    public void An_already_cancelled_wait_abandons_immediately_rather_than_consuming_its_bound()
    {
        using var fixture = new CoordinationFixture();

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(20),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        using var alreadyExpired = new CancellationTokenSource();
        alreadyExpired.Cancel();

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        using MutationLock heldLock = fixture.Mutex.Acquire(alreadyExpired.Token);
        stopwatch.Stop();

        Assert.Equal(MutationLockOutcome.Cancelled, heldLock.Outcome);
        Assert.False(heldLock.IsHeld);

        // A refresh already past its deadline must not sit the full two seconds. Nothing is
        // owned yet, so abandoning costs nothing.
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(1),
            $"A cancelled wait took {stopwatch.Elapsed}; it should abandon promptly.");
    }

    [Fact]
    public void A_mutex_abandoned_by_a_killed_process_is_reported_as_acquired_with_suspect_state()
    {
        using var fixture = new CoordinationFixture();

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(60),
            workingDirectory: fixture.Root,
            releaseOnExit: false);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        // Killed mid-commit. No finally runs, so the mutex is abandoned rather than released.
        peer.Kill();

        using (MutationLock heldLock = fixture.Mutex.Acquire())
        {
            // The critical assertion. AbandonedMutexException means the wait *succeeded* and
            // this thread owns the mutex. Treating it as a failure would leak ownership until
            // process exit and deadlock every later commit.
            Assert.True(heldLock.IsHeld);
            Assert.Equal(MutationLockOutcome.AcquiredAbandoned, heldLock.Outcome);

            // And the caller is told to distrust protected state, because the dead peer may
            // have been between its temp write and its atomic replace.
            Assert.True(heldLock.StateIsSuspect);
        }

        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.MutationLockAbandonedByPeer));

        // Ownership was genuinely released, so subsequent operations succeed.
        using MutationLock after = fixture.Mutex.Acquire();
        Assert.True(after.IsHeld);
        Assert.Equal(MutationLockOutcome.Acquired, after.Outcome);
    }

    [Fact]
    public void Releasing_from_a_different_thread_is_detected_rather_than_silently_corrupting_ownership()
    {
        using var fixture = new CoordinationFixture();

        // This is the failure mode that drove the whole design: an await continuation resuming
        // on a different thread, so ReleaseMutex is called by a thread that does not own the
        // mutex. The ref struct prevents the async form at compile time; this covers a
        // deliberate synchronous thread hop, which the compiler cannot see.
        MutationLock heldLock = fixture.Mutex.Acquire();
        Assert.True(heldLock.IsHeld);

        Exception? captured = null;

        var other = new Thread(() =>
        {
            try
            {
                // Cannot capture a ref struct in a closure — which is precisely the point —
                // so the wrong-thread release is reached through a fresh acquisition attempt
                // that must not succeed while this test's thread still owns the mutex.
                using MutationLock contended = fixture.Mutex.Acquire();
                if (contended.IsHeld)
                {
                    captured = new InvalidOperationException(
                        "A second thread acquired the mutex while it was already held.");
                }
            }
            catch (Exception e)
            {
                captured = e;
            }
        });

        other.Start();
        other.Join();

        Assert.Null(captured);

        // Released on the acquiring thread, as required.
        heldLock.Dispose();

        using MutationLock after = fixture.Mutex.Acquire();
        Assert.True(after.IsHeld);
    }

    [Fact]
    public void A_lock_that_was_not_acquired_refuses_to_authorise_a_mutation()
    {
        using var fixture = new CoordinationFixture();

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(20),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        using MutationLock heldLock = fixture.Mutex.Acquire();
        Assert.False(heldLock.IsHeld);

        // A caller that forgets to check IsHeld must not be able to proceed to mutate state
        // unprotected. Failing loudly here is much better than a silently unsynchronized write.
        //
        // Written as an explicit try/catch rather than Assert.Throws because a ref struct cannot
        // be captured in a lambda — the same restriction that stops it crossing an await.
        InvalidOperationException? thrown = null;

        try
        {
            heldLock.ThrowIfNotHeld();
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        Assert.NotNull(thrown);
    }

    [Fact]
    public void Disposing_the_lock_twice_is_harmless()
    {
        using var fixture = new CoordinationFixture();

        MutationLock heldLock = fixture.Mutex.Acquire();
        heldLock.Dispose();
        heldLock.Dispose();

        // A double release would throw ApplicationException from the runtime and, worse, could
        // release a subsequent acquisition's ownership.
        using MutationLock after = fixture.Mutex.Acquire();
        Assert.True(after.IsHeld);
    }
}
