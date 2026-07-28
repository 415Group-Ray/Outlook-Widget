using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Tests.TestInfrastructure;

/// <summary>
/// An isolated coordination environment: its own temporary directory and its own uniquely
/// named kernel objects.
/// </summary>
/// <remarks>
/// Isolation is per test, not per class. These tests exercise real files and real named
/// mutexes rather than mocking them — mocking the coordination primitives would test the
/// mock — so two tests sharing a scope would contend on the same named mutex and interfere.
/// A GUID scope makes that impossible even under parallel execution.
/// </remarks>
internal sealed class CoordinationFixture : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    public CoordinationFixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "OutlookWidgetTests", Guid.NewGuid().ToString("N"));
        Scope = "t" + Guid.NewGuid().ToString("N");

        Paths = new CoordinationPaths(Root, Scope);
        Paths.EnsureCreated();

        Clock = new TestClock();
        Logger = new RecordingLogger();

        Mutex = Track(new MutationMutex(Paths.MutationMutexName, Logger));
        Cache = new ProtectedCache(Paths, Logger);
        Tombstones = new DisclosureTombstoneStore(Paths, Logger, Clock);
        Leases = new RefreshLeaseStore(Paths, Mutex, Clock, Logger);
        Commits = new StateCommitCoordinator(Paths, Mutex, Logger);
    }

    public string Root { get; }

    public string Scope { get; }

    public CoordinationPaths Paths { get; }

    public TestClock Clock { get; }

    public RecordingLogger Logger { get; }

    public MutationMutex Mutex { get; }

    public ProtectedCache Cache { get; }

    public DisclosureTombstoneStore Tombstones { get; }

    public RefreshLeaseStore Leases { get; }

    public StateCommitCoordinator Commits { get; }

    public T Track<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    /// <summary>
    /// Commits a payload the straightforward way, for tests that need existing state rather
    /// than an assertion about how it got there.
    /// </summary>
    public long SeedState(byte[] payload)
    {
        using MutationLock heldLock = Mutex.Acquire();
        CacheCommitResult result = Cache.Commit(heldLock, payload, expectedGeneration: null);
        return result.Generation;
    }

    public void Dispose()
    {
        for (int i = _disposables.Count - 1; i >= 0; i--)
        {
            try
            {
                _disposables[i].Dispose();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Test teardown; a failure here must not mask the test's own result.
            }
        }

        try
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary directory is harmless.
        }
    }
}

/// <summary>
/// Captures operational events so tests can assert on categories.
/// </summary>
/// <remarks>
/// Note what this can record: an event ID, an outcome, a duration, a count, and an HTTP
/// status. There is nowhere to put a subject line or an account, because
/// <see cref="IOperationalLogger"/> has no string parameter. That is the no-metadata rule
/// enforced by API shape, and this recorder inherits the guarantee rather than restating it.
/// </remarks>
internal sealed class RecordingLogger : IOperationalLogger
{
    private readonly Lock _gate = new();
    private readonly List<(OperationalEventId Id, OperationalOutcome Outcome, int? Count)> _events = [];

    public void Record(
        OperationalEventId id,
        OperationalOutcome outcome,
        TimeSpan? duration = null,
        int? recordCount = null,
        int? httpStatusCode = null)
    {
        lock (_gate)
        {
            _events.Add((id, outcome, recordCount));
        }
    }

    public IReadOnlyList<(OperationalEventId Id, OperationalOutcome Outcome, int? Count)> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    public bool Saw(OperationalEventId id) => Events.Any(e => e.Id == id);

    public bool Saw(OperationalEventId id, OperationalOutcome outcome) =>
        Events.Any(e => e.Id == id && e.Outcome == outcome);

    public int CountOf(OperationalEventId id) => Events.Count(e => e.Id == id);
}
