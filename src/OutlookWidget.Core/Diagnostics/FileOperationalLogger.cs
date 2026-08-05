using System.Globalization;
using System.Text;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Diagnostics;

/// <summary>
/// Appends operational events to a bounded file both processes share.
/// </summary>
/// <remarks>
/// <para>
/// <b>This cannot log mailbox or identity content, and no filtering is what stops it.</b>
/// <see cref="IOperationalLogger"/> accepts closed enums and bounded numbers and has no string
/// parameter anywhere, so a subject line has nowhere to enter. A redaction pass here would be
/// theatre: it could only remove things the API already cannot express. What this type adds is
/// durability and a size bound, not safety.
/// </para>
/// <para>
/// <b>It never throws.</b> Logging sits inside operations whose failure modes are carefully
/// defined — a refresh transaction, a disclosure change, the sole delivery thread — and an
/// exception escaping a log call would convert "the disk is full" into a failed sign-out or a dead
/// delivery worker. A diagnostic that can break the thing it is diagnosing is worse than no
/// diagnostic, so every failure here is swallowed.
/// </para>
/// <para>
/// <b>Bounded by rollover rather than by trimming.</b> At the size limit the current file becomes
/// the previous one and a new file starts, so the total on disk is at most twice the limit and the
/// run that just failed is not discarded to make room for the run investigating it. Trimming from
/// the front would mean rewriting the file under two processes that both append to it.
/// </para>
/// <para>
/// Both processes append to the same file. Interleaving between them is possible and accepted:
/// each record is one short line written in a single call, so the failure mode is ordering between
/// processes rather than a torn line — and a diagnostics log is not a coordination mechanism. The
/// authoritative state remains on disk elsewhere.
/// </para>
/// </remarks>
public sealed class FileOperationalLogger : IOperationalLogger
{
    /// <summary>
    /// The size at which the current log rolls over, in bytes.
    /// </summary>
    /// <remarks>
    /// Small on purpose. These records are a few dozen bytes each, so this holds thousands of
    /// events — far more than the "what just happened" question the companion's diagnostics button
    /// asks — while keeping the package store small enough that nobody has to think about it.
    /// </remarks>
    public const long RolloverBytes = 256 * 1024;

    private readonly CoordinationPaths _paths;
    private readonly Func<DateTimeOffset> _now;

    public FileOperationalLogger(CoordinationPaths paths, ISystemClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
        _now = clock is null
            ? () => DateTimeOffset.UtcNow
            : () => clock.UtcNow;
    }

    public void Record(
        OperationalEventId id,
        OperationalOutcome outcome,
        TimeSpan? duration = null,
        int? recordCount = null,
        int? httpStatusCode = null)
    {
        try
        {
            RollOverIfNeeded();

            File.AppendAllText(
                _paths.DiagnosticsLogFilePath,
                Compose(id, outcome, duration, recordCount, httpStatusCode),
                Encoding.UTF8);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // See the type remarks: a diagnostic must not break what it is diagnosing.
        }
    }

    /// <summary>Formats one record. Every field is an enum name or a number.</summary>
    private string Compose(
        OperationalEventId id,
        OperationalOutcome outcome,
        TimeSpan? duration,
        int? recordCount,
        int? httpStatusCode)
    {
        var line = new StringBuilder(96);

        // Round-trip UTC, so a log read on a machine in another timezone is not quietly reinterpreted.
        line.Append(_now().UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        line.Append(' ').Append(id);
        line.Append(' ').Append(outcome);

        if (duration is TimeSpan elapsed)
        {
            line.Append(" ms=").Append(
                ((long)elapsed.TotalMilliseconds).ToString(CultureInfo.InvariantCulture));
        }

        if (recordCount is int count)
        {
            line.Append(" n=").Append(count.ToString(CultureInfo.InvariantCulture));
        }

        if (httpStatusCode is int status)
        {
            line.Append(" http=").Append(status.ToString(CultureInfo.InvariantCulture));
        }

        return line.Append('\n').ToString();
    }

    private void RollOverIfNeeded()
    {
        var current = new FileInfo(_paths.DiagnosticsLogFilePath);

        if (!current.Exists || current.Length < RolloverBytes)
        {
            return;
        }

        // Move rather than copy-and-truncate: the other process may be appending, and a move that
        // loses the race simply fails, which the caller already treats as "no log this time".
        File.Move(_paths.DiagnosticsLogFilePath, _paths.DiagnosticsLogPreviousFilePath, overwrite: true);
    }
}
