namespace OutlookWidget.Core.Refresh;

/// <summary>
/// The bounds from the plan's "What actually bounds a refresh" table, in one place
/// so the relationship between them can be asserted rather than assumed.
/// </summary>
/// <remarks>
/// The critical invariant is that <see cref="LeaseHorizon"/> exceeds
/// <see cref="WorstCaseRefreshTransaction"/>. A lease expiring mid-commit would let
/// a peer start a second refresh whose commit races the first. The generation
/// compare would still prevent corruption, but the wasted request and the confusing
/// indicator state are avoidable by choosing the horizon correctly — so
/// <see cref="Validate"/> enforces the relationship at startup instead of leaving
/// it to a comment that a later edit can silently falsify.
/// </remarks>
public static class CoordinationBounds
{
    /// <summary>
    /// Bound on every mutation-mutex acquisition. The critical section is local
    /// synchronous I/O whose worst case is the bounded replace-retry ladder, so two
    /// seconds is generous by an order of magnitude: a timeout here indicates a
    /// genuinely stuck peer rather than normal contention.
    /// </summary>
    public static readonly TimeSpan MutexWait = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Overall deadline for the awaited portion of a refresh: silent token
    /// acquisition, the Graph requests, and validation. It does not bound the
    /// commit, which is synchronous and deliberately non-cancellable once entered.
    /// </summary>
    public static readonly TimeSpan AsyncDeadline = TimeSpan.FromSeconds(20);

    /// <summary>Timeout for the Graph requests, nested inside <see cref="AsyncDeadline"/>.</summary>
    public static readonly TimeSpan GraphRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Bound-by-construction budget for the non-cancellable critical section: a
    /// fixed amount of local synchronous work plus the fixed retry ladder. Not
    /// enforced by a token — enforcement would reintroduce the half-committed state
    /// the design exists to prevent — but stated so the arithmetic below is honest.
    /// </summary>
    public static readonly TimeSpan CriticalSection = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Lease claim, plus the awaited work, plus the commit wait and critical
    /// section, plus the lease clear. Steps 2 through 9 of the refresh algorithm.
    /// Widget delivery is deliberately excluded; it is unbounded by the host.
    /// </summary>
    public static TimeSpan WorstCaseRefreshTransaction =>
        MutexWait          // step 2, lease claim
        + AsyncDeadline    // steps 3 to 5
        + MutexWait        // step 6, commit wait
        + CriticalSection  // steps 6 to 8
        + MutexWait;       // step 9, lease clear

    /// <summary>
    /// How long a claimed lease stays live. Must exceed
    /// <see cref="WorstCaseRefreshTransaction"/>, not merely the async deadline.
    /// </summary>
    public static readonly TimeSpan LeaseHorizon = TimeSpan.FromSeconds(30);

    /// <summary>Debounce for the user's manual Refresh action.</summary>
    public static readonly TimeSpan ManualRefreshDebounce = TimeSpan.FromSeconds(15);

    /// <summary>A cached snapshot older than this makes an activation eligible to refresh.</summary>
    public static readonly TimeSpan ActivationStaleness = TimeSpan.FromSeconds(60);

    /// <summary>Opportunistic timer interval while the provider remains active.</summary>
    public static readonly TimeSpan ActiveTimerInterval = TimeSpan.FromMinutes(5);

    /// <summary>
    /// After this long without a successful refresh, suppress message details and
    /// show a stale/reconnect state rather than presenting old subjects as current.
    /// </summary>
    public static readonly TimeSpan StaleDetailSuppression = TimeSpan.FromHours(24);

    /// <summary>
    /// Backoff ladder for an atomic-replace sharing violation from an unrelated
    /// handle such as antivirus, indexing, or a debugger. Applied while retaining
    /// the mutation mutex, because releasing it mid-commit is what would let a peer
    /// observe a half-written state.
    /// </summary>
    public static readonly TimeSpan[] ReplaceRetryBackoff =
    [
        TimeSpan.FromMilliseconds(25),
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
    ];

    /// <summary>
    /// Asserts the relationships the design depends on. Called from the
    /// coordinator's constructor so a bad edit fails immediately and locally rather
    /// than as a rare mid-commit lease reclaim in production.
    /// </summary>
    public static void Validate()
    {
        if (LeaseHorizon <= WorstCaseRefreshTransaction)
        {
            throw new InvalidOperationException(
                $"Lease horizon ({LeaseHorizon}) must exceed the worst-case refresh " +
                $"transaction ({WorstCaseRefreshTransaction}); otherwise a lease can " +
                "expire mid-commit and a peer can claim it under its owner.");
        }

        if (GraphRequestTimeout >= AsyncDeadline)
        {
            throw new InvalidOperationException(
                $"The Graph request timeout ({GraphRequestTimeout}) must be nested " +
                $"inside the async deadline ({AsyncDeadline}).");
        }
    }
}
