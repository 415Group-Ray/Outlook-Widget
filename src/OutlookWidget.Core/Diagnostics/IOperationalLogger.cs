namespace OutlookWidget.Core.Diagnostics;

/// <summary>
/// The complete set of loggable events. A closed enum rather than a string.
/// </summary>
/// <remarks>
/// The plan's rule is that the logging API accepts no sender, subject, tenant or
/// domain, user or account, message or link, token or header, raw request or response,
/// correlation ID, or exception dump — and that the rule is enforced by API shape
/// rather than by a growing redaction subsystem. A free-text event name would defeat
/// that immediately: <c>Log($"refresh failed for {address}")</c> compiles, and no
/// amount of review reliably catches every instance. An enum cannot carry a subject
/// line. Adding an event means adding a member here, which is exactly the moment a
/// reviewer should be looking.
/// </remarks>
public enum OperationalEventId
{
    // Refresh transaction
    RefreshRequested,
    RefreshSkippedDebounce,
    RefreshSkippedLeaseHeld,
    RefreshLeaseClaimed,
    RefreshLeaseClaimTimedOut,
    RefreshLeaseCleared,
    RefreshLeaseClearTimedOut,
    RefreshLeaseReclaimedExpired,
    RefreshCompleted,
    RefreshDiscardedStateChanged,
    RefreshDeadlineExceeded,
    RefreshTimerCallbackFailed,

    // Token acquisition and Graph, recorded by category only
    SilentTokenAcquired,
    SilentTokenUiRequired,
    SilentTokenBrokerUnavailable,
    GraphRequestCompleted,
    GraphRequestFailed,
    GraphThrottled,

    // State commits
    StateCommitted,
    StateCommitFailed,
    SnapshotReplaceRetried,
    SnapshotReplaceFailed,
    CacheReadFailed,
    CacheDiscardedInvalid,

    // Coordination primitives
    MutationLockWaitTimedOut,
    MutationLockAbandonedByPeer,
    MutationLockAffinityViolated,

    // Disclosure suppression
    DisclosureSuppressionWritten,
    DisclosureSuppressionCleared,
    DisclosureSuppressionActive,
    DisclosureSuppressionEnumerationFailed,
    DisclosureSuppressionOrphanDetected,

    // Widget delivery, reported separately from refresh
    DeliveryRequested,
    DeliveryCoalesced,
    DeliveryCompleted,
    DeliveryFailed,

    // Account lifecycle
    SignInCompleted,
    SignOutRequested,
    SignOutCompleted,
    SignOutFailed,
    AccountSwitchRequested,
    AccountSwitchCompleted,
    AccountSwitchFailed,
    PrivacySettingChanged,
    CacheCleared,

    // Launching
    OutlookLaunchAttempted,
    OutlookLaunchFailed,

    /// <summary>
    /// The provider could not start the companion. Distinct from the Outlook events because the
    /// consequence is different: this is the escape hatch offered by every signed-out,
    /// sign-in-required, and broker-unavailable card, so a user who cannot reach the companion
    /// has no route back to a working widget.
    /// </summary>
    CompanionLaunchFailed,
    MessageLinkOpened,
    MessageLinkRejected,
    MessageActionStaleGeneration,
}

/// <summary>
/// Outcome categories. Also closed, for the same reason as
/// <see cref="OperationalEventId"/>.
/// </summary>
public enum OperationalOutcome
{
    Success,

    /// <summary>The operation completed but its result was deliberately not used.</summary>
    Discarded,

    /// <summary>A bounded wait elapsed.</summary>
    Timeout,

    /// <summary>Cancelled by a deadline or an explicit token.</summary>
    Cancelled,

    /// <summary>Failed for a reason the product handles as a defined state.</summary>
    Failed,

    /// <summary>Failed and then recovered without user involvement.</summary>
    Recovered,

    /// <summary>Deliberately not attempted, such as a debounced or duplicate request.</summary>
    Skipped,

    /// <summary>Authorization is required before the operation can succeed.</summary>
    ApprovalRequired,
}

/// <summary>
/// Metadata-free operational logging.
/// </summary>
/// <remarks>
/// <para>
/// Every parameter is either a closed enum or a bounded number. There is no
/// <c>string</c> parameter anywhere on this interface, and there must never be one.
/// That constraint is the enforcement: mailbox and identity metadata are strings, so
/// an API with no string parameter cannot accept them, and a call site that wants to
/// log a subject line has nowhere to put it.
/// </para>
/// <para>
/// <see cref="Record"/> takes an optional HTTP status code because a bare integer
/// status is a category, not metadata — it distinguishes 403 from 429 without naming
/// a mailbox. It does not take an exception, because an exception message routinely
/// contains a URL, an account, or a server response.
/// </para>
/// </remarks>
public interface IOperationalLogger
{
    /// <summary>
    /// Records one event.
    /// </summary>
    /// <param name="id">Which event occurred.</param>
    /// <param name="outcome">Its outcome category.</param>
    /// <param name="duration">How long it took, when meaningful.</param>
    /// <param name="recordCount">
    /// A bounded count, such as the number of messages in a snapshot or the number of
    /// suppression files present. A count, never an identifier.
    /// </param>
    /// <param name="httpStatusCode">The HTTP status category, for Graph events.</param>
    void Record(
        OperationalEventId id,
        OperationalOutcome outcome,
        TimeSpan? duration = null,
        int? recordCount = null,
        int? httpStatusCode = null);
}
