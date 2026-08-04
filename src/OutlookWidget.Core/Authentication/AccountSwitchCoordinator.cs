using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Authentication;

/// <summary>Outcome of the companion's suppress-first account-switch sequence.</summary>
public enum AccountSwitchOutcome
{
    Switched,
    SelectionFailed,
    StateCommitFailed,
    SuppressionClearFailed,
}

/// <summary>
/// The token-free result of companion-only interactive account selection. A class with a safe
/// <see cref="ToString"/> rather than a positional record, so interpolation cannot print the opaque
/// account identifier.
/// </summary>
public sealed class AccountSelectionResult
{
    public AccountSelectionResult(TokenAcquisitionStatus status, string? homeAccountId)
    {
        Status = status;
        HomeAccountId = homeAccountId;
    }

    public TokenAcquisitionStatus Status { get; }

    public string? HomeAccountId { get; }

    public bool IsSelected =>
        Status == TokenAcquisitionStatus.Acquired
        && !string.IsNullOrWhiteSpace(HomeAccountId);

    public override string ToString() => Status.ToString();
}

/// <summary>The bounded account-switch result the companion can safely describe.</summary>
public readonly record struct AccountSwitchResult(
    AccountSwitchOutcome Outcome,
    TokenAcquisitionStatus SelectionStatus,
    StateCommitOutcome? CommitOutcome = null)
{
    public bool IsSwitched => Outcome == AccountSwitchOutcome.Switched;
}

/// <summary>
/// Orders account switching so the prior mailbox is suppressed before interactive selection and
/// stays suppressed until the prior snapshot is cleared and the new selected identifier commits.
/// </summary>
public sealed class AccountSwitchCoordinator
{
    private readonly DisclosureTombstoneStore _tombstones;
    private readonly StateCommitCoordinator _commits;
    private readonly Func<string, IStateCommitAction> _commitActionFactory;
    private readonly IOperationalLogger _logger;

    public AccountSwitchCoordinator(
        DisclosureTombstoneStore tombstones,
        StateCommitCoordinator commits,
        CoordinationPaths paths,
        ProtectedCache cache,
        SelectedAccountStore selectedAccounts,
        IOperationalLogger? logger = null)
        : this(
            tombstones,
            commits,
            homeAccountId => new CommitAccountSwitchStateAction(
                paths,
                cache,
                selectedAccounts,
                homeAccountId,
                logger),
            logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(cache);
        ArgumentNullException.ThrowIfNull(selectedAccounts);
    }

    internal AccountSwitchCoordinator(
        DisclosureTombstoneStore tombstones,
        StateCommitCoordinator commits,
        Func<string, IStateCommitAction> commitActionFactory,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(tombstones);
        ArgumentNullException.ThrowIfNull(commits);
        ArgumentNullException.ThrowIfNull(commitActionFactory);

        _tombstones = tombstones;
        _commits = commits;
        _commitActionFactory = commitActionFactory;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <param name="selectAccountAsync">
    /// Performs companion-only interactive account selection and returns no access token. It runs
    /// only after signed-out suppression has been durably published.
    /// </param>
    public async Task<AccountSwitchResult> SwitchAsync(
        Func<Task<AccountSelectionResult>> selectAccountAsync)
    {
        ArgumentNullException.ThrowIfNull(selectAccountAsync);

        _logger.Record(OperationalEventId.AccountSwitchRequested, OperationalOutcome.Success);
        DisclosureSuppression suppression = _tombstones.Suppress(DisclosureMode.SignedOut);

        try
        {
            AccountSelectionResult selection;

            try
            {
                selection = await selectAccountAsync().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.AccountSwitchFailed, OperationalOutcome.Failed);
                return new AccountSwitchResult(
                    AccountSwitchOutcome.SelectionFailed,
                    TokenAcquisitionStatus.Failed);
            }

            if (!selection.IsSelected)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.AccountSwitchFailed, OperationalOutcome.Failed);
                return new AccountSwitchResult(
                    AccountSwitchOutcome.SelectionFailed,
                    selection.Status);
            }

            StateCommitResult commit = _commits.CommitDisclosureChange(
                _commitActionFactory(selection.HomeAccountId!),
                OperationalEventId.AccountSwitchFailed);

            if (!commit.IsCommitted)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.AccountSwitchFailed, OperationalOutcome.Failed);
                return new AccountSwitchResult(
                    AccountSwitchOutcome.StateCommitFailed,
                    selection.Status,
                    commit.Outcome);
            }

            suppression.CommitAndClear();

            if (!suppression.IsCleared)
            {
                suppression.CommitAndClear();
            }

            if (!suppression.IsCleared)
            {
                suppression.CompleteWithoutClearing();
                _logger.Record(OperationalEventId.AccountSwitchFailed, OperationalOutcome.Failed);
                return new AccountSwitchResult(
                    AccountSwitchOutcome.SuppressionClearFailed,
                    selection.Status,
                    commit.Outcome);
            }

            _logger.Record(OperationalEventId.AccountSwitchCompleted, OperationalOutcome.Success);
            return new AccountSwitchResult(
                AccountSwitchOutcome.Switched,
                selection.Status,
                commit.Outcome);
        }
        finally
        {
            if (!suppression.IsCleared)
            {
                suppression.CompleteWithoutClearing();
            }
        }
    }
}
