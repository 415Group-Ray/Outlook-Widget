using System.Text.Json;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>
/// The durable record that a sign-in completed, paired with the best-effort event of the same name.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why both.</b> <see cref="SignInCompletedSignal"/> is precise and immediate, and it is the
/// normal path: the companion raises it the moment a sign-in succeeds and the provider forces one
/// Graph attempt. But a named event is an accelerant, not a fact — when the raise cannot open its
/// handle the evidence is gone, and the provider is left with a fresh snapshot, sticky authorization
/// suppression, and no reason to go to Graph. On the small card there is no Refresh action to break
/// out of that by hand.
/// </para>
/// <para>
/// This is the fact the event was announcing, written where a later read can find it. The provider
/// remembers the token it last acted on; a different one means a sign-in has happened since. Any
/// opportunity re-derives that — a Board activation, the five-minute active timer, an unrelated
/// signal — so a lost raise costs latency rather than convergence.
/// </para>
/// <para>
/// <b>An opaque token, not a timestamp or a counter.</b> The only question asked is whether it
/// differs from the last one acted on, which needs no ordering and therefore cannot be misread
/// after a clock step. A counter would additionally have to survive being read back and incremented
/// by two processes.
/// </para>
/// <para>
/// It says nothing about a mailbox, an account, or a tenant, so it is not DPAPI-protected — the same
/// reasoning as the authorization record.
/// </para>
/// </remarks>
public static class SignInCompletedRecord
{
    private sealed class Record
    {
        public Guid Token { get; init; }
    }

    /// <summary>
    /// Writes a new token, replacing any previous one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Never throws. This runs on a sign-in that has already succeeded, and failing to record the
    /// fact must not fail the sign-in the user just completed — the event may still deliver it, and
    /// the cost of losing both is a delayed recovery rather than a wrong one. The same reasoning
    /// the authorization record uses.
    /// </para>
    /// <para>
    /// Write-then-atomic-replace, so an interrupted write cannot leave a truncated file for the
    /// provider to read as an unfamiliar token and act on.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the token reached disk.</returns>
    public static bool Write(CoordinationPaths paths, IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            Directory.CreateDirectory(paths.RootDirectory);

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Record { Token = Guid.NewGuid() });

            File.WriteAllBytes(paths.SignInCompletedRecordTempFilePath, payload);

            if (File.Exists(paths.SignInCompletedRecordFilePath))
            {
                File.Replace(
                    paths.SignInCompletedRecordTempFilePath,
                    paths.SignInCompletedRecordFilePath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(
                    paths.SignInCompletedRecordTempFilePath,
                    paths.SignInCompletedRecordFilePath);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (logger ?? NullOperationalLogger.Instance)
                .Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return false;
        }
    }

    /// <summary>
    /// Records that the provider has acted on <paramref name="token"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Acknowledgement is durable because in-memory bookkeeping is not.</b> The provider used to
    /// remember which token it had handled, so every recycle, reboot, and package upgrade started
    /// with no memory of it and forced a Graph transaction against a historical sign-in over a
    /// perfectly fresh cache.
    /// </para>
    /// <para>
    /// <b>Its own file, written only by the provider.</b> Acknowledging by deleting the companion's
    /// record needed a read and a delete that were not one operation: a sign-in completing between
    /// them replaced the file, and the delete discarded a token that had never been acted on. Two
    /// single-writer files remove the window instead of locking around it, which matters on a path
    /// whose whole purpose is to work when other coordination has failed.
    /// </para>
    /// <para>
    /// Never throws, for the reason <see cref="Write"/> does not: a failure leaves the
    /// acknowledgement behind, which costs one redundant refresh rather than a lost recovery.
    /// </para>
    /// </remarks>
    public static void Acknowledge(CoordinationPaths paths, Guid token, IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            Directory.CreateDirectory(paths.RootDirectory);

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(new Record { Token = token });

            File.WriteAllBytes(paths.SignInAcknowledgedRecordTempFilePath, payload);

            if (File.Exists(paths.SignInAcknowledgedRecordFilePath))
            {
                File.Replace(
                    paths.SignInAcknowledgedRecordTempFilePath,
                    paths.SignInAcknowledgedRecordFilePath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(
                    paths.SignInAcknowledgedRecordTempFilePath,
                    paths.SignInAcknowledgedRecordFilePath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (logger ?? NullOperationalLogger.Instance)
                .Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
        }
    }

    /// <summary>
    /// The completed sign-in still awaiting a recovery attempt, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Recovery is owed when the companion's token differs from the provider's acknowledgement.
    /// Because each file has a single writer, neither read can observe a torn pairing: the worst
    /// case is reading an older acknowledgement against a newer token, which owes a refresh that
    /// has just been made — one redundant attempt rather than a lost one.
    /// </remarks>
    public static Guid? ReadOutstanding(CoordinationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (Read(paths) is not { } token)
        {
            return null;
        }

        return ReadToken(paths.SignInAcknowledgedRecordFilePath) == token ? null : token;
    }

    /// <summary>
    /// Reads the current token, or <see langword="null"/> when there is none to be sure of.
    /// </summary>
    /// <remarks>
    /// <b>Absent and unreadable both answer null, and that is not a fail-closed decision.</b> This
    /// value only ever schedules extra work; it never decides what may be displayed. Answering null
    /// means "no evidence of a sign-in", so the provider does not spend a Graph attempt on a file it
    /// could not read — the cost is a delayed recovery, not a disclosure. Reading it the other way
    /// would let an unreadable file force an attempt on every single check.
    /// </remarks>
    public static Guid? Read(CoordinationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        return ReadToken(paths.SignInCompletedRecordFilePath);
    }

    private static Guid? ReadToken(string path)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            Record? record = JsonSerializer.Deserialize<Record>(stream);

            return record is null || record.Token == Guid.Empty ? null : record.Token;
        }
        catch (Exception e) when (
            e is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            return null;
        }
    }
}
