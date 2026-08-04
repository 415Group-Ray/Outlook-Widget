using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Authentication;

/// <summary>Outcome of the companion's suppress-first sign-out sequence.</summary>
public enum SignOutOutcome
{
    SignedOut,
    AccountRemovalFailed,
    StateCommitFailed,
    SuppressionClearFailed,
}

/// <summary>The bounded result the companion can describe without exposing account data.</summary>
public readonly record struct SignOutResult(
    SignOutOutcome Outcome,
    StateCommitOutcome? CommitOutcome = null)
{
    public bool IsSignedOut => Outcome == SignOutOutcome.SignedOut;
}

/// <summary>
/// Orders sign-out so disclosure suppression is published before asynchronous account removal,
/// and is lifted only after durable signed-out state commits.
/// </summary>
public sealed class SignOutCoordinator
{
    private readonly DisclosureTombstoneStore _tombstones;
    private readonly StateCommitCoordinator _commits;
    private readonly IStateCommitAction _commitAction;
    private readonly IOperationalLogger _logger;

    public SignOutCoordinator(
        DisclosureTombstoneStore tombstones,
        StateCommitCoordinator commits,
        CommitSignedOutStateAction commitAction,
        IOperationalLogger? logger = null)
        : this(tombstones, commits, (IStateCommitAction)commitAction, logger)
    {
    }

    internal SignOutCoordinator(
        DisclosureTombstoneStore tombstones,
        StateCommitCoordinator commits,
        IStateCommitAction commitAction,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tombstones);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(commitAction);

        _tombstones = tombstones;
        _commits = commits;
        _commitAction = commitAction;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <param name="removeAccountAsync">
    /// Removes the selected account from this application's MSAL cache. It runs only after the
    /// signed-out tombstone has been published and must not display authentication UI.
    /// </param>
    public async Task<SignOutResult> SignOutAsync(Func<Task> removeAccountAsync)
    {
        ArgumentNullException.ThrowIfNull(removeAccountAsync);

        _logger.Record(OperationalEventId.SignOutRequested, OperationalOutcome.Success);
        DisclosureSuppression suppression = _tombstones.Suppress(DisclosureMode.SignedOut);

        try
        {
            try
            {
                await removeAccountAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.SignOutFailed, OperationalOutcome.Failed);
                return new SignOutResult(SignOutOutcome.AccountRemovalFailed);
            }

            StateCommitResult commit = _commits.CommitDisclosureChange(
                _commitAction,
                OperationalEventId.SignOutFailed);

            if (!commit.IsCommitted)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.SignOutFailed, OperationalOutcome.Failed);
                return new SignOutResult(SignOutOutcome.StateCommitFailed, commit.Outcome);
            }

            suppression.CommitAndClear();

            if (!suppression.IsCleared)
            {
                // A sharing violation can be transient, so give this handle one bounded retry before
                // handing recovery back to the user. If it still fails, unregister the completed
                // operation without deleting its fail-closed marker; the companion's explicit orphan
                // recovery action can then remove it safely.
                suppression.CommitAndClear();
            }

            if (!suppression.IsCleared)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.SignOutFailed, OperationalOutcome.Failed);
                return new SignOutResult(SignOutOutcome.SuppressionClearFailed, commit.Outcome);
            }

            _logger.Record(OperationalEventId.SignOutCompleted, OperationalOutcome.Success);
            return new SignOutResult(SignOutOutcome.SignedOut, commit.Outcome);
        }
        finally
        {
            // Any exception after publication must leave the fail-closed marker on disk but stop
            // presenting this operation as live. Otherwise same-process explicit recovery skips it
            // until the companion exits.
            if (!suppression.IsCleared)
            {
                suppression.CompleteWithoutClearing();
            }
        }
    }
}
