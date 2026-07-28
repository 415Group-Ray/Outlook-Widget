namespace OutlookWidget.Core.Caching;

/// <summary>
/// Where coordination and cache state live, and the names of the shared kernel
/// objects that guard them.
/// </summary>
/// <remarks>
/// <para>
/// The companion and the provider are separate processes that must agree on all of
/// these exactly. Deriving them in one place means a mismatch is impossible rather
/// than merely unlikely — two processes each building <c>"snapshot.bin"</c> from
/// their own string literal is the kind of divergence that produces a silent
/// coordination failure rather than a build error.
/// </para>
/// <para>
/// The root is injectable so tests exercise the real file and mutex behaviour against
/// a temporary directory and uniquely named primitives, rather than mocking the very
/// mechanisms under test.
/// </para>
/// </remarks>
public sealed class CoordinationPaths
{
    /// <summary>
    /// Distinguishes one logical instance of the coordination state. In production
    /// this is a constant; tests give each case its own scope so concurrent test runs
    /// neither share files nor contend on the same named mutex.
    /// </summary>
    private readonly string _scope;

    public CoordinationPaths(string rootDirectory, string scope = "v1")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(scope);

        RootDirectory = rootDirectory;
        _scope = scope;
    }

    /// <summary>
    /// Resolves the production location: the package's per-user local data directory.
    /// </summary>
    /// <remarks>
    /// When the process is packaged, <c>LocalApplicationData</c> is redirected into the
    /// package's own local data store, which is what section 8 requires and what
    /// uninstall removes. Unpackaged — during unit testing and early Phase 0 work —
    /// it resolves to the ordinary per-user path, which is correct for that context.
    /// </remarks>
    public static CoordinationPaths ForCurrentUser() =>
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OutlookWidget"));

    public string RootDirectory { get; }

    /// <summary>The DPAPI-protected snapshot and settings envelope.</summary>
    public string StateFilePath => Path.Combine(RootDirectory, $"state-{_scope}.bin");

    /// <summary>Temporary file used for the write-then-atomic-replace commit.</summary>
    public string StateTempFilePath => Path.Combine(RootDirectory, $"state-{_scope}.tmp");

    /// <summary>Backup path required by <see cref="File.Replace(string, string, string?)"/>.</summary>
    public string StateBackupFilePath => Path.Combine(RootDirectory, $"state-{_scope}.bak");

    /// <summary>The refresh lease record.</summary>
    public string LeaseFilePath => Path.Combine(RootDirectory, $"refresh-lease-{_scope}.json");

    /// <summary>
    /// Directory holding one disclosure-suppression file per in-flight operation.
    /// Never a single shared file: see <c>DisclosureTombstoneStore</c> for why a shared
    /// file cannot be safely reclaimed.
    /// </summary>
    public string SuppressionDirectory => Path.Combine(RootDirectory, $"suppression-{_scope}");

    /// <summary>Guards synchronous local state commits. Bounded waits only.</summary>
    public string MutationMutexName => $"OutlookWidget-Mutation-{_scope}";

    /// <summary>
    /// Signalled after a committed state change. Payload-free: it says only that
    /// committed state changed, and every listener re-reads state for itself.
    /// </summary>
    public string StateChangedEventName => $"OutlookWidget-StateChanged-{_scope}";

    /// <summary>
    /// Signalled when a disclosure-reducing operation begins, before it attempts its
    /// commit. Independent of the mutation mutex, because a wedged peer is exactly when
    /// failing closed matters most.
    /// </summary>
    public string SuppressDetailsEventName => $"OutlookWidget-SuppressDetails-{_scope}";

    /// <summary>
    /// Creates the directories this instance needs. Safe to call repeatedly and from
    /// multiple processes.
    /// </summary>
    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(SuppressionDirectory);
    }
}
