using OutlookWidget.Core.Caching;

namespace OutlookWidget.Packaging;

/// <summary>Why locating packaged state ended as it did.</summary>
public enum PackagedStateStatus
{
    /// <summary>The process has package identity and state was located inside the package store.</summary>
    Resolved,

    /// <summary>
    /// The process has no package identity. Not an error condition to recover from — the caller must
    /// refuse to run.
    /// </summary>
    Unpackaged,

    /// <summary>
    /// The identity query itself failed, for a reason other than the process being unpackaged.
    /// Distinct from <see cref="Unpackaged"/> because one is a deployment mistake and the other is a
    /// broken query, and both must fail closed but they diagnose differently.
    /// </summary>
    IdentityQueryFailed,
}

/// <summary>The outcome of locating packaged state.</summary>
/// <param name="Status">Why the attempt ended as it did.</param>
/// <param name="Paths">
/// Where state lives, present only on <see cref="PackagedStateStatus.Resolved"/>. Deliberately
/// <see langword="null"/> otherwise: an unpackaged process must not be handed a usable path, because
/// the whole failure mode being prevented is one that quietly writes to the wrong place.
/// </param>
/// <param name="PackageFamilyName">The identity state was located under, or null.</param>
public readonly record struct PackagedStateResult(
    PackagedStateStatus Status,
    CoordinationPaths? Paths,
    string? PackageFamilyName)
{
    public bool IsResolved => Status == PackagedStateStatus.Resolved && Paths is not null;
}

/// <summary>
/// Locates coordination and cache state for a process that must be packaged, failing closed when it
/// is not.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this type exists.</b> <see cref="PackageIdentity.TryGetFamilyName"/> deliberately returns
/// <see langword="null"/> for an unpackaged process, and
/// <see cref="CoordinationPaths.Resolve(string?, string)"/> deliberately accepts null and answers
/// with the ordinary per-user path. Both behaviours are correct in isolation, and composing them
/// produces a silent fallback: a provider started without package identity resolved state to
/// <c>%LocalAppData%\OutlookWidget</c>, created the directories, and carried on. That is outside the
/// package store, so uninstall cannot remove it — which contradicts the stated privacy behaviour
/// that uninstall removes cached mailbox data.
/// </para>
/// <para>
/// The gap was invisible because the fatal case that <em>was</em> handled — a failed identity query —
/// sat directly under a comment explaining why falling back to the unpackaged path is unacceptable.
/// The reasoning was right and the null case simply was not covered. Putting the composition behind
/// one guarded call means the two behaviours cannot be recombined incorrectly at a new call site.
/// </para>
/// <para>
/// <b>Nothing is created here.</b> Directories are the caller's business, and only once the result is
/// <see cref="PackagedStateStatus.Resolved"/> — so a refusing process leaves no trace on disk. That
/// ordering is the point: the previous code created the directories before anything could object.
/// </para>
/// </remarks>
public static class PackagedState
{
    /// <summary>
    /// Locates state for the current process, which must be packaged.
    /// </summary>
    public static PackagedStateResult Locate(string scope = "v1") =>
        Locate(PackageIdentity.TryGetFamilyName, scope);

    /// <summary>
    /// Locates state using a supplied identity query.
    /// </summary>
    /// <param name="queryFamilyName">
    /// Returns the package family name, null when unpackaged, or throws
    /// <see cref="PackageIdentityException"/>. Injectable so both failure paths are testable without
    /// an installed package and without launching the COM server — neither of which a unit test can
    /// arrange.
    /// </param>
    /// <param name="scope">Distinguishes one logical instance of the coordination state.</param>
    internal static PackagedStateResult Locate(Func<string?> queryFamilyName, string scope = "v1")
    {
        ArgumentNullException.ThrowIfNull(queryFamilyName);

        string? packageFamilyName;

        try
        {
            packageFamilyName = queryFamilyName();
        }
        catch (PackageIdentityException)
        {
            return new PackagedStateResult(PackagedStateStatus.IdentityQueryFailed, null, null);
        }

        // The check this type exists for. Rejected before resolving a path, not after, so there is no
        // moment at which an unpackaged location has been computed and could be used by mistake.
        if (string.IsNullOrEmpty(packageFamilyName))
        {
            return new PackagedStateResult(PackagedStateStatus.Unpackaged, null, null);
        }

        return new PackagedStateResult(
            PackagedStateStatus.Resolved,
            CoordinationPaths.Resolve(packageFamilyName, scope),
            packageFamilyName);
    }
}
