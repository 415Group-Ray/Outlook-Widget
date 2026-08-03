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
    private readonly CommitSignedOutStateAction _commitAction;
    private readonly IOperationalLogger _logger;

    public SignOutCoordinator(
        DisclosureTombstoneStore tombstones,
        StateCommitCoordinator commits,
        CommitSignedOutStateAction commitAction,
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
            await removeAccountAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            _logger.Record(OperationalEventId.SignOutFailed, OperationalOutcome.Failed);
            return new SignOutResult(SignOutOutcome.AccountRemovalFailed);
        }

        StateCommitResult commit = _commits.CommitDisclosureChange(_commitAction);

        if (!commit.IsCommitted)
        {
            _logger.Record(OperationalEventId.SignOutFailed, OperationalOutcome.Failed);
            return new SignOutResult(SignOutOutcome.StateCommitFailed, commit.Outcome);
        }

        suppression.CommitAndClear();
        _logger.Record(OperationalEventId.SignOutCompleted, OperationalOutcome.Success);
        return new SignOutResult(SignOutOutcome.SignedOut, commit.Outcome);
    }
}
