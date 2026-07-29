using System.Runtime.InteropServices;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Packaging;

namespace OutlookWidget.App;

/// <summary>
/// A minimal packaged companion, sufficient to prove Phase 0 gate 1 and the companion-activation
/// half of the packaging work.
/// </summary>
/// <remarks>
/// <para>
/// This is not the companion described in section 3. It has no sign-in, no settings, no
/// diagnostics, and no WinUI. It exists so that a signed MSIX has something to contain and
/// something observable to launch, which is exactly what gate 1 needs and no more.
/// </para>
/// <para>
/// It does report two genuinely useful facts, because both are cheap here and awkward to
/// establish later: whether the process has package identity at all, and where the packaged
/// per-user local data directory actually resolves to. The second matters because the whole
/// coordination design assumed <c>LocalApplicationData</c> is redirected into the package's own
/// store when running packaged, and measurement on this machine showed it is not.
/// </para>
/// <para>
/// Since the provider exists, this probe is also how the widget action that launches the
/// companion is observed: gate 6 passes when clicking the widget's action makes this window
/// appear.
/// </para>
/// </remarks>
internal static partial class Program
{
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;

    [STAThread]
    private static int Main(string[] args)
    {
        string report = BuildIdentityReport(args);

        // A message box rather than a window: it needs no XAML, no framework package, and no
        // message loop, so if it appears then package activation genuinely worked.
        MessageBox(IntPtr.Zero, report, "Outlook Inbox Widget — Phase 0 probe", MB_OK | MB_ICONINFORMATION);

        return 0;
    }

    private static string BuildIdentityReport(string[] args)
    {
        var lines = new List<string>
        {
            "Phase 0 packaging probe. This is not the real companion app.",
            string.Empty,
        };

        // The provider passes an argument when it launches this, so gate 6 can distinguish
        // "the widget action started the companion" from the user starting it from Start.
        if (args.Length > 0)
        {
            lines.Add($"Launched with argument: {string.Join(' ', args)}");
            lines.Add(string.Empty);
        }

        // Both the unpackaged case and a failed query run through PackagedState, the same guarded
        // composition the provider uses. This probe is allowed to *report* that it is unpackaged —
        // that is half its purpose — but it must not be the place where a second, unguarded
        // combination of "identity may be null" and "Resolve accepts null" gets written.
        PackagedStateResult state = PackagedState.Locate();

        if (state.Status == PackagedStateStatus.IdentityQueryFailed)
        {
            lines.Add("Package identity: QUERY FAILED.");
            lines.Add("State location cannot be determined safely. Nothing was read or written.");
            return string.Join(Environment.NewLine, lines);
        }

        if (state.Status == PackagedStateStatus.Unpackaged)
        {
            lines.Add("Package identity: NONE — running unpackaged.");
            lines.Add(
                "If this appears after installing the MSIX, the app was launched from its build "
                + "output rather than through the installed package.");
            lines.Add(string.Empty);
            lines.Add("No coordination state path was resolved and nothing was created. The real "
                      + "companion will refuse to run in this state for the same reason the provider "
                      + "does: state outside the package store survives uninstall.");
            return string.Join(Environment.NewLine, lines);
        }

        string packageFamilyName = state.PackageFamilyName!;

        lines.Add("Package identity: present.");

        // The full name is diagnostic only. It carries the version, so it must never place state.
        try
        {
            lines.Add($"Package full name: {PackageIdentity.TryGetFullName()}");
        }
        catch (PackageIdentityException)
        {
            lines.Add("Package full name: unavailable (family name resolved, so state is placed).");
        }

        lines.Add($"Package family:    {packageFamilyName}");
        lines.Add(string.Empty);

        // The family name, not LocalApplicationData, is what places packaged state. Measurement
        // on this machine showed LocalApplicationData is NOT redirected for a packaged full-trust
        // desktop app, so state located that way would survive uninstall — contradicting the
        // product's own privacy claim. CoordinationPaths places it explicitly instead.
        CoordinationPaths paths = state.Paths!;
        lines.Add("Coordination state root:");
        lines.Add(paths.RootDirectory);
        lines.Add(string.Empty);

        bool insidePackageStore = paths.RootDirectory.Contains(
            Path.Combine("Packages", packageFamilyName),
            StringComparison.OrdinalIgnoreCase);

        lines.Add(insidePackageStore
            ? "Inside the package store, so uninstall removes it. This is what section 11 "
              + "promises about cached mailbox data."
            : "WARNING: outside the package store. Cached mailbox data would survive "
              + "uninstall, contradicting the stated privacy behaviour.");

        lines.Add(string.Empty);
        lines.Add($"Bounds in force: mutex wait {CoordinationBounds.MutexWait.TotalSeconds:0}s, "
                  + $"async deadline {CoordinationBounds.AsyncDeadline.TotalSeconds:0}s, "
                  + $"lease horizon {CoordinationBounds.LeaseHorizon.TotalSeconds:0}s.");

        return string.Join(Environment.NewLine, lines);
    }
}
