using OutlookWidget.Core.Caching;
using OutlookWidget.Packaging;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Tests for the guard that stops an unpackaged process from placing state outside the package store.
/// </summary>
/// <remarks>
/// <para>
/// These cover a defect rather than a design: the provider treated a failed identity query as fatal
/// and a null family name as ordinary, passing the null into
/// <see cref="CoordinationPaths.Resolve(string?, string)"/> — which correctly answers with the
/// per-user path — and then created those directories. State there survives package uninstall, so
/// once mailbox data exists it would outlive the app that promised to remove it.
/// </para>
/// <para>
/// Both failure paths are reachable here only because the identity query is injectable. Neither can
/// be arranged otherwise: a test process cannot acquire package identity, and it cannot make
/// <c>GetCurrentPackageFamilyName</c> fail on demand. Testing this through the provider would mean
/// launching the COM server, which a unit test cannot do either.
/// </para>
/// </remarks>
public sealed class PackagedStateTests
{
    private const string FamilyName = "415Group.OutlookInboxWidget_dgbvqhastx60y";

    [Fact]
    public void A_packaged_process_resolves_state_inside_the_package_store()
    {
        PackagedStateResult result = PackagedState.Locate(() => FamilyName);

        Assert.Equal(PackagedStateStatus.Resolved, result.Status);
        Assert.True(result.IsResolved);
        Assert.Equal(FamilyName, result.PackageFamilyName);
        Assert.NotNull(result.Paths);

        // The location that matters: inside the per-package store, so uninstall removes it.
        Assert.Contains(
            Path.Combine("Packages", FamilyName),
            result.Paths!.RootDirectory,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_unpackaged_process_is_refused_rather_than_given_the_per_user_path()
    {
        // The defect. CoordinationPaths.Resolve(null) is correct in isolation and returns
        // %LocalAppData%\OutlookWidget; composing it with an identity query that may legitimately
        // return null is what produced a silent fallback.
        PackagedStateResult result = PackagedState.Locate(() => null);

        Assert.Equal(PackagedStateStatus.Unpackaged, result.Status);
        Assert.False(result.IsResolved);

        // No usable path is handed back. Returning the per-user path alongside a warning status
        // would leave the mistake one dereference away.
        Assert.Null(result.Paths);
        Assert.Null(result.PackageFamilyName);
    }

    [Fact]
    public void An_empty_family_name_is_treated_as_unpackaged()
    {
        // Defence against a query that reports success with nothing useful. An empty string would
        // otherwise build a path with an empty segment, which resolves somewhere real.
        Assert.Equal(PackagedStateStatus.Unpackaged, PackagedState.Locate(() => string.Empty).Status);
    }

    [Fact]
    public void A_failed_identity_query_is_refused_and_distinguished_from_unpackaged()
    {
        PackagedStateResult result = PackagedState.Locate(
            () => throw new PackageIdentityException("GetCurrentPackageFamilyName", 1234));

        // Kept distinct from Unpackaged deliberately: one is a broken query and the other means the
        // executable was started directly instead of activated from an installed package. Both fail
        // closed, but they send an operator to different places.
        Assert.Equal(PackagedStateStatus.IdentityQueryFailed, result.Status);
        Assert.False(result.IsResolved);
        Assert.Null(result.Paths);
    }

    [Fact]
    public void No_state_is_created_by_any_refusal()
    {
        // The ordering the defect got wrong: the previous code created directories before anything
        // could object, so a refusing process still left a footprint. Locate resolves nothing and
        // touches no filesystem, so there is nothing to clean up after a refusal.
        //
        // A UNIQUE scope, not the production "v1". The first version of this test asserted that the
        // production unpackaged suppression directory was absent, which made it fail on any account
        // where that directory already existed — including, with some irony, an account that had
        // exercised the very defect this test documents. It was passing here only because the
        // leftover directory had been cleaned up by hand minutes earlier. A scope that cannot
        // pre-exist removes the dependency on machine state entirely.
        string scope = "refusal-" + Guid.NewGuid().ToString("N");
        string suppression = CoordinationPaths.Resolve(packageFamilyName: null, scope).SuppressionDirectory;

        Assert.False(
            Directory.Exists(suppression),
            $"Precondition failed: {suppression} already exists, so this test could not distinguish "
                + "a refusal that created state from pre-existing state.");

        _ = PackagedState.Locate(() => null, scope);
        _ = PackagedState.Locate(() => throw new PackageIdentityException(), scope);

        Assert.False(
            Directory.Exists(suppression),
            $"A refused location created coordination state at {suppression}. Locate must not touch "
                + "the filesystem; only a resolved caller may call EnsureCreated.");
    }

    [Fact]
    public void The_scope_is_carried_through_to_the_resolved_paths()
    {
        // Tests pass their own scope so concurrent runs do not share files or contend on the same
        // named mutex. The guard must not quietly drop it.
        PackagedStateResult result = PackagedState.Locate(() => FamilyName, "unit-test-scope");

        Assert.True(result.IsResolved);
        Assert.Contains("unit-test-scope", result.Paths!.StateFilePath, StringComparison.Ordinal);
        Assert.Contains("unit-test-scope", result.Paths.MutationMutexName, StringComparison.Ordinal);
    }
}
