using System.Diagnostics;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Provider;

/// <summary>
/// Starts the companion application from a widget action.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why the provider needs this at all.</b> The provider authenticates silently and fails
/// closed: it has no reference or code path to interactive acquisition and must never open a
/// browser or show authentication UI. So every state it cannot resolve on its own — signed out,
/// sign-in required, broker unavailable, orphaned suppression needing explicit recovery — ends in
/// a card whose only action is to open the companion. This class is that action.
/// </para>
/// <para>
/// <b>Two candidates, documented first.</b> Shell activation of the package's own application
/// user model ID is the documented way to start a packaged application, and it gives real
/// activation semantics rather than a bare process launch. Starting the companion executable
/// directly is the fallback: both live in the same package, and a child process of a packaged
/// full-trust process inherits its package identity, so the companion still resolves
/// package-local state correctly. The fallback is layout-coupled — it depends on the sibling
/// directory <c>Build-Package.ps1</c> creates — which is precisely why it is second.
/// </para>
/// </remarks>
internal sealed class CompanionLauncher
{
    /// <summary>
    /// The companion's <c>Application Id</c> in the package manifest. Must match
    /// <c>&lt;Application Id="App"&gt;</c>; the application user model ID is the package family
    /// name, <c>!</c>, and this.
    /// </summary>
    private const string CompanionApplicationId = "App";

    /// <summary>
    /// The companion's directory inside the package, as assembled by the build script. Sibling to
    /// the provider's own directory.
    /// </summary>
    private const string CompanionDirectoryName = "OutlookWidget.App";

    private const string CompanionExecutableName = "OutlookWidget.App.exe";

    private readonly string? _packageFamilyName;
    private readonly IOperationalLogger _logger;
    private readonly Func<ProcessStartInfo, bool> _start;

    /// <param name="packageFamilyName">
    /// This package's family name, or <see langword="null"/> when running unpackaged. Null
    /// disables the shell-activation candidate rather than constructing a meaningless user model
    /// ID from an empty family name.
    /// </param>
    /// <param name="logger">Metadata-free operational logging.</param>
    public CompanionLauncher(string? packageFamilyName, IOperationalLogger? logger = null)
        : this(packageFamilyName, logger, DefaultStart)
    {
    }

    internal CompanionLauncher(
        string? packageFamilyName,
        IOperationalLogger? logger,
        Func<ProcessStartInfo, bool> start)
    {
        ArgumentNullException.ThrowIfNull(start);

        _packageFamilyName = packageFamilyName;
        _logger = logger ?? NullOperationalLogger.Instance;
        _start = start;
    }

    /// <summary>
    /// Tries each candidate in order and stops at the first accepted launch.
    /// </summary>
    /// <returns>Whether the companion was started.</returns>
    public bool Launch()
    {
        foreach (ProcessStartInfo startInfo in Candidates())
        {
            if (_start(startInfo))
            {
                return true;
            }
        }

        // Nothing further to try, and nothing useful to show: the card offering this action is
        // already the fallback state. Recorded so a repeated failure is visible in diagnostics
        // rather than presenting as an action that silently does nothing.
        _logger.Record(OperationalEventId.CompanionLaunchFailed, OperationalOutcome.Failed);
        return false;
    }

    private IEnumerable<ProcessStartInfo> Candidates()
    {
        if (_packageFamilyName is not null)
        {
            // UseShellExecute because a shell moniker is not an executable and cannot be started
            // by the plain CreateProcess path.
            yield return new ProcessStartInfo(
                $"shell:AppsFolder\\{_packageFamilyName}!{CompanionApplicationId}")
            {
                UseShellExecute = true,
            };
        }

        // AppContext.BaseDirectory is this provider's own directory inside the package, so the
        // companion is one level up and across. Derived rather than hard-coded as an absolute
        // path: the package install location contains the package full name, which carries the
        // version and changes on every update.
        string? providerDirectory = Path.GetDirectoryName(
            AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));

        if (providerDirectory is not null)
        {
            string companionPath = Path.Combine(
                providerDirectory, CompanionDirectoryName, CompanionExecutableName);

            yield return new ProcessStartInfo(companionPath)
            {
                UseShellExecute = false,

                // Lets the companion report that a widget action started it, which is how gate 6
                // is distinguished from the user launching it from Start.
                Arguments = "--from-widget",

                // Its own directory, so it never inherits the provider's as a relative base.
                WorkingDirectory = Path.Combine(providerDirectory, CompanionDirectoryName),
            };
        }
    }

    private static bool DefaultStart(ProcessStartInfo startInfo)
    {
        try
        {
            using Process? process = Process.Start(startInfo);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception
                                     or InvalidOperationException
                                     or PlatformNotSupportedException
                                     or FileNotFoundException
                                     or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
