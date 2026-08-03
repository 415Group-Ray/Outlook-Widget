using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

public sealed class SignOutCoordinatorTests
{
    private static readonly AuthenticationOptions Registration =
        AuthenticationOptions.TryCreate(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"))!;

    [Fact]
    public async Task Sign_out_suppresses_before_account_removal_then_commits_durable_signed_out_state()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("object.tenant"));
        fixture.SeedState("mailbox"u8.ToArray());
        AuthorizationStateStore.Write(
            fixture.Paths,
            Registration,
            TokenAcquisitionStatus.ApprovalRequired,
            DateTimeOffset.UtcNow,
            fixture.Logger);

        var coordinator = new SignOutCoordinator(
            fixture.Tombstones,
            fixture.Commits,
            new CommitSignedOutStateAction(
                fixture.Paths,
                fixture.Cache,
                selected,
                fixture.Logger),
            fixture.Logger);

        bool suppressionWasPublished = false;
        SignOutResult result = await coordinator.SignOutAsync(
            () =>
            {
                suppressionWasPublished =
                    fixture.Tombstones.GetEffectiveMode() == DisclosureMode.SignedOut;
                return Task.CompletedTask;
            });

        Assert.True(suppressionWasPublished);
        Assert.True(result.IsSignedOut);
        Assert.Equal(StateCommitOutcome.Committed, result.CommitOutcome);
        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
        Assert.Equal(SelectedAccountStatus.SignedOut, selected.Read().Status);
        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.True(fixture.Logger.Saw(
            Diagnostics.OperationalEventId.SignOutCompleted,
            Diagnostics.OperationalOutcome.Success));
    }

    [Fact]
    public async Task Account_removal_failure_leaves_prior_state_covered_by_the_signed_out_tombstone()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("object.tenant"));
        fixture.SeedState("mailbox"u8.ToArray());

        var coordinator = new SignOutCoordinator(
            fixture.Tombstones,
            fixture.Commits,
            new CommitSignedOutStateAction(
                fixture.Paths,
                fixture.Cache,
                selected,
                fixture.Logger),
            fixture.Logger);

        SignOutResult result = await coordinator.SignOutAsync(
            () => Task.FromException(new IOException("simulated")));

        Assert.Equal(SignOutOutcome.AccountRemovalFailed, result.Outcome);
        Assert.Equal(CacheReadStatus.Success, fixture.Cache.Read().Status);
        Assert.Equal(SelectedAccountStatus.Recorded, selected.Read().Status);
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
        Assert.True(fixture.Logger.Saw(
            Diagnostics.OperationalEventId.SignOutFailed,
            Diagnostics.OperationalOutcome.Failed));
    }

    [Fact]
    public async Task Local_commit_failure_leaves_the_signed_out_tombstone_in_force()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(
            fixture.Paths,
            Registration,
            fixture.Logger,
            new FailingProtector());
        fixture.SeedState("mailbox"u8.ToArray());

        var coordinator = new SignOutCoordinator(
            fixture.Tombstones,
            fixture.Commits,
            new CommitSignedOutStateAction(
                fixture.Paths,
                fixture.Cache,
                selected,
                fixture.Logger),
            fixture.Logger);

        SignOutResult result = await coordinator.SignOutAsync(() => Task.CompletedTask);

        Assert.Equal(SignOutOutcome.StateCommitFailed, result.Outcome);
        Assert.Equal(StateCommitOutcome.CommitFailed, result.CommitOutcome);
        Assert.Equal(CacheReadStatus.Success, fixture.Cache.Read().Status);
        Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    [Fact]
    public async Task Committed_sign_out_reports_recovery_needed_when_its_tombstone_cannot_be_cleared()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("object.tenant"));
        fixture.SeedState("mailbox"u8.ToArray());

        var coordinator = new SignOutCoordinator(
            fixture.Tombstones,
            fixture.Commits,
            new CommitSignedOutStateAction(
                fixture.Paths,
                fixture.Cache,
                selected,
                fixture.Logger),
            fixture.Logger);

        FileStream? blocker = null;

        try
        {
            SignOutResult result = await coordinator.SignOutAsync(
                () =>
                {
                    string marker = Assert.Single(
                        Directory.GetFiles(fixture.Paths.SuppressionDirectory, "*.suppress"));
                    blocker = new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.None);
                    return Task.CompletedTask;
                });

            Assert.Equal(SignOutOutcome.SuppressionClearFailed, result.Outcome);
            Assert.Equal(StateCommitOutcome.Committed, result.CommitOutcome);
            Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
            Assert.Equal(SelectedAccountStatus.SignedOut, selected.Read().Status);
            Assert.Equal(DisclosureMode.SignedOut, fixture.Tombstones.GetEffectiveMode());
        }
        finally
        {
            blocker?.Dispose();
        }

        Assert.Equal(1, fixture.Tombstones.ClearAllOrphans());
        Assert.Equal(DisclosureMode.Full, fixture.Tombstones.GetEffectiveMode());
    }

    private sealed class FailingProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new System.Security.Cryptography.CryptographicException("simulated");

        public byte[] Unprotect(byte[] ciphertext, byte[] entropy) => ciphertext;
    }
}
