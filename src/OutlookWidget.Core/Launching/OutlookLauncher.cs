using System.Diagnostics;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Launching;

/// <summary>Which strategy started New Outlook, or that none could.</summary>
public enum OutlookLaunchStrategy
{
    /// <summary>Nothing was attempted, or every candidate failed.</summary>
    None,

    /// <summary>
    /// Bare <c>olk.exe</c> resolved through the app execution alias on <c>PATH</c>. Preferred:
    /// the alias is a reparse point that Windows maintains across package updates.
    /// </summary>
    AppExecutionAlias,

    /// <summary>
    /// Shell activation of the installed package's application user model ID. Needs no
    /// <c>PATH</c> lookup and no versioned path.
    /// </summary>
    PackageActivation,
}

/// <summary>The outcome of one launch attempt.</summary>
/// <param name="Strategy">Which candidate succeeded, or <see cref="OutlookLaunchStrategy.None"/>.</param>
/// <param name="Attempted">How many candidates were tried.</param>
public readonly record struct OutlookLaunchResult(OutlookLaunchStrategy Strategy, int Attempted)
{
    public bool IsSuccess => Strategy != OutlookLaunchStrategy.None;
}

/// <summary>
/// Starts New Outlook using the strategies Phase 0 measured on the target device, in the order
/// it recorded them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two candidates, deliberately, and no versioned path.</b> Section 9 forbids hard-coding
/// <c>C:\Program Files\WindowsApps\Microsoft.OutlookForWindows_&lt;version&gt;\...</c>, and the
/// reference machine shows exactly why: that path embeds the Outlook version, so it changes on
/// every Outlook update and a launcher built on it fails silently afterwards. Both strategies
/// here are version-independent. <c>scripts/Test-OutlookLaunch.ps1</c> is the script that
/// established both resolve, and this class preserves its preference order so the code and the
/// evidence cannot disagree.
/// </para>
/// <para>
/// <b>New Outlook only.</b> There is no Classic Outlook fallback and none may be added. If both
/// candidates fail, the caller degrades the Open Outlook action rather than reaching for a
/// different client.
/// </para>
/// <para>
/// <b>Launching is fire-and-forget.</b> A returned success means the launch request was accepted,
/// not that a window appeared: whether New Outlook is starting cold, already running, updating,
/// or damaged is not observable from here. That distinction is why gate 7 has a manual half.
/// </para>
/// </remarks>
public sealed class OutlookLauncher
{
    /// <summary>
    /// The app execution alias. A bare file name on purpose — resolving it is the shell's job,
    /// and writing any directory here would reintroduce the prohibited versioned path.
    /// </summary>
    private const string ExecutionAlias = "olk.exe";

    /// <summary>
    /// New Outlook's application user model ID: package family name, <c>!</c>, application id.
    /// Both halves are version-independent, which is what makes this a legitimate constant while
    /// the install path is not. Recorded from the reference machine's installed package manifest.
    /// </summary>
    private const string ApplicationUserModelId =
        "Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows";

    private readonly IOperationalLogger _logger;
    private readonly Func<ProcessStartInfo, bool> _start;

    public OutlookLauncher(IOperationalLogger? logger = null)
        : this(logger, DefaultStart)
    {
    }

    /// <param name="logger">Metadata-free operational logging.</param>
    /// <param name="start">
    /// Starts a process, returning whether it was accepted. Injectable so tests exercise
    /// candidate ordering and fallback without starting a mail client.
    /// </param>
    internal OutlookLauncher(IOperationalLogger? logger, Func<ProcessStartInfo, bool> start)
    {
        ArgumentNullException.ThrowIfNull(start);

        _logger = logger ?? NullOperationalLogger.Instance;
        _start = start;
    }

    /// <summary>
    /// Tries each candidate in order and stops at the first accepted launch.
    /// </summary>
    public OutlookLaunchResult Launch()
    {
        _logger.Record(OperationalEventId.OutlookLaunchAttempted, OperationalOutcome.Success);

        int attempted = 0;

        foreach ((OutlookLaunchStrategy strategy, ProcessStartInfo startInfo) in Candidates())
        {
            attempted++;

            if (_start(startInfo))
            {
                return new OutlookLaunchResult(strategy, attempted);
            }
        }

        // Both version-independent strategies failed. Section 17 degrades the Open Outlook action
        // to the approved web-only mode rather than treating this as fatal.
        _logger.Record(
            OperationalEventId.OutlookLaunchFailed,
            OperationalOutcome.Failed,
            recordCount: attempted);

        return new OutlookLaunchResult(OutlookLaunchStrategy.None, attempted);
    }

    private static IEnumerable<(OutlookLaunchStrategy Strategy, ProcessStartInfo StartInfo)> Candidates()
    {
        // UseShellExecute for both. The alias is a reparse point rather than a real executable and
        // the shell moniker is not an executable at all, so neither can be started by the plain
        // CreateProcess path.
        yield return (
            OutlookLaunchStrategy.AppExecutionAlias,
            new ProcessStartInfo(ExecutionAlias) { UseShellExecute = true });

        yield return (
            OutlookLaunchStrategy.PackageActivation,
            new ProcessStartInfo($"shell:AppsFolder\\{ApplicationUserModelId}") { UseShellExecute = true });
    }

    private static bool DefaultStart(ProcessStartInfo startInfo)
    {
        try
        {
            // A null return means the shell handed the request to an already-running instance,
            // which is a success rather than a failure and is the normal case when Outlook is
            // already open.
            using Process? process = Process.Start(startInfo);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                     or InvalidOperationException
                                     or PlatformNotSupportedException)
        {
            // File not found, no shell association, or activation refused. Try the next candidate.
            return false;
        }
    }
}
