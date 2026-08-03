using System.Globalization;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Refresh;

/// <summary>Whether explicit interrupted-operation recovery could inspect its directory.</summary>
public enum DisclosureRecoveryStatus
{
    Completed,
    DirectoryAbsent,
    Unreadable,
}

/// <summary>The bounded result of one explicit orphan-recovery attempt.</summary>
public readonly record struct DisclosureRecoveryResult(
    DisclosureRecoveryStatus Status,
    int RemovedCount);

/// <summary>
/// The fail-closed disclosure path: one suppression file per in-flight
/// disclosure-reducing operation, written before that operation attempts its commit.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists at all.</b> The mutation mutex cannot be the only route to a
/// fail-closed state, because a wedged peer is exactly when failing closed matters
/// most. If logout could hide details only by committing signed-out state,
/// incrementing the generation, and signalling the state-changed event, then a mutex
/// timeout would leave the provider with no signal and it would keep rendering the
/// prior valid cache — the precise opposite of the intended behaviour. So suppression
/// is written first, on a path that needs no mutex, and the risky mutation is
/// attempted afterwards. The safe state is already in place when the operation is
/// attempted, so a timeout leaves safety intact rather than requiring a signal that
/// cannot be sent.
/// </para>
/// <para>
/// <b>Why one file per operation and never a shared one.</b> A shared file cannot be
/// safely reclaimed. "Read the owner ID, then delete if it matches" is not an atomic
/// conditional delete: operation A can read its own ID, operation B can replace the
/// file, and A can then delete B's tombstone. A non-weakening read-modify-write has
/// the identical lost-update problem. Rather than add a lock to protect the very
/// mechanism that exists to survive a stuck lock, the sharing is removed — one writer,
/// one file, one deleter, and therefore no window between check and act.
/// </para>
/// <para>
/// <b>Fail-closed on every uncertainty.</b> An enumeration failure, or any file that is
/// present but unparseable, yields <see cref="DisclosureMode.SignedOut"/>. Unlike the
/// lease record, whose unreadable state must be ignorable, an unreadable tombstone must
/// suppress: the harm here is disclosing data that should be hidden.
/// </para>
/// <para>
/// A file left behind by a crashed or killed operation persists and keeps suppression
/// active, which is the correct direction. It is cleared by a later successful
/// disclosure-changing commit removing its own file, or by an explicit user action in
/// the companion, so recovery requires intent rather than happening silently.
/// </para>
/// </remarks>
public sealed class DisclosureTombstoneStore
{
    private const string FileExtension = ".suppress";

    private static readonly Lock RegistriesGate = new();
    private static readonly Dictionary<string, WeakReference<ActiveOperationRegistry>> Registries =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    private readonly CoordinationPaths _paths;
    private readonly IOperationalLogger _logger;
    private readonly ISystemClock _clock;
    private readonly Func<string, string, string[]> _enumerateFiles;

    /// <summary>
    /// Operations this process has suppressed and not yet cleared, shared by every store for
    /// the same canonical suppression path. Consulted by <see cref="ClearAllOrphans"/>, so that
    /// recovery through one service cannot delete another service's live operation marker.
    /// </summary>
    private readonly ActiveOperationRegistry _activeRegistry;

    public DisclosureTombstoneStore(
        CoordinationPaths paths,
        IOperationalLogger? logger = null,
        ISystemClock? clock = null)
        : this(paths, logger, clock, Directory.GetFiles)
    {
    }

    internal DisclosureTombstoneStore(
        CoordinationPaths paths,
        IOperationalLogger? logger,
        ISystemClock? clock,
        Func<string, string, string[]> enumerateFiles)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(enumerateFiles);

        _paths = paths;
        _logger = logger ?? NullOperationalLogger.Instance;
        _clock = clock ?? SystemClock.Instance;
        _enumerateFiles = enumerateFiles;
        _activeRegistry = GetActiveRegistry(paths.SuppressionDirectory);
    }

    /// <summary>
    /// Writes this operation's suppression file and signals the suppress-details event.
    /// Takes no mutex and must never be made to.
    /// </summary>
    /// <returns>
    /// A handle that deletes only this operation's own file, and only when the caller
    /// explicitly commits it after a successful state commit.
    /// </returns>
    public DisclosureSuppression Suppress(DisclosureMode mode) =>
        Suppress(mode, Guid.NewGuid());

    internal DisclosureSuppression Suppress(
        DisclosureMode mode,
        Guid operationId,
        Action? afterMarkerPublished = null)
    {
        if (mode == DisclosureMode.Full)
        {
            throw new ArgumentOutOfRangeException(
                nameof(mode),
                mode,
                "Suppression must reduce disclosure. A change that increases disclosure " +
                "commits normally and needs no tombstone: there is no safety argument for " +
                "pre-emptively revealing more.");
        }

        ArgumentOutOfRangeException.ThrowIfEqual(operationId, Guid.Empty);

        _paths.EnsureCreated();

        string path = FilePathFor(operationId);

        // Content records the mode and a creation stamp. The stamp is diagnostic only —
        // suppression never expires on its own, because an operation that has not
        // resolved has not established a safe committed state.
        string content = string.Create(
            CultureInfo.InvariantCulture,
            $"{(int)mode}\n{_clock.UtcNow:O}\n{Environment.ProcessId}\n");

        // Write to a temporary name and move into place, so a reader can never observe
        // a zero-length file that parses as nothing. A partially written suppression
        // file would still suppress — unparseable means SignedOut — but it would
        // suppress more strongly than intended, and correctness should not depend on
        // the failure path.
        string tempPath = path + ".writing";

        // Registration and publication are one in-process transaction with respect to orphan
        // recovery. Register first, then expose the marker while holding the same gate that
        // ClearAllOrphans uses to snapshot active operations. If publication fails, roll the
        // registration back before recovery can observe it.
        lock (_activeRegistry.Gate)
        {
            _activeRegistry.Operations.Add(operationId);

            try
            {
                File.WriteAllText(tempPath, content);
                File.Move(tempPath, path, overwrite: true);
                afterMarkerPublished?.Invoke();
            }
            catch
            {
                _activeRegistry.Operations.Remove(operationId);
                TryDeleteFile(tempPath);
                throw;
            }
        }

        _logger.Record(OperationalEventId.DisclosureSuppressionWritten, OperationalOutcome.Success);

        SignalSuppressEvent();

        return new DisclosureSuppression(this, operationId, mode);
    }

    /// <summary>
    /// The effective disclosure mode right now: the strongest mode among the files
    /// present, or <see cref="DisclosureMode.Full"/> when there are none.
    /// </summary>
    /// <remarks>
    /// Precedence is computed here, at read time, rather than maintained in shared
    /// state. That is what removes the lost-update problem: no operation ever writes a
    /// combined value that another operation could overwrite.
    /// </remarks>
    public DisclosureMode GetEffectiveMode()
    {
        string[] files;

        try
        {
            // Directory.Exists returns false both when the directory is legitimately absent and
            // when Windows cannot query it. Using it as a precheck would turn ACL damage or a
            // transient filesystem failure into Full disclosure. Enumerate directly so the
            // exception preserves that distinction.
            files = _enumerateFiles(_paths.SuppressionDirectory, "*" + FileExtension);
        }
        catch (DirectoryNotFoundException)
        {
            // A missing directory is the normal first-run/uninstalled-state case.
            return DisclosureMode.Full;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cannot determine the state, so assume the worst. Suppress with the
            // strongest mode.
            _logger.Record(
                OperationalEventId.DisclosureSuppressionEnumerationFailed,
                OperationalOutcome.Failed);
            return DisclosureMode.SignedOut;
        }

        if (files.Length == 0)
        {
            return DisclosureMode.Full;
        }

        DisclosureMode effective = DisclosureMode.Full;

        foreach (string file in files)
        {
            DisclosureMode mode = ReadMode(file);
            if (mode > effective)
            {
                effective = mode;
            }
        }

        _logger.Record(
            OperationalEventId.DisclosureSuppressionActive,
            OperationalOutcome.Success,
            recordCount: files.Length);

        return effective;
    }

    /// <summary>
    /// Whether any suppression file is present. Equivalent to
    /// <see cref="GetEffectiveMode"/> being stronger than <see cref="DisclosureMode.Full"/>,
    /// exposed separately because the provider's render path asks this question on every
    /// pass and reads better for it.
    /// </summary>
    public bool IsSuppressed() => GetEffectiveMode() != DisclosureMode.Full;

    /// <summary>
    /// How many suppression files exist, for the companion's diagnostics view.
    /// </summary>
    public int CountSuppressionFiles()
    {
        try
        {
            return _enumerateFiles(_paths.SuppressionDirectory, "*" + FileExtension).Length;
        }
        catch (DirectoryNotFoundException)
        {
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(
                OperationalEventId.DisclosureSuppressionEnumerationFailed,
                OperationalOutcome.Failed);
            return -1;
        }
    }

    /// <summary>
    /// Removes every suppression file. This is the companion's explicit recovery
    /// action for files orphaned by a crashed operation, and it is the only way
    /// suppression is cleared other than an operation removing its own file.
    /// </summary>
    /// <remarks>
    /// Deliberately requires user intent. Orphaned files keep suppression active, which
    /// is the safe direction, so clearing them must be a decision rather than an
    /// automatic timeout — an automatic clear would re-disclose the previous account's
    /// subjects at exactly the moment nobody was watching.
    /// </remarks>
    public int ClearAllOrphans() => ClearAllOrphansWithResult().RemovedCount;

    /// <summary>
    /// Removes orphaned markers and reports whether the suppression directory was actually readable.
    /// </summary>
    public DisclosureRecoveryResult ClearAllOrphansWithResult() =>
        ClearAllOrphansDetailed(beforeEnumeration: null);

    internal int ClearAllOrphans(Action? beforeEnumeration) =>
        ClearAllOrphansDetailed(beforeEnumeration).RemovedCount;

    private DisclosureRecoveryResult ClearAllOrphansDetailed(Action? beforeEnumeration)
    {
        int removed = 0;
        string[] files;

        try
        {
            beforeEnumeration?.Invoke();
            files = _enumerateFiles(_paths.SuppressionDirectory, "*" + FileExtension);
        }
        catch (DirectoryNotFoundException)
        {
            _logger.Record(
                OperationalEventId.DisclosureSuppressionCleared,
                OperationalOutcome.Success,
                recordCount: 0);
            SignalSuppressEvent();
            return new DisclosureRecoveryResult(DisclosureRecoveryStatus.DirectoryAbsent, 0);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.Record(
                OperationalEventId.DisclosureSuppressionEnumerationFailed,
                OperationalOutcome.Failed);
            return new DisclosureRecoveryResult(DisclosureRecoveryStatus.Unreadable, 0);
        }

        foreach (string file in files)
        {
            if (IsOwnerProcessAlive(file))
            {
                // Written by a process that still exists. It is not an orphan, and the user
                // asked to clear interrupted operations rather than live ones.
                _logger.Record(
                    OperationalEventId.DisclosureSuppressionActive,
                    OperationalOutcome.Skipped);
                continue;
            }

            bool deleted;

            if (TryGetOperationId(file) is Guid operationId)
            {
                // Recheck under the same gate Suppress holds while registering and publishing.
                // A one-time snapshot is stale as soon as the gate is released: a new marker can
                // become visible before enumeration and be absent from that snapshot. Keeping
                // this check and deletion together makes "active or deleted" atomic with
                // respect to every in-process disclosure operation.
                lock (_activeRegistry.Gate)
                {
                    if (_activeRegistry.Operations.Contains(operationId))
                    {
                        _logger.Record(
                            OperationalEventId.DisclosureSuppressionActive,
                            OperationalOutcome.Skipped);
                        continue;
                    }

                    deleted = TryDeleteFile(file);
                }
            }
            else
            {
                // A registered operation always has a GUID filename, so an unparseable name
                // cannot race the active registry. It still follows the fail-closed owner
                // liveness check above before explicit recovery may remove it.
                deleted = TryDeleteFile(file);
            }

            if (deleted)
            {
                removed++;
            }
        }

        _logger.Record(
            OperationalEventId.DisclosureSuppressionCleared,
            OperationalOutcome.Success,
            recordCount: removed);

        SignalSuppressEvent();
        return new DisclosureRecoveryResult(DisclosureRecoveryStatus.Completed, removed);
    }

    /// <summary>
    /// Deletes one operation's own file. Called only by
    /// <see cref="DisclosureSuppression.CommitAndClear"/>, and only after that
    /// operation's state commit has succeeded.
    /// </summary>
    internal bool DeleteOwn(Guid operationId)
    {
        string path = FilePathFor(operationId);

        if (TryDeleteFile(path))
        {
            lock (_activeRegistry.Gate)
            {
                _activeRegistry.Operations.Remove(operationId);
            }

            _logger.Record(OperationalEventId.DisclosureSuppressionCleared, OperationalOutcome.Success);
            SignalSuppressEvent();
            return true;
        }

        // Keep the operation active and report failure to its handle. A transient sharing
        // violation must remain retryable; declaring the handle cleared here would strand its
        // tombstone until explicit orphan recovery and could leave counts-only mode active after
        // the user successfully chose full disclosure.
        _logger.Record(OperationalEventId.DisclosureSuppressionCleared, OperationalOutcome.Failed);
        return false;
    }

    /// <summary>
    /// Marks a failed operation as no longer running without weakening its suppression.
    /// </summary>
    /// <remarks>
    /// The marker deliberately remains on disk and continues to fail closed. Removing only the
    /// in-process registration lets the companion's explicit orphan-recovery action distinguish
    /// this completed failure from a disclosure operation that is still actively committing.
    /// </remarks>
    internal void CompleteWithoutClearing(Guid operationId)
    {
        lock (_activeRegistry.Gate)
        {
            _activeRegistry.Operations.Remove(operationId);
        }
    }

    private static ActiveOperationRegistry GetActiveRegistry(string suppressionDirectory)
    {
        string key = Path.TrimEndingDirectorySeparator(Path.GetFullPath(suppressionDirectory));

        lock (RegistriesGate)
        {
            if (Registries.TryGetValue(key, out WeakReference<ActiveOperationRegistry>? reference)
                && reference.TryGetTarget(out ActiveOperationRegistry? registry))
            {
                return registry;
            }

            registry = new ActiveOperationRegistry();
            Registries[key] = new WeakReference<ActiveOperationRegistry>(registry);
            return registry;
        }
    }

    private sealed class ActiveOperationRegistry
    {
        public Lock Gate { get; } = new();

        public HashSet<Guid> Operations { get; } = [];
    }

    private string FilePathFor(Guid operationId) =>
        Path.Combine(_paths.SuppressionDirectory, operationId.ToString("N") + FileExtension);

    private DisclosureMode ReadMode(string path)
    {
        try
        {
            // Share generously: another process may be reading the same file, and a
            // reader must never be the reason a peer's operation fails.
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            string? first = reader.ReadLine();

            if (int.TryParse(first, CultureInfo.InvariantCulture, out int value)
                && Enum.IsDefined(typeof(DisclosureMode), value)
                && (DisclosureMode)value != DisclosureMode.Full)
            {
                return (DisclosureMode)value;
            }

            // Present but unparseable, or claiming Full, which no suppression file may
            // legitimately do. Fail closed.
            _logger.Record(
                OperationalEventId.DisclosureSuppressionOrphanDetected,
                OperationalOutcome.Recovered);
            return DisclosureMode.SignedOut;
        }
        catch (FileNotFoundException)
        {
            // Deleted by its owner between enumeration and read. That is the owner
            // legitimately finishing, so it contributes nothing.
            return DisclosureMode.Full;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Cannot read a file that exists. Fail closed.
            _logger.Record(
                OperationalEventId.DisclosureSuppressionOrphanDetected,
                OperationalOutcome.Failed);
            return DisclosureMode.SignedOut;
        }
    }

    /// <summary>
    /// Recovers the operation GUID from a suppression file name.
    /// </summary>
    private static Guid? TryGetOperationId(string path) =>
        Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out Guid id) ? id : null;

    /// <summary>
    /// Whether the process that wrote a suppression file still exists.
    /// </summary>
    /// <remarks>
    /// A secondary guard only. PID reuse means a live PID does not prove the <em>same</em>
    /// process is running, so this is used to decline deletion — never to authorise it. Failing
    /// to determine liveness counts as alive, which keeps suppression in place: the safe
    /// direction, consistent with every other uncertainty in this type.
    /// </remarks>
    private static bool IsOwnerProcessAlive(string path)
    {
        try
        {
            using var stream = new FileStream(
                path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);

            _ = reader.ReadLine();  // mode
            _ = reader.ReadLine();  // creation stamp
            string? processIdText = reader.ReadLine();

            if (!int.TryParse(processIdText, CultureInfo.InvariantCulture, out int processId))
            {
                // Unparseable owner. Treat as alive so recovery leaves it alone; a genuinely
                // orphaned file with a corrupt owner line is cleared by reinstalling or by
                // clearing the cache, both of which are more deliberate than this action.
                return true;
            }

            if (processId == Environment.ProcessId)
            {
                // This process wrote it, but it is not in the active set — so the operation
                // already completed or was abandoned in-process. Safe to clear.
                return false;
            }

            using System.Diagnostics.Process owner = System.Diagnostics.Process.GetProcessById(processId);
            return !owner.HasExited;
        }
        catch (ArgumentException)
        {
            // GetProcessById throws when no such process exists. That is the orphan case.
            return false;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return true;
        }
    }

    private static bool TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private void SignalSuppressEvent()
    {
        try
        {
            // An auto-reset event would be delivered to at most one waiter, so a
            // manual-reset pulse is used: every listener re-reads state for itself and
            // the signal carries no payload.
            using var suppressEvent = EventWaitHandle.OpenExisting(_paths.SuppressDetailsEventName);
            suppressEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // No listener is running. The provider re-enumerates on construction and on
            // Activate, so a missed signal costs nothing: state on disk is authoritative
            // and the event is only an accelerant.
        }
    }
}

/// <summary>
/// One in-flight disclosure-reducing operation's suppression.
/// </summary>
/// <remarks>
/// Not <see cref="IDisposable"/> on purpose. Disposal semantics would invite
/// <c>using</c>, which clears suppression on <em>every</em> exit path including the
/// failure paths — and on failure the tombstone must stay. Clearing therefore requires
/// the explicit, named call <see cref="CommitAndClear"/>, so that leaving suppression in
/// place is what happens when a caller does nothing.
/// </remarks>
public sealed class DisclosureSuppression
{
    private readonly DisclosureTombstoneStore _store;
    private readonly Guid _operationId;
    private bool _completed;
    private bool _cleared;

    internal DisclosureSuppression(DisclosureTombstoneStore store, Guid operationId, DisclosureMode mode)
    {
        _store = store;
        _operationId = operationId;
        Mode = mode;
    }

    /// <summary>The mode this operation is suppressing to.</summary>
    public DisclosureMode Mode { get; }

    /// <summary>This operation's identity. Exposed for assertions and diagnostics.</summary>
    public Guid OperationId => _operationId;

    /// <summary>Whether this operation has already removed its own file.</summary>
    public bool IsCleared => _cleared;

    /// <summary>
    /// Removes this operation's own file, after its state commit has succeeded.
    /// </summary>
    /// <remarks>
    /// Call this only once committed state is authoritative and itself says signed-out
    /// or details-hidden. Calling it after a failed commit would re-disclose data that
    /// nothing else is now hiding.
    /// </remarks>
    public void CommitAndClear()
    {
        if (_completed)
        {
            return;
        }

        _cleared = _store.DeleteOwn(_operationId);
        _completed = _cleared;
    }

    /// <summary>
    /// Completes an unsuccessful disclosure operation while leaving its fail-closed marker.
    /// </summary>
    /// <remarks>
    /// Call this after the state commit has definitively failed and the operation will not retry.
    /// The tombstone remains authoritative until the user invokes explicit orphan recovery.
    /// </remarks>
    public void CompleteWithoutClearing()
    {
        if (_completed)
        {
            return;
        }

        _store.CompleteWithoutClearing(_operationId);
        _completed = true;
    }
}
