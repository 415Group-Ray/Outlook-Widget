using System.Buffers.Binary;
using System.Security.Cryptography;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Caching;

internal interface IDataProtector
{
    byte[] Protect(byte[] payload, byte[] entropy);

    byte[] Unprotect(byte[] payload, byte[] entropy);
}

internal sealed class CurrentUserDataProtector : IDataProtector
{
    public static CurrentUserDataProtector Instance { get; } = new();

    private CurrentUserDataProtector()
    {
    }

    public byte[] Protect(byte[] payload, byte[] entropy) =>
        ProtectedData.Protect(payload, entropy, DataProtectionScope.CurrentUser);

    public byte[] Unprotect(byte[] payload, byte[] entropy) =>
        ProtectedData.Unprotect(payload, entropy, DataProtectionScope.CurrentUser);
}

/// <summary>Why a read of the protected state ended the way it did.</summary>
public enum CacheReadStatus
{
    /// <summary>A complete, current-format state was read and unprotected.</summary>
    Success,

    /// <summary>No state file exists yet.</summary>
    Absent,

    /// <summary>
    /// State was explicitly cleared by a logout, account switch, or cache-clear, and the
    /// generation advanced to say so. Authoritative, and deliberately distinct from both
    /// <see cref="Absent"/> and <see cref="Corrupt"/>.
    /// </summary>
    /// <remarks>
    /// This status has to exist. <see cref="ProtectedCache.Clear"/> writes a header with no
    /// payload, and without recognising that shape the read path would hand the empty remainder
    /// to DPAPI, get a <see cref="CryptographicException"/>, and report a perfectly normal
    /// sign-out as corruption — so once the disclosure tombstone lifted, the delivery sink would
    /// render an error or recovery card instead of the signed-out card.
    /// </remarks>
    Cleared,

    /// <summary>
    /// The file exists but its format version is not supported. This cache is
    /// reconstructible and has no migration path: discard and refetch.
    /// </summary>
    UnsupportedVersion,

    /// <summary>The file is truncated, corrupt, or fails DPAPI unprotection.</summary>
    Corrupt,

    /// <summary>The file could not be opened. Transient; the prior in-memory value stands.</summary>
    Unreadable,
}

/// <summary>The outcome of one lock-free read.</summary>
/// <param name="Status">Why the read ended as it did.</param>
/// <param name="Generation">
/// The committed generation, readable even when the payload is unusable, because it is
/// stored in the plaintext header.
/// </param>
/// <param name="Payload">The unprotected bytes, present only on success.</param>
public readonly record struct CacheReadResult(
    CacheReadStatus Status,
    long Generation,
    byte[]? Payload)
{
    public bool IsSuccess => Status == CacheReadStatus.Success;
}

/// <summary>Why a commit ended the way it did.</summary>
public enum CacheCommitStatus
{
    /// <summary>The new state replaced the old one and the generation advanced.</summary>
    Success,

    /// <summary>
    /// Committed state changed while this operation's I/O was in flight, so the result
    /// was discarded rather than allowed to resurrect superseded data.
    /// </summary>
    GenerationMismatch,

    /// <summary>
    /// The atomic replace remained blocked after the bounded retry ladder. The prior
    /// snapshot is intact and unchanged.
    /// </summary>
    ReplaceBlocked,

    /// <summary>The commit failed for another I/O reason. The prior snapshot is intact.</summary>
    Failed,
}

/// <summary>The outcome of one commit.</summary>
public readonly record struct CacheCommitResult(CacheCommitStatus Status, long Generation)
{
    public bool IsSuccess => Status == CacheCommitStatus.Success;
}

/// <summary>
/// DPAPI-protected local state with a generation counter, committed by write-then-atomic
/// replace and read without any lock.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads never wait.</b> A reader opens the file with
/// <c>FileShare.ReadWrite | FileShare.Delete</c>, which permits a Windows replace while
/// the reader still holds the prior file open. The reader therefore observes either the
/// prior complete state or the new complete state, never a partially written file, and
/// never waits for token acquisition or Graph I/O. Omitting <c>FileShare.Delete</c> is
/// the specific mistake that turns a correct-looking reader into the cause of a
/// committer's sharing violation, because <see cref="File.Replace(string, string, string?)"/>
/// must delete or rename the destination.
/// </para>
/// <para>
/// <b>Writes happen only under the mutation mutex, synchronously.</b> Every mutating
/// method requires a held <see cref="MutationLock"/> and contains no <c>await</c>, so
/// acquisition and release occur on one thread as <see cref="Mutex"/> requires.
/// </para>
/// <para>
/// <b>The header is plaintext and the payload is protected.</b> Format version and
/// generation sit in a small unencrypted header so a reader can compare generations
/// without a DPAPI round-trip on every render pass, and so a corrupt payload can still
/// be diagnosed. Neither field is mailbox data. Nothing is gained by protecting them:
/// DPAPI <c>CurrentUser</c> defends against other Windows users, not against the current
/// user, who could equally rewrite an encrypted header.
/// </para>
/// </remarks>
public sealed class ProtectedCache
{
    /// <summary>"OWSC" — Outlook Widget State Cache.</summary>
    private static readonly byte[] Magic = [0x4F, 0x57, 0x53, 0x43];

    /// <summary>
    /// Current format version. Increment on any layout change; the old file is then
    /// discarded rather than migrated.
    /// </summary>
    private const int CurrentFormatVersion = 1;

    /// <summary>magic(4) + version(4) + generation(8)</summary>
    private const int HeaderLength = 16;

    /// <summary>
    /// Additional DPAPI entropy. Not a secret — it ships in the binary — but it binds
    /// the ciphertext to this specific use, so a blob from elsewhere in the user's
    /// profile cannot be substituted and silently unprotected here.
    /// </summary>
    private static readonly byte[] Entropy = "OutlookWidget.ProtectedCache.v1"u8.ToArray();

    private readonly CoordinationPaths _paths;
    private readonly IOperationalLogger _logger;
    private readonly IDataProtector _protector;

    public ProtectedCache(CoordinationPaths paths, IOperationalLogger? logger = null)
        : this(paths, logger, CurrentUserDataProtector.Instance)
    {
    }

    internal ProtectedCache(
        CoordinationPaths paths,
        IOperationalLogger? logger,
        IDataProtector protector)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(protector);

        _paths = paths;
        _logger = logger ?? NullOperationalLogger.Instance;
        _protector = protector;
    }

    /// <summary>
    /// Reads and unprotects the committed state without taking any lock.
    /// </summary>
    public CacheReadResult Read()
    {
        byte[] raw;

        try
        {
            using var stream = new FileStream(
                _paths.StateFilePath,
                FileMode.Open,
                FileAccess.Read,
                // Both flags matter. ReadWrite lets a committer write; Delete lets it
                // replace or rename the destination while this handle is open.
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096,
                FileOptions.SequentialScan);

            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            raw = buffer.ToArray();
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new CacheReadResult(CacheReadStatus.Absent, 0, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.CacheReadFailed, OperationalOutcome.Failed);
            return new CacheReadResult(CacheReadStatus.Unreadable, 0, null);
        }

        if (raw.Length < HeaderLength || !raw.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            _logger.Record(OperationalEventId.CacheDiscardedInvalid, OperationalOutcome.Discarded);
            return new CacheReadResult(CacheReadStatus.Corrupt, 0, null);
        }

        int version = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4, 4));
        long generation = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(8, 8));

        if (version != CurrentFormatVersion)
        {
            _logger.Record(OperationalEventId.CacheDiscardedInvalid, OperationalOutcome.Discarded);
            return new CacheReadResult(CacheReadStatus.UnsupportedVersion, generation, null);
        }

        // A header with no payload is the explicit cleared representation written by Clear(), and
        // it must be recognised BEFORE unprotecting. DPAPI rejects an empty blob, so falling
        // through would report a successful sign-out as corruption.
        if (raw.Length == HeaderLength)
        {
            return new CacheReadResult(CacheReadStatus.Cleared, generation, null);
        }

        try
        {
            byte[] payload = _protector.Unprotect(
                raw.AsSpan(HeaderLength).ToArray(),
                Entropy);

            return new CacheReadResult(CacheReadStatus.Success, generation, payload);
        }
        catch (CryptographicException)
        {
            // Truncated, tampered with, or protected under a different user or machine
            // state. Reconstructible either way.
            _logger.Record(OperationalEventId.CacheDiscardedInvalid, OperationalOutcome.Discarded);
            return new CacheReadResult(CacheReadStatus.Corrupt, generation, null);
        }
    }

    /// <summary>
    /// Reads only the committed generation, without a DPAPI round-trip.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The provider's delivery worker and render path compare generations on every pass.
    /// Unprotecting a payload to answer "has anything changed" would make the common case
    /// pay for the rare one.
    /// </para>
    /// <para>
    /// Answers zero when the generation cannot be read, which is correct for every caller that
    /// only <em>compares</em> it: a conditional commit sees a mismatch and discards, and a render
    /// path keeps what it had. It is <b>not</b> correct for a caller that will write the value
    /// back, which is what <see cref="TryReadGeneration"/> exists for — see its remarks.
    /// </para>
    /// </remarks>
    public long ReadGeneration() =>
        TryReadGeneration(out long generation) == GenerationReadStatus.Unreadable ? 0 : generation;

    /// <summary>Whether the plaintext generation header could be read, and what it said.</summary>
    private enum GenerationReadStatus
    {
        /// <summary>The header was read, or is absent or corrupt in a way that means generation zero.</summary>
        Read,

        /// <summary>The file exists and could not be opened or read. The stored value is unknown.</summary>
        Unreadable,
    }

    /// <summary>
    /// Reads the committed generation, distinguishing "the stored value is zero" from "the stored
    /// value could not be determined".
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The two are not interchangeable for a writer, and treating them as one was a defect.</b>
    /// Every mutation here derives the next generation from the current one, so a transiently
    /// unreadable header — a sharing violation from antivirus or an indexer, the same interference
    /// the replace-retry ladder exists to absorb, but hit on the read instead of the replace — made
    /// an unconditional <see cref="Clear"/> stamp generation 1 over generation N. The counter then
    /// runs <em>backwards</em>, and every consumer that compares generations to decide whether
    /// anything changed is reading a number that no longer orders: conditional refresh commits
    /// discard against a value they can never match until the counter climbs back, and the
    /// <c>CustomState</c> round trip hands the Widgets host a generation older than one it was
    /// already given.
    /// </para>
    /// <para>
    /// So a writer that cannot read the current generation fails its commit and leaves the prior
    /// snapshot alone, which every caller already handles: a refresh retries on its next trigger, and
    /// a disclosure-reducing operation reports explicit failure while its tombstone keeps details
    /// hidden. Refusing to write costs a retry; writing a regressed counter costs the ordering the
    /// whole design rests on.
    /// </para>
    /// <para>
    /// An <em>absent</em> file is genuinely generation zero and stays a successful read, because a
    /// first commit has to be able to happen. A present file with a bad magic or a short header is
    /// also zero: it holds no usable state and no recoverable counter, so overwriting it from zero is
    /// the only thing available and the only thing correct.
    /// </para>
    /// </remarks>
    private GenerationReadStatus TryReadGeneration(out long generation)
    {
        generation = 0;

        try
        {
            using var stream = new FileStream(
                _paths.StateFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            Span<byte> header = stackalloc byte[HeaderLength];
            int read = stream.ReadAtLeast(header, HeaderLength, throwOnEndOfStream: false);

            if (read < HeaderLength || !header[..Magic.Length].SequenceEqual(Magic))
            {
                return GenerationReadStatus.Read;
            }

            generation = BinaryPrimitives.ReadInt64LittleEndian(header[8..16]);
            return GenerationReadStatus.Read;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            // Nothing has ever been committed. Zero is the true current generation, not a guess.
            return GenerationReadStatus.Read;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return GenerationReadStatus.Unreadable;
        }
    }

    /// <summary>
    /// Protects <paramref name="payload"/> and commits it, advancing the generation.
    /// </summary>
    /// <param name="heldLock">
    /// A mutation lock this thread already holds. Passed explicitly so that the
    /// requirement is visible at every call site rather than assumed, and so the
    /// <c>ref struct</c> cannot have been smuggled across an await to get here.
    /// </param>
    /// <param name="payload">The state to protect and store.</param>
    /// <param name="expectedGeneration">
    /// The generation observed before this operation's I/O began. If committed state has
    /// advanced past it, the commit is refused as
    /// <see cref="CacheCommitStatus.GenerationMismatch"/> — this is what prevents an
    /// in-flight refresh from resurrecting data after a logout or overwriting a newer
    /// setting. Pass <see langword="null"/> for an unconditional commit, which is correct
    /// for a state change that is itself authoritative, such as logout.
    /// </param>
    public CacheCommitResult Commit(
        in MutationLock heldLock,
        byte[] payload,
        long? expectedGeneration)
    {
        heldLock.ThrowIfNotHeld();
        ArgumentNullException.ThrowIfNull(payload);

        // Re-read under the mutex. The value captured before the awaited work is stale by
        // definition; this comparison is the one that counts.
        if (TryReadGeneration(out long currentGeneration) == GenerationReadStatus.Unreadable)
        {
            // The next generation cannot be derived from a value that was not read. Fail rather
            // than restart the counter from zero and make it run backwards; see TryReadGeneration.
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return new CacheCommitResult(CacheCommitStatus.Failed, currentGeneration);
        }

        if (expectedGeneration is long expected && currentGeneration != expected)
        {
            _logger.Record(
                OperationalEventId.RefreshDiscardedStateChanged,
                OperationalOutcome.Discarded);
            return new CacheCommitResult(CacheCommitStatus.GenerationMismatch, currentGeneration);
        }

        long nextGeneration = currentGeneration + 1;

        byte[] protectedPayload;

        try
        {
            protectedPayload = _protector.Protect(payload, Entropy);
        }
        catch (CryptographicException)
        {
            // DPAPI can fail when the current-user profile or key material is unavailable.
            // The prior snapshot is still authoritative, so surface a failed commit rather than
            // letting an implementation exception escape the refresh result contract.
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return new CacheCommitResult(CacheCommitStatus.Failed, currentGeneration);
        }

        byte[] file = new byte[HeaderLength + protectedPayload.Length];
        Magic.CopyTo(file, 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4, 4), CurrentFormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(8, 8), nextGeneration);
        protectedPayload.CopyTo(file, HeaderLength);

        return WriteAndReplace(file, nextGeneration);
    }

    /// <summary>
    /// Deletes the committed state and advances the generation, so a listener still sees
    /// a change rather than concluding nothing happened.
    /// </summary>
    /// <remarks>
    /// Used by logout, account switch, and explicit cache-clear. Advancing the generation
    /// on a delete matters: a peer that compares generations to decide whether to re-read
    /// would otherwise treat "file gone, generation unchanged" as a transient read failure
    /// and keep rendering its prior in-memory copy.
    /// </remarks>
    public CacheCommitResult Clear(in MutationLock heldLock)
    {
        heldLock.ThrowIfNotHeld();

        if (TryReadGeneration(out long currentGeneration) == GenerationReadStatus.Unreadable)
        {
            // Unconditional does not mean "at any generation". A clear whose advanced generation was
            // derived from an unread counter would regress it, and the listeners that compare
            // generations to notice the clear are exactly who that breaks. The caller reports failure
            // and, for a disclosure-reducing operation, its tombstone keeps details hidden meanwhile.
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return new CacheCommitResult(CacheCommitStatus.Failed, currentGeneration);
        }

        long nextGeneration = currentGeneration + 1;

        // A header-only file records the advanced generation with no payload, which is a
        // positive statement that state was cleared, and Read() reports it as
        // CacheReadStatus.Cleared. Deleting the file outright would be indistinguishable from a
        // first run, and a first run must not look like a completed sign-out.
        byte[] file = new byte[HeaderLength];
        Magic.CopyTo(file, 0);
        BinaryPrimitives.WriteInt32LittleEndian(file.AsSpan(4, 4), CurrentFormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(file.AsSpan(8, 8), nextGeneration);

        CacheCommitResult result = WriteAndReplace(file, nextGeneration);

        if (result.IsSuccess)
        {
            _logger.Record(OperationalEventId.CacheCleared, OperationalOutcome.Success);
        }

        return result;
    }

    /// <summary>
    /// Removes a temporary file orphaned by a process killed mid-commit. Called after an
    /// abandoned-mutex acquisition, where protected state is suspect.
    /// </summary>
    public void RemoveOrphanedTemporaryFiles(in MutationLock heldLock)
    {
        heldLock.ThrowIfNotHeld();

        foreach (string path in new[] { _paths.StateTempFilePath, _paths.StateBackupFilePath })
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A file that cannot be removed is harmless: the next commit overwrites
                // the temporary name, and the backup name is only ever written by
                // File.Replace.
            }
        }
    }

    /// <summary>
    /// Writes to the temporary name and moves it into place atomically, retrying a
    /// sharing violation on the bounded ladder while retaining the mutex.
    /// </summary>
    /// <remarks>
    /// The mutex is deliberately retained across the backoff. Releasing it between
    /// attempts would let a peer observe the window between the temporary write and the
    /// replace, which is exactly the half-committed state the whole design excludes.
    /// The ladder is short and fixed for the same reason: it must stay inside the
    /// non-cancellable critical section's construction-bounded budget.
    /// </remarks>
    private CacheCommitResult WriteAndReplace(byte[] fileBytes, long nextGeneration)
    {
        _paths.EnsureCreated();

        try
        {
            File.WriteAllBytes(_paths.StateTempFilePath, fileBytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return new CacheCommitResult(CacheCommitStatus.Failed, nextGeneration - 1);
        }

        int attempt = 0;

        while (true)
        {
            try
            {
                if (File.Exists(_paths.StateFilePath))
                {
                    // File.Replace preserves the destination's identity and is atomic
                    // from a reader's point of view. It requires the destination to
                    // exist, hence the branch.
                    File.Replace(
                        _paths.StateTempFilePath,
                        _paths.StateFilePath,
                        _paths.StateBackupFilePath,
                        ignoreMetadataErrors: true);

                    TryDelete(_paths.StateBackupFilePath);
                }
                else
                {
                    // First commit. Move is atomic within a volume.
                    File.Move(_paths.StateTempFilePath, _paths.StateFilePath, overwrite: false);
                }

                _logger.Record(OperationalEventId.StateCommitted, OperationalOutcome.Success);
                return new CacheCommitResult(CacheCommitStatus.Success, nextGeneration);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A sharing violation from an unrelated handle — antivirus, indexing, a
                // debugger — is transient and worth retrying. An exhausted ladder is not
                // an error state for the product: the prior snapshot is still valid and
                // the data is reconstructible on the next trigger.
                if (attempt >= CoordinationBounds.ReplaceRetryBackoff.Length)
                {
                    _logger.Record(
                        OperationalEventId.SnapshotReplaceFailed,
                        OperationalOutcome.Failed,
                        recordCount: attempt);

                    TryDelete(_paths.StateTempFilePath);
                    return new CacheCommitResult(CacheCommitStatus.ReplaceBlocked, nextGeneration - 1);
                }

                _logger.Record(
                    OperationalEventId.SnapshotReplaceRetried,
                    OperationalOutcome.Recovered,
                    recordCount: attempt + 1);

                // Thread.Sleep, not Task.Delay. This is inside a synchronous critical
                // section holding a thread-affine mutex; awaiting here is the defect the
                // design exists to prevent.
                Thread.Sleep(CoordinationBounds.ReplaceRetryBackoff[attempt]);
                attempt++;
            }
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing depends on this succeeding.
        }
    }
}
