using System.Runtime.InteropServices;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;

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
/// coordination design assumes <c>LocalApplicationData</c> is redirected into the package's own
/// store when running packaged, and that assumption has never been checked on this machine.
/// </para>
/// </remarks>
internal static partial class Program
{
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(IntPtr hWnd, string text, string caption, uint type);

    private const uint MB_OK = 0x0;
    private const uint MB_ICONINFORMATION = 0x40;

    [STAThread]
    private static int Main()
    {
        string report = BuildIdentityReport();

        // A message box rather than a window: it needs no XAML, no framework package, and no
        // message loop, so if it appears then package activation genuinely worked.
        MessageBox(IntPtr.Zero, report, "Outlook Inbox Widget — Phase 0 probe", MB_OK | MB_ICONINFORMATION);

        return 0;
    }

    private static string BuildIdentityReport()
    {
        var lines = new List<string>
        {
            "Phase 0 packaging probe. This is not the real companion app.",
            string.Empty,
        };

        string? packageFullName = TryGetCurrentPackageFullName();
        string? packageFamilyName = TryGetCurrentPackageFamilyName();

        if (packageFullName is null)
        {
            lines.Add("Package identity: NONE — running unpackaged.");
            lines.Add(
                "If this appears after installing the MSIX, the app was launched from its build "
                + "output rather than through the installed package.");
        }
        else
        {
            lines.Add("Package identity: present.");
            lines.Add($"Package full name: {packageFullName}");
            lines.Add($"Package family:    {packageFamilyName}");
        }

        lines.Add(string.Empty);

        // The family name, not LocalApplicationData, is what places packaged state. Measurement
        // on this machine showed LocalApplicationData is NOT redirected for a packaged full-trust
        // desktop app, so state located that way would survive uninstall — contradicting the
        // product's own privacy claim. CoordinationPaths.Resolve places it explicitly instead.
        CoordinationPaths paths = CoordinationPaths.Resolve(packageFamilyName);
        lines.Add("Coordination state root:");
        lines.Add(paths.RootDirectory);
        lines.Add(string.Empty);

        if (packageFamilyName is null)
        {
            lines.Add("Unpackaged, so this is the ordinary per-user path. Uninstall does not "
                      + "apply, because there is no package.");
        }
        else
        {
            bool insidePackageStore = paths.RootDirectory.Contains(
                Path.Combine("Packages", packageFamilyName),
                StringComparison.OrdinalIgnoreCase);

            lines.Add(insidePackageStore
                ? "Inside the package store, so uninstall removes it. This is what section 11 "
                  + "promises about cached mailbox data."
                : "WARNING: outside the package store. Cached mailbox data would survive "
                  + "uninstall, contradicting the stated privacy behaviour.");
        }

        lines.Add(string.Empty);
        lines.Add($"Bounds in force: mutex wait {CoordinationBounds.MutexWait.TotalSeconds:0}s, "
                  + $"async deadline {CoordinationBounds.AsyncDeadline.TotalSeconds:0}s, "
                  + $"lease horizon {CoordinationBounds.LeaseHorizon.TotalSeconds:0}s.");

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Returns the current package full name, or <see langword="null"/> when the process has no
    /// package identity.
    /// </summary>
    /// <remarks>
    /// <c>GetCurrentPackageFullName</c> returns <c>APPMODEL_ERROR_NO_PACKAGE</c> (15700) rather
    /// than failing when the process is unpackaged, which is how "am I packaged" is actually
    /// determined. Distinguishing that specific code from a real error matters: treating every
    /// failure as unpackaged would hide a genuine problem.
    /// </remarks>
    private static string? TryGetCurrentPackageFullName()
    {
        const int ErrorInsufficientBuffer = 122;
        const int AppModelErrorNoPackage = 15700;

        uint length = 0;
        int result;

        unsafe
        {
            // First call with a null buffer to learn the required length.
            result = GetCurrentPackageFullName(&length, null);
        }

        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer)
        {
            return $"(unexpected result {result} from GetCurrentPackageFullName)";
        }

        char[] buffer = new char[length];

        unsafe
        {
            fixed (char* pointer = buffer)
            {
                result = GetCurrentPackageFullName(&length, pointer);
            }
        }

        if (result != 0)
        {
            return $"(unexpected result {result} from GetCurrentPackageFullName)";
        }

        // The returned length includes the terminating null, which does not belong in the string.
        return new string(buffer, 0, (int)length - 1);
    }

    // Pointer parameters keep this signature fully blittable, so the generated stub needs no
    // runtime marshalling for the output buffer.
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static unsafe partial int GetCurrentPackageFullName(
        uint* packageFullNameLength,
        char* packageFullName);

    /// <summary>
    /// Returns the current package family name, or <see langword="null"/> when unpackaged.
    /// </summary>
    /// <remarks>
    /// The family name rather than the full name, because it is the stable part: the full name
    /// carries the version and architecture and changes with every build, while the family name
    /// is name plus publisher hash and is what names the per-package data store. State located by
    /// full name would move on every update and orphan the previous version's cache.
    /// </remarks>
    private static string? TryGetCurrentPackageFamilyName()
    {
        const int ErrorInsufficientBuffer = 122;
        const int AppModelErrorNoPackage = 15700;

        uint length = 0;
        int result;

        unsafe
        {
            result = GetCurrentPackageFamilyName(&length, null);
        }

        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer)
        {
            return null;
        }

        char[] buffer = new char[length];

        unsafe
        {
            fixed (char* pointer = buffer)
            {
                result = GetCurrentPackageFamilyName(&length, pointer);
            }
        }

        return result == 0 ? new string(buffer, 0, (int)length - 1) : null;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFamilyName")]
    private static unsafe partial int GetCurrentPackageFamilyName(
        uint* packageFamilyNameLength,
        char* packageFamilyName);
}
