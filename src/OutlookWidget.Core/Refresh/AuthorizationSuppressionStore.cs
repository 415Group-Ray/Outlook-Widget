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

/// <summary>The outcome of one suppression read.</summary>
/// <param name="Status">Whether the value below is what was stored.</param>
/// <param name="DetailsWithheld">
/// Whether sender and subject must be withheld — the defaults when absent, and
/// <see langword="true"/> when unreadable, so a caller that ignores the status cannot disclose more
/// than it should.
/// </param>
public readonly record struct AuthorizationSuppressionResult(
    AuthorizationSuppressionStatus Status,
    bool DetailsWithheld);

/// <summary>
/// The durable record of whether the mailbox last refused this app's token.
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
/// <b>Only an authorized read may clear it.</b> Writing <see langword="false"/> is a claim that this
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
        try
        {
            // No File.Exists pre-check. It reports false for every failure it meets, so a
            // present-but-unreadable record would be classified absent, absent means not
            // suppressed, and the policy would invert. Absence is proven by the exception that
            // means absence and by nothing else.
            using var stream = new FileStream(
                _paths.AuthorizationSuppressionFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            Record? record = JsonSerializer.Deserialize<Record>(stream);

            return record is null
                ? new AuthorizationSuppressionResult(AuthorizationSuppressionStatus.Unreadable, true)
                : new AuthorizationSuppressionResult(
                    AuthorizationSuppressionStatus.Success,
                    record.DetailsWithheld);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AuthorizationSuppressionResult(AuthorizationSuppressionStatus.Absent, false);
        }
        catch (Exception e) when (
            e is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return new AuthorizationSuppressionResult(AuthorizationSuppressionStatus.Unreadable, true);
        }
    }

    /// <summary>
    /// Records the decision. Never throws.
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
    /// persist a *set* decision is not: this process withholds correctly, and a restart before the
    /// next attempt would not. That residual window is the reason the return value is surfaced
    /// rather than discarded.
    /// </para>
    /// </remarks>
    /// <returns><see langword="true"/> when the decision reached disk.</returns>
    public bool Write(bool detailsWithheld)
    {
        try
        {
            Directory.CreateDirectory(_paths.RootDirectory);

            byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
                new Record { DetailsWithheld = detailsWithheld });

            File.WriteAllBytes(_paths.AuthorizationSuppressionTempFilePath, payload);

            if (File.Exists(_paths.AuthorizationSuppressionFilePath))
            {
                File.Replace(
                    _paths.AuthorizationSuppressionTempFilePath,
                    _paths.AuthorizationSuppressionFilePath,
                    destinationBackupFileName: null);
            }
            else
            {
                File.Move(
                    _paths.AuthorizationSuppressionTempFilePath,
                    _paths.AuthorizationSuppressionFilePath);
            }

            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
            return false;
        }
    }
}
