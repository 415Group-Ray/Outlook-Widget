using System.Diagnostics;
using System.Globalization;

namespace OutlookWidget.Core.Tests.TestInfrastructure;

/// <summary>
/// A genuinely separate process that acquires the named mutation mutex and holds it.
/// </summary>
/// <remarks>
/// <para>
/// A second thread in this process would also block a named-mutex waiter, so it could
/// simulate contention. It could not simulate what the plan actually requires proof of:
/// a peer <em>process</em> wedged inside a critical section, and — for the abandoned-mutex
/// case — a holder that dies without ever running a <c>finally</c>. Thread abandonment is
/// close, but a killed process is the real failure and it costs little to test the real one.
/// </para>
/// <para>
/// PowerShell is used as the peer rather than a dedicated helper executable because it needs
/// no separate project, no build ordering, and no deployment step, while still being a
/// distinct process with its own handle table.
/// </para>
/// </remarks>
internal sealed class MutexHoldingPeer : IDisposable
{
    private readonly Process _process;
    private readonly string _readyFilePath;
    private bool _disposed;

    private MutexHoldingPeer(Process process, string readyFilePath)
    {
        _process = process;
        _readyFilePath = readyFilePath;
    }

    /// <summary>
    /// Starts a peer that takes <paramref name="mutexName"/> and holds it for
    /// <paramref name="holdFor"/>, then releases it and exits.
    /// </summary>
    /// <param name="mutexName">
    /// The bare mutex name from <c>CoordinationPaths.MutationMutexName</c>. The
    /// <c>Local\</c> prefix is applied here, matching <c>MutationMutex</c>.
    /// </param>
    /// <param name="holdFor">
    /// How long the peer stays inside its critical section. Must comfortably exceed the
    /// bounded wait under test, or the test races the peer's release.
    /// </param>
    /// <param name="workingDirectory">A directory the peer may write its ready file into.</param>
    /// <param name="releaseOnExit">
    /// When false, the peer never releases: it holds and sleeps until killed. Use this for the
    /// abandoned-mutex case, where the point is that no <c>finally</c> ever runs.
    /// </param>
    public static MutexHoldingPeer Start(
        string mutexName,
        TimeSpan holdFor,
        string workingDirectory,
        bool releaseOnExit = true)
    {
        Directory.CreateDirectory(workingDirectory);
        string readyFile = Path.Combine(workingDirectory, $"peer-ready-{Guid.NewGuid():N}.flag");

        string holdMilliseconds = ((int)holdFor.TotalMilliseconds).ToString(CultureInfo.InvariantCulture);
        string release = releaseOnExit ? "$m.ReleaseMutex()" : "# deliberately never released";

        // Signal readiness only *after* the mutex is actually owned. Waiting on process start
        // instead would race: the test would begin its bounded wait before the peer had the
        // mutex, and the assertion would sometimes pass for the wrong reason.
        string script = string.Join(
            "; ",
            $"$m = New-Object System.Threading.Mutex($false, 'Local\\{mutexName}')",
            "$null = $m.WaitOne(10000)",
            $"Set-Content -LiteralPath '{readyFile}' -Value 'held'",
            $"Start-Sleep -Milliseconds {holdMilliseconds}",
            release);

        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add(script);

        Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start the peer process.");

        return new MutexHoldingPeer(process, readyFile);
    }

    /// <summary>
    /// Blocks until the peer confirms it owns the mutex.
    /// </summary>
    public void WaitUntilHolding(TimeSpan timeout)
    {
        DateTime deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(_readyFilePath))
            {
                return;
            }

            if (_process.HasExited)
            {
                throw new InvalidOperationException(
                    $"The peer process exited (code {_process.ExitCode}) before acquiring the mutex.");
            }

            Thread.Sleep(25);
        }

        throw new TimeoutException($"The peer process did not acquire the mutex within {timeout}.");
    }

    /// <summary>
    /// Kills the peer without letting it release, abandoning the mutex.
    /// </summary>
    public void Kill()
    {
        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(5000);
            }
        }
        catch (InvalidOperationException)
        {
            // Already gone.
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Kill();
        _process.Dispose();

        try
        {
            File.Delete(_readyFilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Test teardown only.
        }
    }
}
