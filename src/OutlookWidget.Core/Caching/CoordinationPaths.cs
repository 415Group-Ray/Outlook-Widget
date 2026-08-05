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
    /// Resolves the state location for the current process.
    /// </summary>
    /// <param name="packageFamilyName">
    /// The package family name when running packaged, or <see langword="null"/> when running
    /// unpackaged. The caller supplies it because determining package identity needs Win32
    /// interop, and this library stays free of it: the core is surface-agnostic and should not
    /// know that MSIX exists.
    /// </param>
    /// <param name="scope">Distinguishes one logical instance of the coordination state.</param>
    /// <remarks>
    /// <para>
    /// <b>Packaged state must be located explicitly, because it is not redirected for us.</b>
    /// An earlier version of this method assumed that
    /// <see cref="Environment.SpecialFolder.LocalApplicationData"/> is redirected into the
    /// package's own store when the process is packaged. That holds for UWP, and it was
    /// <em>measured to be false here</em>: the packaged full-trust companion resolved it to the
    /// ordinary <c>%LocalAppData%\OutlookWidget</c>, outside the package store entirely.
    /// </para>
    /// <para>
    /// That mattered for more than tidiness. Section 11 states that uninstall removes
    /// package-local cache and settings, and the troubleshooting guide repeats it. State written
    /// outside the package store survives uninstall, so a DPAPI-protected snapshot containing
    /// senders and subjects would have been left behind after the app was removed — a privacy
    /// claim the product would not actually have honoured.
    /// </para>
    /// <para>
    /// <c>LocalCache\Local</c> rather than <c>LocalState</c>: it is the conventional target for
    /// Win32-style local application data inside a package, so the path stays the same if
    /// Windows ever does apply that redirection, and it is semantically right for this content —
    /// a reconstructible, machine-local, DPAPI-bound cache that should neither roam nor migrate.
    /// </para>
    /// </remarks>
    public static CoordinationPaths Resolve(string? packageFamilyName, string scope = "v1")
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        string root = packageFamilyName is null
            // Unpackaged: unit tests and early Phase 0 work. Correct for that context, and the
            // only case where state is not removed by uninstall, because there is no package.
            ? Path.Combine(localAppData, "OutlookWidget")
            : Path.Combine(localAppData, "Packages", packageFamilyName, "LocalCache", "Local", "OutlookWidget");

        return new CoordinationPaths(root, scope);
    }

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

    /// <summary>
    /// Directory holding MSAL's own token cache. The same as the state root, so one uninstall
    /// removes cache, coordination state, and account metadata together.
    /// </summary>
    /// <remarks>
    /// The MSAL cache helper takes a directory and a file name separately rather than a path,
    /// which is why this is exposed as a pair as well as a combined path.
    /// </remarks>
    public string TokenCacheDirectory => RootDirectory;

    /// <summary>MSAL's token cache file name.</summary>
    public string TokenCacheFileName => $"msal-{_scope}.bin";

    /// <summary>
    /// MSAL's token cache, DPAPI-protected by the cache helper and shared by both processes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Placed here rather than where the MSAL documentation suggests.</b> That documentation
    /// offers <c>%LocalAppData%\{AppName}\msalcache.bin</c> as the MSIX example, which on this
    /// machine resolves outside the package store for the measured reason recorded on
    /// <see cref="Resolve"/> — so the account metadata would survive uninstall. Section 11 promises
    /// it does not.
    /// </para>
    /// <para>
    /// It holds ID tokens and account metadata, not the refresh token: with the broker enabled the
    /// refresh token stays device-bound inside WAM. That is why this file is a convenience for
    /// account discovery rather than the credential itself, and why losing it costs a fresh
    /// interactive sign-in and nothing more.
    /// </para>
    /// </remarks>
    public string TokenCacheFilePath => Path.Combine(TokenCacheDirectory, TokenCacheFileName);

    /// <summary>
    /// The terminal interactive authorization outcome, written by the companion for the provider.
    /// </summary>
    /// <remarks>
    /// Separate from the snapshot and not DPAPI-protected, because it holds one enum member and a
    /// timestamp rather than anything about a mailbox. See <c>AuthorizationStateStore</c> for why the
    /// provider cannot derive this state for itself.
    /// </remarks>
    public string AuthorizationStateFilePath =>
        Path.Combine(RootDirectory, $"authorization-{_scope}.json");

    /// <summary>
    /// The MSAL home-account identifier the user actually chose, written by the companion for the
    /// provider.
    /// </summary>
    /// <remarks>
    /// Separate from both the snapshot and the authorization record. It has to outlive a cleared
    /// snapshot — silent acquisition needs to know which account to ask for <em>before</em> any
    /// refresh has ever succeeded — and it says nothing about consent, so folding it into either
    /// would make that file mean two things. DPAPI-protected like the snapshot and unlike the
    /// authorization record, because section 4 step 6 requires it. See <c>SelectedAccountStore</c>.
    /// </remarks>
    public string SelectedAccountFilePath =>
        Path.Combine(RootDirectory, $"account-{_scope}.bin");

    /// <summary>Temporary file used to replace the selected-account record atomically.</summary>
    public string SelectedAccountTempFilePath =>
        Path.Combine(RootDirectory, $"account-{_scope}.tmp");

    /// <summary>The user's rendering preferences, written by the companion for the provider.</summary>
    /// <remarks>
    /// <para>
    /// <b>Its own file, and a deliberate deviation from section 3's diagram</b>, which places
    /// settings inside the DPAPI-protected envelope alongside the snapshot. That placement does not
    /// survive its own requirements: <c>ProtectedCache.Clear</c> writes a header and no payload, so
    /// a logout or account switch would erase the settings with the mailbox — and "hide message
    /// details" coming back on as *off* after signing in again is a privacy regression produced by
    /// storage layout. The same argument already applies to <see cref="SelectedAccountFilePath"/>,
    /// which is separate for the same reason: it has to outlive a cleared snapshot.
    /// </para>
    /// <para>
    /// Not DPAPI-protected, matching <see cref="AuthorizationStateFilePath"/>. It holds rendering
    /// preferences and nothing about a mailbox, so there is no content to protect — and the
    /// fail-closed read below means tampering can only ever hide more, never reveal more.
    /// </para>
    /// </remarks>
    public string SettingsFilePath => Path.Combine(RootDirectory, $"settings-{_scope}.json");

    /// <summary>Temporary file used to replace the settings record atomically.</summary>
    public string SettingsTempFilePath => Path.Combine(RootDirectory, $"settings-{_scope}.tmp");

    /// <summary>
    /// The operational diagnostics log both processes append to.
    /// </summary>
    /// <remarks>
    /// Inside the package store like everything else here, so uninstall removes it. It cannot
    /// contain mailbox or identity content, and that is a property of
    /// <see cref="Diagnostics.IOperationalLogger"/> rather than of any filtering done on the way
    /// out: the interface accepts closed enums and bounded numbers and has no string parameter, so
    /// there is nowhere for a subject line to enter.
    /// </remarks>
    public string DiagnosticsLogFilePath => Path.Combine(RootDirectory, $"diagnostics-{_scope}.log");

    /// <summary>The previous log, kept so a rollover does not discard the run that just failed.</summary>
    public string DiagnosticsLogPreviousFilePath =>
        Path.Combine(RootDirectory, $"diagnostics-{_scope}.1.log");

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
