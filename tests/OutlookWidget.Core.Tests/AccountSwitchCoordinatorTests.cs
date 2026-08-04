using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

public sealed class AccountSwitchCoordinatorTests
{
    private static readonly AuthenticationOptions Registration =
        AuthenticationOptions.TryCreate(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"))!;

    [Fact]
    public void Selection_result_string_does_not_expose_the_account_identifier()
    {
        var result = new AccountSelectionResult(
            TokenAcquisitionStatus.Acquired,
            "sensitive.object.tenant");

        Assert.Equal("Acquired", result.ToString());
        Assert.DoesNotContain("sensitive", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Switch_suppresses_before_selection_then_clears_prior_mail_and_commits_new_account()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("old.tenant"));
        fixture.SeedState("old mailbox"u8.ToArray());
        AuthorizationStateStore.Write(
            fixture.Paths,
            Registration,
            TokenAcquisitionStatus.ApprovalRequired,
            DateTimeOffset.UtcNow,
            fixture.Logger);

        var coordinator = Build(fixture, selected);
        bool suppressionWasPublished = false;

        AccountSwitchResult result = await coordinator.SwitchAsync(
            () =>
            {
                suppressionWasPublished =
                    fixture.Tombstones.GetEffectiveMode() == DisclosureMode.SignedOut;
                return Task.FromResult(
                    new AccountSelectionResult(TokenAcquisitionStatus.Acquired, "new.tenant"));
            });

        Assert.True(suppressionWasPublished);
        Assert.True(result.IsSwitched);
        Assert.Equal(StateCommitOutcome.Committed, result.CommitOutcome);
        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
        Assert.Equal(SelectedAccountStatus.Recorded, selected.Read().Status);
        Assert.Equal("new.tenant", selected.Read().HomeAccountId);
        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.True(fixture.Logger.Saw(
            Diagnostics.OperationalEventId.AccountSwitchCompleted,
            Diagnostics.OperationalOutcome.Success));
    }

    [Fact]
    public async Task Failed_selection_leaves_prior_state_covered_and_recoverable()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("old.tenant"));
        fixture.SeedState("old mailbox"u8.ToArray());

        AccountSwitchResult result = await Build(fixture, selected).SwitchAsync(
            () => Task.FromResult(
                new AccountSelectionResult(TokenAcquisitionStatus.Cancelled, null)));

        Assert.Equal(AccountSwitchOutcome.SelectionFailed, result.Outcome);
        Assert.Equal(TokenAcquisitionStatus.Cancelled, result.SelectionStatus);
        Assert.Equal(CacheReadStatus.Success, fixture.Cache.Read().Status);
        Assert.Equal("old.tenant", selected.Read().HomeAccountId);
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public async Task Cache_clear_failure_preserves_prior_selection_and_suppression()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("old.tenant"));
        fixture.SeedState("old mailbox"u8.ToArray());

        using (var blocker = new FileStream(
                   fixture.Paths.StateFilePath,
                   FileMode.Open,
                   FileAccess.Read,
                   FileShare.None))
        {
            AccountSwitchResult result = await Build(fixture, selected).SwitchAsync(
                () => Task.FromResult(
                    new AccountSelectionResult(TokenAcquisitionStatus.Acquired, "new.tenant")));

            Assert.Equal(AccountSwitchOutcome.StateCommitFailed, result.Outcome);
            Assert.Equal(StateCommitOutcome.CommitFailed, result.CommitOutcome);
            Assert.Equal("old.tenant", selected.Read().HomeAccountId);
            Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        }

        Assert.Equal(CacheReadStatus.Success, fixture.Cache.Read().Status);
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
    }

    [Fact]
    public async Task Selection_write_failure_does_not_publish_a_partial_new_identifier()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("old.tenant"));
        fixture.SeedState("old mailbox"u8.ToArray());

        var failingSelectionWriter = new SelectedAccountStore(
            fixture.Paths,
            Registration,
            fixture.Logger,
            new FailingProtector());

        var coordinator = new AccountSwitchCoordinator(
            fixture.Tombstones,
            fixture.Commits,
            fixture.Paths,
            fixture.Cache,
            failingSelectionWriter,
            fixture.Logger);

        AccountSwitchResult result = await coordinator.SwitchAsync(
            () => Task.FromResult(
                new AccountSelectionResult(TokenAcquisitionStatus.Acquired, "new.tenant")));

        Assert.Equal(AccountSwitchOutcome.StateCommitFailed, result.Outcome);
        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
        Assert.Equal("old.tenant", selected.Read().HomeAccountId);
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
    }

    [Fact]
    public async Task Committed_switch_reports_recovery_needed_when_its_tombstone_cannot_be_cleared()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("old.tenant"));
        fixture.SeedState("old mailbox"u8.ToArray());

        FileStream? blocker = null;

        try
        {
            AccountSwitchResult result = await Build(fixture, selected).SwitchAsync(
                () =>
                {
                    string marker = Assert.Single(
                        Directory.GetFiles(fixture.Paths.SuppressionDirectory, "*.suppress"));
                    blocker = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);

                    return Task.FromResult(
                        new AccountSelectionResult(TokenAcquisitionStatus.Acquired, "new.tenant"));
                });

            Assert.Equal(AccountSwitchOutcome.SuppressionClearFailed, result.Outcome);
            Assert.Equal(StateCommitOutcome.Committed, result.CommitOutcome);
            Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
            Assert.Equal("new.tenant", selected.Read().HomeAccountId);
            Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        }
        finally
        {
            blocker?.Dispose();
        }

        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public async Task Thrown_commit_unregisters_suppression_for_explicit_recovery()
    {
        using var fixture = new CoordinationFixture();
        var coordinator = new AccountSwitchCoordinator(
            fixture.Tombstones,
            fixture.Commits,
            _ => new ThrowingCommitAction(),
            fixture.Logger);

        await Assert.ThrowsAsync<UnauthorizedAccessException>(
            () => coordinator.SwitchAsync(
                () => Task.FromResult(
                    new AccountSelectionResult(TokenAcquisitionStatus.Acquired, "new.tenant"))));

        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    private static AccountSwitchCoordinator Build(
        CoordinationFixture fixture,
        SelectedAccountStore selected) =>
        new(
            fixture.Tombstones,
            fixture.Commits,
            fixture.Paths,
            fixture.Cache,
            selected,
            fixture.Logger);

    private sealed class FailingProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new System.Security.Cryptography.CryptographicException("simulated");

        public byte[] Unprotect(byte[] ciphertext, byte[] entropy) => ciphertext;
    }

    private sealed class ThrowingCommitAction : IStateCommitAction
    {
        public CacheCommitResult Execute(in MutationLock heldLock) =>
            throw new UnauthorizedAccessException("simulated");
    }
}
