using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The durable diagnostics log.
/// </summary>
/// <remarks>
/// There is deliberately no test that the log excludes senders or subjects. It could not include
/// them: <see cref="IOperationalLogger"/> has no string parameter, so such a test would assert a
/// property of the interface at the wrong layer and pass no matter what this class did. What is
/// worth testing is the part that could actually go wrong — the size bound, and the promise never
/// to throw inside the operations this sits in the middle of.
/// </remarks>
public sealed class FileOperationalLoggerTests : IDisposable
{
    private readonly CoordinationFixture _fixture = new();
    private readonly CoordinationPaths _paths;
    private readonly FileOperationalLogger _logger;

    public FileOperationalLoggerTests()
    {
        _paths = _fixture.Paths;
        _logger = new FileOperationalLogger(_paths);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void A_record_is_one_line_of_enum_names_and_numbers()
    {
        _logger.Record(
            OperationalEventId.RefreshCompleted,
            OperationalOutcome.Success,
            duration: TimeSpan.FromMilliseconds(1360),
            recordCount: 5,
            httpStatusCode: 200);

        string[] lines = File.ReadAllLines(_paths.DiagnosticsLogFilePath);

        string line = Assert.Single(lines);
        Assert.Contains("RefreshCompleted", line, StringComparison.Ordinal);
        Assert.Contains("Success", line, StringComparison.Ordinal);
        Assert.Contains("ms=1360", line, StringComparison.Ordinal);
        Assert.Contains("n=5", line, StringComparison.Ordinal);
        Assert.Contains("http=200", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Optional_fields_are_omitted_rather_than_written_as_empty()
    {
        _logger.Record(OperationalEventId.DeliveryCompleted, OperationalOutcome.Success);

        string line = Assert.Single(File.ReadAllLines(_paths.DiagnosticsLogFilePath));

        Assert.DoesNotContain("ms=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("n=", line, StringComparison.Ordinal);
        Assert.DoesNotContain("http=", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Records_accumulate_in_order()
    {
        _logger.Record(OperationalEventId.SignOutRequested, OperationalOutcome.Success);
        _logger.Record(OperationalEventId.SignOutCompleted, OperationalOutcome.Success);

        string[] lines = File.ReadAllLines(_paths.DiagnosticsLogFilePath);

        Assert.Equal(2, lines.Length);
        Assert.Contains("SignOutRequested", lines[0], StringComparison.Ordinal);
        Assert.Contains("SignOutCompleted", lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void The_log_rolls_over_instead_of_growing_without_bound()
    {
        // Seed past the limit so the next record triggers the roll, rather than writing a quarter of
        // a megabyte one event at a time.
        File.WriteAllText(
            _paths.DiagnosticsLogFilePath,
            new string('x', (int)FileOperationalLogger.RolloverBytes + 1));

        _logger.Record(OperationalEventId.DeliveryCompleted, OperationalOutcome.Success);

        Assert.True(File.Exists(_paths.DiagnosticsLogPreviousFilePath));

        // The run that just failed is preserved rather than discarded to make room for the run
        // investigating it.
        Assert.True(new FileInfo(_paths.DiagnosticsLogPreviousFilePath).Length > FileOperationalLogger.RolloverBytes);

        string line = Assert.Single(File.ReadAllLines(_paths.DiagnosticsLogFilePath));
        Assert.Contains("DeliveryCompleted", line, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_rollover_replaces_the_previous_log_and_keeps_the_total_bounded()
    {
        for (int roll = 0; roll < 2; roll++)
        {
            File.WriteAllText(
                _paths.DiagnosticsLogFilePath,
                new string('x', (int)FileOperationalLogger.RolloverBytes + 1));

            _logger.Record(OperationalEventId.DeliveryCompleted, OperationalOutcome.Success);
        }

        long total = new FileInfo(_paths.DiagnosticsLogFilePath).Length
                     + new FileInfo(_paths.DiagnosticsLogPreviousFilePath).Length;

        Assert.True(
            total <= (FileOperationalLogger.RolloverBytes * 2) + 1024,
            $"Two files at most, each bounded by the rollover size. Total was {total}.");
    }

    [Fact]
    public void An_unwritable_log_never_throws()
    {
        // The promise that matters most. This sits inside a refresh transaction, a disclosure
        // change, and the sole delivery thread; an exception escaping here would turn a full disk
        // into a failed sign-out or a dead delivery worker.
        Directory.CreateDirectory(_paths.DiagnosticsLogFilePath);

        try
        {
            _logger.Record(OperationalEventId.DeliveryFailed, OperationalOutcome.Failed);
        }
        finally
        {
            Directory.Delete(_paths.DiagnosticsLogFilePath, recursive: true);
        }
    }

    [Fact]
    public void The_log_lives_in_the_state_root_so_uninstall_removes_it()
    {
        _logger.Record(OperationalEventId.DeliveryCompleted, OperationalOutcome.Success);

        Assert.Equal(
            Path.GetFullPath(_paths.RootDirectory),
            Path.GetFullPath(Path.GetDirectoryName(_paths.DiagnosticsLogFilePath)!));
    }
}
