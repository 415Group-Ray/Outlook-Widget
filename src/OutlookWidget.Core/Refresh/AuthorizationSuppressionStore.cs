using System.Text.Json;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>The stored answer to "must message details be withheld".</summary>
public enum AuthorizationSuppressionStatus
{
    /// <summary>The record was read and understood.</summary>
    Success,

    /// <summary>Nothing has been written. The mailbox has never refused this app.</summary>
    Absent,

    /// <summary>Present and unusable, or unreadable. The caller must fail closed.</summary>
    Unreadable,
}

/// <summary>The evidence that currently requires message details to remain withheld.</summary>
public enum AuthorizationSuppressionReason
{
    /// <summary>No authorization decision is withholding details.</summary>
    None,

    /// <summary>The record is unreadable or predates reason-aware persistence.</summary>
    Unknown,

    /// <summary>Silent authentication requires an interactive sign-in.</summary>
    InteractionRequired,

    /// <summary>Tenant policy requires administrator approval.</summary>
    ApprovalRequired,

    /// <summary>Graph returned HTTP 401.</summary>
    Unauthorized,

    /// <summary>Graph returned HTTP 403.</summary>
    Forbidden,

    /// <summary>The selected account has no supported mailbox.</summary>
    MailboxNotSupported,
}

/// <summary>The outcome of one suppression read.</summary>
/// <param name="Status">Whether the value below is what was stored.</param>
/// <param name="Reason">
/// Why sender and subject must be withheld. <see cref="AuthorizationSuppressionReason.None"/> is
/// returned when absent, and <see cref="AuthorizationSuppressionReason.Unknown"/> when unreadable,
/// so a caller that ignores the status cannot disclose more than it should.
/// </param>
public readonly record struct AuthorizationSuppressionResult(
    AuthorizationSuppressionStatus Status,
    AuthorizationSuppressionReason Reason)
{
    public bool DetailsWithheld => Reason != AuthorizationSuppressionReason.None;
}

/// <summary>
/// The durable reason that authentication or the mailbox last required message details to be withheld.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because a restart was disclosing what a 403 had withheld.</b> The decision was an
/// in-memory flag on the provider's presentation state, so a package upgrade or a provider recycle
/// recreated it as "not suppressed". The recovered-instance delivery that follows a restart runs
/// before any new Graph result, so it rendered the previously withheld senders and subjects, and a
/// snapshot still inside the freshness window could then skip Graph entirely — meaning nothing ever
/// re-established that the refusal had been resolved.
/// </para>
/// <para>
/// <b>Only an authorized read may clear it.</b> Writing
/// <see cref="AuthorizationSuppressionReason.None"/> is a claim that this
/// app read this mailbox successfully; nothing else — not a token acquisition, not a throttle, not a
/// restart — is evidence of that. The asymmetry is the whole point: setting it is cheap and
/// reversible, clearing it is a disclosure.
/// </para>
/// <para>
/// <b>Absent is not a failure and does not fail closed.</b> A fresh install has never been refused,
/// and there is also no cached mail for it to withhold. Absence is unambiguous in a way corruption
/// is not — the same distinction the settings store draws.
/// </para>
/// </remarks>
public sealed class AuthorizationSuppressionStore
{
    /// <summary>The stored shape. One required member, so an empty document is unreadable.</summary>
    /// <remarks>
    /// <c>required</c> for the reason the privacy setting carries it: a <c>bool</c> defaults to
    /// <see langword="false"/>, so a file corrupted into valid JSON would otherwise deserialize
    /// cleanly into "disclose everything" and be reported as a known value.
    /// </remarks>
    private sealed class Record
    {
        public required bool DetailsWithheld { get; init; }

        public AuthorizationSuppressionReason? Reason { get; init; }
    }

    private readonly CoordinationPaths _paths;
    private readonly IOperationalLogger _logger;

    public AuthorizationSuppressionStore(CoordinationPaths paths, IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>Reads the stored decision, or says why it could not.</summary>
    public AuthorizationSuppressionResult Read()
    {
        AuthorizationSuppressionResult primary = ReadRecord(_paths.AuthorizationSuppressionFilePath);
        AuthorizationSuppressionResult fallback =
            ReadRecord(_paths.AuthorizationSuppressionFallbackFilePath);

        if (primary.Status == AuthorizationSuppressionStatus.Unreadable
            || fallback.Status == AuthorizationSuppressionStatus.Unreadable)
        {
            return Unreadable();
        }

        // The fallback is written only after a primary write fails, so a withholding fallback is
        // newer than any primary record that survived that failure and carries the current reason.
        if (fallback is { Status: AuthorizationSuppressionStatus.Success, DetailsWithheld: true })
        {
            return fallback;
        }

        if (primary is { Status: AuthorizationSuppressionStatus.Success, DetailsWithheld: true })
        {
            return primary;
        }

        return primary.Status == AuthorizationSuppressionStatus.Success
            ? primary
            : new AuthorizationSuppressionResult(
                AuthorizationSuppressionStatus.Absent,
                AuthorizationSuppressionReason.None);
    }

    private static AuthorizationSuppressionResult ReadRecord(string path)
    {
        try
        {
            // No File.Exists pre-check. It reports false for every failure it meets, so a
            // present-but-unreadable record would be classified absent and invert the policy.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            Record? record = JsonSerializer.Deserialize<Record>(stream);

            if (record is null)
            {
                return Unreadable();
            }

            AuthorizationSuppressionReason reason = record.DetailsWithheld
                ? record.Reason ?? AuthorizationSuppressionReason.Unknown
                : AuthorizationSuppressionReason.None;

            if (!Enum.IsDefined(reason)
                || (!record.DetailsWithheld
                    && record.Reason is not (null or AuthorizationSuppressionReason.None)))
            {
                return Unreadable();
            }

            return new AuthorizationSuppressionResult(
                AuthorizationSuppressionStatus.Success,
                reason);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AuthorizationSuppressionResult(
                AuthorizationSuppressionStatus.Absent,
                AuthorizationSuppressionReason.None);
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return Unreadable();
        }
    }

    private static AuthorizationSuppressionResult Unreadable() =>
        new(AuthorizationSuppressionStatus.Unreadable, AuthorizationSuppressionReason.Unknown);

    /// <summary>
    /// Records the decision. Never throws for a valid enum value.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs inside the refresh path, where an exception would turn a full disk into a failed
    /// refresh. A failure is logged and swallowed, which leaves the in-memory decision protecting
    /// this process.
    /// </para>
    /// <para>
    /// <b>The two directions fail differently, and the asymmetry is worth knowing.</b> Failing to
    /// persist a *cleared* decision is harmless: the record still says withheld, so the next process
    /// is merely more conservative than it needs to be until the next successful read. Failing to
    /// persist a *set* decision to both the primary record and its fallback is not: this process
    /// withholds correctly, and a restart before the next attempt would not. That residual window is
    /// the reason the return value is surfaced rather than discarded.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the decision reached disk.</returns>
    public bool Write(AuthorizationSuppressionReason reason)
    {
        if (!Enum.IsDefined(reason))
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return false;
        }

        var record = new Record
        {
            DetailsWithheld = reason != AuthorizationSuppressionReason.None,
            Reason = reason,
        };

        if (TryWriteRecord(
                _paths.AuthorizationSuppressionFilePath,
                _paths.AuthorizationSuppressionTempFilePath,
                record))
        {
            if (reason != AuthorizationSuppressionReason.None)
            {
                return true;
            }

            return TryDeleteFallback();
        }

        // A failed recovery write is already safe: the old record remains withholding. A failed
        // withholding write is different, so preserve the reason in a dedicated marker that generic
        // interrupted-operation recovery never enumerates.
        return reason != AuthorizationSuppressionReason.None
            && TryWriteRecord(
                _paths.AuthorizationSuppressionFallbackFilePath,
                _paths.AuthorizationSuppressionFallbackTempFilePath,
                record);
    }

    private bool TryWriteRecord(string path, string temporaryPath, Record record)
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);
            File.WriteAllBytes(temporaryPath, JsonSerializer.SerializeToUtf8Bytes(record));

            if (File.Exists(path))
            {
                File.Replace(temporaryPath, path, destinationBackupFileName: null);
            }
            else
            {
                File.Move(temporaryPath, path);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return false;
        }
    }

    private bool TryDeleteFallback()
    {
        try
        {
            File.Delete(_paths.AuthorizationSuppressionFallbackFilePath);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return false;
        }
    }
}
