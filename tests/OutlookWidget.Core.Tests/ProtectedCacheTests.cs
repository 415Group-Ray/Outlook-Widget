using System.Security.Cryptography;
using System.Text;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// DPAPI round-trip, generation semantics, atomic replacement under an open reader, and the
/// discard paths for corrupt or unsupported state.
/// </summary>
public sealed class ProtectedCacheTests
{
    private sealed class FailingProtector : IDataProtector
    {
        public byte[] Protect(byte[] payload, byte[] entropy) =>
            throw new CryptographicException("Current-user key unavailable.");

        public byte[] Unprotect(byte[] payload, byte[] entropy) =>
            throw new NotSupportedException();
    }

    private static byte[] Payload(string content) => Encoding.UTF8.GetBytes(content);

    [Fact]
    public void Round_trips_a_payload_and_advances_the_generation()
    {
        using var fixture = new CoordinationFixture();

        CacheReadResult before = fixture.Cache.Read();
        Assert.Equal(CacheReadStatus.Absent, before.Status);

        long generation = fixture.SeedState(Payload("first"));
        Assert.Equal(1, generation);

        CacheReadResult after = fixture.Cache.Read();
        Assert.Equal(CacheReadStatus.Success, after.Status);
        Assert.Equal(1, after.Generation);
        Assert.Equal("first", Encoding.UTF8.GetString(after.Payload!));

        long second = fixture.SeedState(Payload("second"));
        Assert.Equal(2, second);
        Assert.Equal("second", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
    }

    [Fact]
    public void Generation_is_readable_without_a_dpapi_round_trip()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("state"));

        // The delivery worker and render path compare generations on every pass. Making the
        // common case pay for a DPAPI unprotect would be a real cost on a glanceable widget.
        Assert.Equal(1, fixture.Cache.ReadGeneration());
    }

    [Fact]
    public void A_stale_expected_generation_is_refused_so_an_in_flight_refresh_cannot_resurrect_data()
    {
        using var fixture = new CoordinationFixture();

        long captured = fixture.Cache.ReadGeneration();

        // A logout, privacy change, or newer refresh lands while this operation's Graph I/O is
        // still in flight.
        fixture.SeedState(Payload("committed by someone else"));

        using MutationLock heldLock = fixture.Mutex.Acquire();
        CacheCommitResult result = fixture.Cache.Commit(heldLock, Payload("stale result"), captured);

        Assert.Equal(CacheCommitStatus.GenerationMismatch, result.Status);

        // The newer state survives untouched. This is the guard that stops an in-flight request
        // overwriting a newer setting or reviving a signed-out account.
        Assert.Equal(
            "committed by someone else",
            Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
    }

    [Fact]
    public void An_unconditional_commit_wins_regardless_of_the_generation()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("old"));

        // Logout and account switch are themselves authoritative and must not be refused
        // because a concurrent refresh moved the generation.
        using MutationLock heldLock = fixture.Mutex.Acquire();
        CacheCommitResult result = fixture.Cache.Commit(heldLock, Payload("new"), expectedGeneration: null);

        Assert.Equal(CacheCommitStatus.Success, result.Status);
        Assert.Equal(2, result.Generation);
    }

    [Fact]
    public void Clear_removes_the_payload_but_still_advances_the_generation()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("account data"));

        using (MutationLock heldLock = fixture.Mutex.Acquire())
        {
            CacheCommitResult cleared = fixture.Cache.Clear(heldLock);
            Assert.Equal(CacheCommitStatus.Success, cleared.Status);
            Assert.Equal(2, cleared.Generation);
        }

        CacheReadResult read = fixture.Cache.Read();

        // Cleared state must be distinguishable from a first run. If Clear deleted the file,
        // a peer would read Absent and could not tell a completed sign-out from a fresh
        // install — and a generation that did not advance would look like a transient read
        // failure, leaving the peer rendering its prior in-memory copy.
        Assert.Equal(2, read.Generation);
        Assert.Null(read.Payload);

        // Assert the exact status, not merely "not Absent".
        //
        // This assertion is why the test now catches a bug it previously missed. Clear() writes a
        // header with no payload, and the read path used to hand that empty remainder to DPAPI,
        // which rejected it — so a perfectly normal sign-out was reported as Corrupt. The old
        // assertions (not Absent, null payload) were both satisfied by Corrupt, so a real defect
        // passed. A cleared cache reported as corrupt means that once the disclosure tombstone
        // lifts, the delivery sink renders an error or recovery card instead of the signed-out
        // card the user just asked for.
        Assert.Equal(CacheReadStatus.Cleared, read.Status);
    }

    [Fact]
    public void A_cleared_cache_is_not_reported_as_corrupt_and_logs_no_corruption()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("account data"));

        using (MutationLock heldLock = fixture.Mutex.Acquire())
        {
            fixture.Cache.Clear(heldLock);
        }

        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);

        // Nothing about a deliberate sign-out is a discard or a corruption event, and logging it
        // as one would send someone hunting a cache bug that does not exist.
        Assert.False(fixture.Logger.Saw(Diagnostics.OperationalEventId.CacheDiscardedInvalid));
    }

    [Fact]
    public void A_DPAPI_protection_failure_becomes_a_failed_commit_and_preserves_prior_state()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("prior"));

        var failingCache = new ProtectedCache(
            fixture.Paths,
            fixture.Logger,
            new FailingProtector());

        CacheCommitResult result;

        using (MutationLock heldLock = fixture.Mutex.Acquire())
        {
            result = failingCache.Commit(
                heldLock,
                Payload("replacement"),
                expectedGeneration: 1);
        }

        Assert.Equal(CacheCommitStatus.Failed, result.Status);
        Assert.Equal(1, result.Generation);
        Assert.Equal("prior", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.StateCommitFailed));
    }

    [Fact]
    public void A_cleared_cache_survives_a_reread_by_a_separate_cache_instance()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("account data"));

        using (MutationLock heldLock = fixture.Mutex.Acquire())
        {
            fixture.Cache.Clear(heldLock);
        }

        // A restarted provider constructs its own ProtectedCache and must reach the same
        // conclusion, because the authoritative state is on disk rather than in memory.
        var restarted = new ProtectedCache(fixture.Paths, fixture.Logger);
        CacheReadResult read = restarted.Read();

        Assert.Equal(CacheReadStatus.Cleared, read.Status);
        Assert.Equal(2, read.Generation);
        Assert.Null(read.Payload);
    }

    [Fact]
    public void Corrupt_state_is_discarded_rather_than_rendered()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("valid"));

        // Truncate the protected payload, leaving the header intact.
        byte[] onDisk = File.ReadAllBytes(fixture.Paths.StateFilePath);
        File.WriteAllBytes(fixture.Paths.StateFilePath, onDisk[..(onDisk.Length - 20)]);

        CacheReadResult read = fixture.Cache.Read();

        Assert.Equal(CacheReadStatus.Corrupt, read.Status);
        Assert.Null(read.Payload);

        // Unvalidated strings must never reach a card, so the payload is withheld entirely
        // rather than partially decoded.
        Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.CacheDiscardedInvalid));
    }

    [Fact]
    public void A_file_with_the_wrong_magic_is_treated_as_corrupt()
    {
        using var fixture = new CoordinationFixture();
        Directory.CreateDirectory(fixture.Paths.RootDirectory);
        File.WriteAllBytes(fixture.Paths.StateFilePath, new byte[64]);

        Assert.Equal(CacheReadStatus.Corrupt, fixture.Cache.Read().Status);
    }

    [Fact]
    public void An_unsupported_format_version_is_discarded_and_not_migrated()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("valid"));

        byte[] onDisk = File.ReadAllBytes(fixture.Paths.StateFilePath);

        // Bump the stored format version past what this build understands.
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(onDisk.AsSpan(4, 4), 999);
        File.WriteAllBytes(fixture.Paths.StateFilePath, onDisk);

        CacheReadResult read = fixture.Cache.Read();

        // This cache is reconstructible from Graph, so there is no migration path and building
        // one would be machinery with no user-visible benefit.
        Assert.Equal(CacheReadStatus.UnsupportedVersion, read.Status);
        Assert.Null(read.Payload);
    }

    [Fact]
    public void A_reader_holding_the_snapshot_open_does_not_block_a_commit()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("original"));

        // Exactly how ProtectedCache.Read opens the file. Omitting FileShare.Delete is the
        // specific mistake that turns a correct-looking reader into the cause of a committer's
        // sharing violation, because File.Replace must delete or rename the destination.
        using (var reader = new FileStream(
            fixture.Paths.StateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete))
        {
            long generation = fixture.SeedState(Payload("replaced while open"));
            Assert.Equal(2, generation);

            // The reader still sees a complete file — the prior one — rather than a torn read.
            reader.Position = 0;
            byte[] buffer = new byte[reader.Length];
            int read = reader.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
            Assert.Equal(buffer.Length, read);
        }

        Assert.Equal("replaced while open", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
    }

    [Fact]
    public void A_reader_without_FileShare_Delete_provokes_the_bounded_retry_and_the_prior_snapshot_survives()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("original"));

        // A hostile handle: antivirus, indexing, or a debugger holding the destination without
        // permitting delete. This is the sharing violation the retry ladder exists for.
        using (new FileStream(
            fixture.Paths.StateFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read))
        {
            using MutationLock heldLock = fixture.Mutex.Acquire();
            CacheCommitResult result = fixture.Cache.Commit(
                heldLock,
                Payload("blocked"),
                expectedGeneration: null);

            Assert.Equal(CacheCommitStatus.ReplaceBlocked, result.Status);

            // The ladder was actually walked, not skipped.
            Assert.True(
                fixture.Logger.CountOf(Diagnostics.OperationalEventId.SnapshotReplaceRetried)
                    == CoordinationBounds.ReplaceRetryBackoff.Length,
                "The bounded replace ladder should be attempted exactly once per configured delay.");

            Assert.True(fixture.Logger.Saw(Diagnostics.OperationalEventId.SnapshotReplaceFailed));
        }

        // The prior valid snapshot is retained and refetch happens on the next trigger. An
        // exhausted ladder is not a corruption event.
        CacheReadResult after = fixture.Cache.Read();
        Assert.Equal(CacheReadStatus.Success, after.Status);
        Assert.Equal("original", Encoding.UTF8.GetString(after.Payload!));
        Assert.Equal(1, after.Generation);
    }

    [Fact]
    public void A_commit_without_the_mutex_is_refused()
    {
        using var fixture = new CoordinationFixture();

        using var peer = MutexHoldingPeer.Start(
            fixture.Paths.MutationMutexName,
            holdFor: TimeSpan.FromSeconds(20),
            workingDirectory: fixture.Root);

        peer.WaitUntilHolding(TimeSpan.FromSeconds(15));

        using MutationLock notHeld = fixture.Mutex.Acquire();
        Assert.False(notHeld.IsHeld);

        // try/catch rather than Assert.Throws: a ref struct cannot be captured in a lambda, which
        // is the same restriction that prevents it crossing an await.
        InvalidOperationException? thrown = null;

        try
        {
            fixture.Cache.Commit(notHeld, Payload("unprotected"), expectedGeneration: null);
        }
        catch (InvalidOperationException e)
        {
            thrown = e;
        }

        Assert.NotNull(thrown);

        // Nothing was written.
        Assert.Equal(CacheReadStatus.Absent, fixture.Cache.Read().Status);
    }

    [Fact]
    public void An_unreadable_generation_fails_a_clear_instead_of_restarting_the_counter()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("account a mailbox"));
        fixture.SeedState(Payload("account a mailbox, refreshed"));

        Assert.Equal(2, fixture.Cache.ReadGeneration());

        using (new FileStream(
                   fixture.Paths.StateFilePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            using MutationLock heldLock = fixture.Mutex.Acquire();
            CacheCommitResult result = fixture.Cache.Clear(heldLock);

            // Unconditional does not mean "at any generation". Deriving the next generation from a
            // counter that was not read is how the counter runs backwards, and every consumer that
            // compares generations to notice a change is reading a number that no longer orders.
            Assert.Equal(CacheCommitStatus.Failed, result.Status);

            // Failed before writing anything, not after. The old behaviour read zero, wrote a
            // temporary file for generation 1, and only then discovered it could not replace.
            Assert.False(File.Exists(fixture.Paths.StateTempFilePath));
        }

        // The prior snapshot and its generation are exactly as they were, which is what makes the
        // caller's retry-on-the-next-trigger behaviour correct.
        Assert.Equal(2, fixture.Cache.ReadGeneration());
        Assert.Equal(CacheReadStatus.Success, fixture.Cache.Read().Status);
    }

    [Fact]
    public void An_unreadable_generation_fails_an_unconditional_commit_for_the_same_reason()
    {
        using var fixture = new CoordinationFixture();
        fixture.SeedState(Payload("committed"));

        using (new FileStream(
                   fixture.Paths.StateFilePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            using MutationLock heldLock = fixture.Mutex.Acquire();

            CacheCommitResult result = fixture.Cache.Commit(
                heldLock, Payload("replacement"), expectedGeneration: null);

            Assert.Equal(CacheCommitStatus.Failed, result.Status);
            Assert.False(File.Exists(fixture.Paths.StateTempFilePath));
        }

        Assert.Equal(1, fixture.Cache.ReadGeneration());
    }

    [Fact]
    public void An_absent_file_is_generation_zero_rather_than_an_unreadable_one()
    {
        using var fixture = new CoordinationFixture();

        // The distinction the fail-on-unreadable rule depends on. A missing file genuinely is
        // generation zero, so a first commit and a first clear both have to be able to happen; only a
        // file that exists and will not open is unknown.
        using MutationLock heldLock = fixture.Mutex.Acquire();
        CacheCommitResult result = fixture.Cache.Clear(heldLock);

        Assert.Equal(CacheCommitStatus.Success, result.Status);
        Assert.Equal(1, result.Generation);
        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
    }

    [Fact]
    public void A_corrupt_header_is_generation_zero_so_unusable_state_can_be_overwritten()
    {
        using var fixture = new CoordinationFixture();

        // Present, openable, and carrying no recoverable counter. There is nothing to preserve and
        // nothing to order against, so overwriting from zero is both the only option and the right one.
        File.WriteAllBytes(fixture.Paths.StateFilePath, [0xFF, 0xFE, 0xFD, 0xFC]);

        using MutationLock heldLock = fixture.Mutex.Acquire();
        CacheCommitResult result = fixture.Cache.Commit(
            heldLock, Payload("rebuilt"), expectedGeneration: null);

        Assert.Equal(CacheCommitStatus.Success, result.Status);
        Assert.Equal(1, result.Generation);
        Assert.Equal("rebuilt", Encoding.UTF8.GetString(fixture.Cache.Read().Payload!));
    }

    [Fact]
    public void Orphaned_temporary_files_from_a_killed_commit_are_removed()
    {
        using var fixture = new CoordinationFixture();

        File.WriteAllBytes(fixture.Paths.StateTempFilePath, [1, 2, 3]);
        File.WriteAllBytes(fixture.Paths.StateBackupFilePath, [4, 5, 6]);

        using MutationLock heldLock = fixture.Mutex.Acquire();
        fixture.Cache.RemoveOrphanedTemporaryFiles(heldLock);

        Assert.False(File.Exists(fixture.Paths.StateTempFilePath));
        Assert.False(File.Exists(fixture.Paths.StateBackupFilePath));
    }
}
