using System.Runtime.InteropServices;

namespace OutlookWidget.Packaging;

/// <summary>
/// The current process's MSIX identity, or its absence.
/// </summary>
/// <remarks>
/// <para>
/// This exists as its own assembly because two executables need the same answer and
/// <c>OutlookWidget.Core</c> may not be the one to give it. <c>CoordinationPaths.Resolve</c>
/// takes the package family name as a parameter precisely so the core stays surface-agnostic
/// and free of any knowledge that MSIX exists. Duplicating the interop in the companion and
/// the provider would satisfy that rule and create a worse problem: two copies of a
/// two-call buffer protocol whose error codes are easy to get subtly wrong, drifting apart.
/// </para>
/// <para>
/// The family name is the value that matters. The full name carries the version and
/// architecture and changes on every build, so state located by it would move on every update
/// and orphan the previous version's cache and suppression files.
/// </para>
/// </remarks>
public static partial class PackageIdentity
{
    /// <summary>
    /// <c>APPMODEL_ERROR_NO_PACKAGE</c>. Returned rather than a failure when the process has no
    /// package identity, which is how "am I packaged" is actually determined. Distinguishing
    /// this specific code from a real error matters: treating every failure as unpackaged would
    /// hide a genuine problem behind a plausible-looking answer.
    /// </summary>
    private const int AppModelErrorNoPackage = 15700;

    private const int ErrorInsufficientBuffer = 122;
    private const int ErrorSuccess = 0;

    /// <summary>
    /// The package family name, or <see langword="null"/> when running unpackaged.
    /// </summary>
    /// <exception cref="PackageIdentityException">
    /// The query failed for a reason other than the process being unpackaged. Deliberately not
    /// collapsed into <see langword="null"/>: an unpackaged process and a broken query lead to
    /// different state locations, and silently choosing the unpackaged path would write cached
    /// mailbox data outside the package store, where uninstall cannot remove it.
    /// </exception>
    public static string? TryGetFamilyName() => Query(IdentityKind.FamilyName);

    /// <summary>
    /// The package full name, or <see langword="null"/> when running unpackaged. Diagnostics
    /// only — never use this to locate state.
    /// </summary>
    /// <exception cref="PackageIdentityException">As <see cref="TryGetFamilyName"/>.</exception>
    public static string? TryGetFullName() => Query(IdentityKind.FullName);

    /// <summary>Whether this process has package identity.</summary>
    /// <exception cref="PackageIdentityException">As <see cref="TryGetFamilyName"/>.</exception>
    public static bool IsPackaged() => TryGetFamilyName() is not null;

    private enum IdentityKind
    {
        FamilyName,
        FullName,
    }

    /// <summary>
    /// Runs the standard two-call Win32 buffer protocol: ask for the length, then ask again with
    /// a buffer of that length.
    /// </summary>
    /// <remarks>
    /// The two entry points share this method through an enum rather than a delegate. A delegate
    /// over these signatures is a pointer-typed delegate, which would make every call site an
    /// unsafe context and push <c>unsafe</c> out into the public methods. The enum keeps it here.
    /// </remarks>
    private static unsafe string? Query(IdentityKind kind)
    {
        string entryPoint = kind == IdentityKind.FamilyName
            ? nameof(GetCurrentPackageFamilyName)
            : nameof(GetCurrentPackageFullName);

        uint length = 0;
        int result = Invoke(kind, &length, null);

        if (result == AppModelErrorNoPackage)
        {
            return null;
        }

        if (result != ErrorInsufficientBuffer)
        {
            // Includes ERROR_SUCCESS with a zero-length buffer, which would mean the API
            // reported success for a name it never wrote.
            throw new PackageIdentityException(entryPoint, result);
        }

        char[] buffer = new char[length];

        fixed (char* pointer = buffer)
        {
            result = Invoke(kind, &length, pointer);
        }

        if (result != ErrorSuccess)
        {
            throw new PackageIdentityException(entryPoint, result);
        }

        if (length == 0)
        {
            throw new PackageIdentityException(entryPoint, result);
        }

        // The returned length counts the terminating null, which does not belong in the string.
        return new string(buffer, 0, (int)length - 1);
    }

    private static unsafe int Invoke(IdentityKind kind, uint* length, char* buffer) =>
        kind == IdentityKind.FamilyName
            ? GetCurrentPackageFamilyName(length, buffer)
            : GetCurrentPackageFullName(length, buffer);

    // Pointer parameters keep these signatures fully blittable, so the generated stubs need no
    // runtime marshalling for the output buffer.
    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFamilyName")]
    private static unsafe partial int GetCurrentPackageFamilyName(
        uint* packageFamilyNameLength,
        char* packageFamilyName);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentPackageFullName")]
    private static unsafe partial int GetCurrentPackageFullName(
        uint* packageFullNameLength,
        char* packageFullName);
}

/// <summary>
/// A package-identity query failed for a reason other than the process being unpackaged.
/// </summary>
public sealed class PackageIdentityException : Exception
{
    public PackageIdentityException(string entryPoint, int result)
        : base($"{entryPoint} returned {result}.")
    {
        EntryPoint = entryPoint;
        Result = result;
    }

    public PackageIdentityException()
    {
        EntryPoint = string.Empty;
    }

    public PackageIdentityException(string message)
        : base(message)
    {
        EntryPoint = string.Empty;
    }

    public PackageIdentityException(string message, Exception innerException)
        : base(message, innerException)
    {
        EntryPoint = string.Empty;
    }

    /// <summary>Which Win32 entry point failed.</summary>
    public string EntryPoint { get; }

    /// <summary>The Win32 result it returned.</summary>
    public int Result { get; }
}
