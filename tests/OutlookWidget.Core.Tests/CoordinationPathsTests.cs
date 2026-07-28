using OutlookWidget.Core.Caching;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Where state is placed, and why the packaged case cannot be left to the platform.
/// </summary>
/// <remarks>
/// These exist because a measured assumption turned out to be false. The original code assumed
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/> is redirected into the package's
/// own store when the process is packaged. Running the packaged companion showed it resolving to
/// the ordinary <c>%LocalAppData%\OutlookWidget</c> instead — outside the package store, and
/// therefore surviving uninstall, which contradicts the product's stated privacy behaviour for
/// cached senders and subjects.
/// </remarks>
public sealed class CoordinationPathsTests
{
    private const string FamilyName = "415Group.OutlookInboxWidget_dgbvqhastx60y";

    private static string LocalAppData =>
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    [Fact]
    public void Packaged_state_lives_inside_the_package_store_so_uninstall_removes_it()
    {
        CoordinationPaths paths = CoordinationPaths.Resolve(FamilyName);

        string expectedPrefix = Path.Combine(LocalAppData, "Packages", FamilyName);

        Assert.StartsWith(expectedPrefix, paths.RootDirectory, StringComparison.OrdinalIgnoreCase);

        // Every file, not just the root, must be inside the store. A lease or suppression file
        // left outside it would keep a signed-out widget suppressed after a reinstall.
        foreach (string path in new[]
        {
            paths.StateFilePath,
            paths.StateTempFilePath,
            paths.StateBackupFilePath,
            paths.LeaseFilePath,
            paths.SuppressionDirectory,
        })
        {
            Assert.StartsWith(expectedPrefix, path, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void Packaged_state_is_not_placed_by_LocalApplicationData_alone()
    {
        CoordinationPaths packaged = CoordinationPaths.Resolve(FamilyName);
        CoordinationPaths unpackaged = CoordinationPaths.Resolve(null);

        // The specific regression: if these were ever equal, the packaged build would be writing
        // mailbox data outside its own store again.
        Assert.NotEqual(unpackaged.RootDirectory, packaged.RootDirectory);
    }

    [Fact]
    public void Unpackaged_state_uses_the_ordinary_per_user_path()
    {
        CoordinationPaths paths = CoordinationPaths.Resolve(null);

        Assert.Equal(Path.Combine(LocalAppData, "OutlookWidget"), paths.RootDirectory);
    }

    [Fact]
    public void The_family_name_rather_than_the_full_name_places_state()
    {
        // The family name is stable across versions; a full name carries version and
        // architecture. Locating state by full name would move it on every update and orphan the
        // previous version's cache and suppression files.
        CoordinationPaths first = CoordinationPaths.Resolve(FamilyName);
        CoordinationPaths second = CoordinationPaths.Resolve(FamilyName);

        Assert.Equal(first.StateFilePath, second.StateFilePath);
        Assert.DoesNotContain("0.1.0.0", first.RootDirectory, StringComparison.Ordinal);
    }

    [Fact]
    public void Scope_isolates_state_without_changing_the_root()
    {
        CoordinationPaths production = CoordinationPaths.Resolve(FamilyName);
        CoordinationPaths isolated = CoordinationPaths.Resolve(FamilyName, "test-scope");

        Assert.Equal(production.RootDirectory, isolated.RootDirectory);
        Assert.NotEqual(production.StateFilePath, isolated.StateFilePath);
        Assert.NotEqual(production.MutationMutexName, isolated.MutationMutexName);
        Assert.NotEqual(production.SuppressionDirectory, isolated.SuppressionDirectory);
    }

    [Fact]
    public void Named_primitives_are_user_scoped_rather_than_global()
    {
        CoordinationPaths paths = CoordinationPaths.Resolve(FamilyName);

        // The DPAPI CurrentUser scope of the state these guard means two Windows users have
        // separate state and must not serialize against each other. Global\ would also require
        // privileges this package does not request.
        Assert.DoesNotContain("Global", paths.MutationMutexName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Global", paths.StateChangedEventName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Global", paths.SuppressDetailsEventName, StringComparison.OrdinalIgnoreCase);
    }
}
