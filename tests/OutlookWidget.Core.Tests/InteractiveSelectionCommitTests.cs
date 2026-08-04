using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Covers the publication an interactive sign-in performs: replacing the selected identifier and
/// deciding, in the same critical section, what happens to the mailbox the previous selection owned.
/// </summary>
/// <remarks>
/// The defect these pin is that sign-in used to publish the identifier alone. A sign-in landing on a
/// different account than the one recorded left the previous account's snapshot committed and
/// unsuppressed, and the provider renders committed state rather than waiting for a refresh — so the
/// previous mailbox stayed on screen under the new account's selection.
/// </remarks>
public sealed class InteractiveSelectionCommitTests
{
    private static readonly AuthenticationOptions Registration =
        AuthenticationOptions.TryCreate(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"))!;

    private static byte[] SnapshotFor(string homeAccountId) =>
        new MailboxSnapshot
        {
            TenantId = Registration.TenantId,
            HomeAccountId = homeAccountId,
            TotalItemCount = 12,
            UnreadItemCount = 3,
            Messages =
            [
                new MessagePreview
                {
                    DisplaySender = "Dana Fry",
                    Subject = "Quarterly review",
                    ReceivedAt = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero),
                    IsRead = false,
                },
            ],
            RefreshedAtUtc = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero),
        }.Serialize();

    private static StateCommitResult Publish(
        CoordinationFixture fixture,
        SelectedAccountStore selected,
        string homeAccountId) =>
        fixture.Commits.CommitDisclosureChange(
            new CommitInteractiveSelectionAction(
                fixture.Paths,
                fixture.Cache,
                selected,
                homeAccountId,
                fixture.Logger),
            Diagnostics.OperationalEventId.SignInPublicationFailed);

    [Fact]
    public void Signing_in_to_a_different_account_removes_the_previous_accounts_mailbox()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("account.a"));
        fixture.SeedState(SnapshotFor("account.a"));

        StateCommitResult commit = Publish(fixture, selected, "account.b");

        Assert.True(commit.IsCommitted);

        // Cleared rather than merely superseded: the previous account's senders and subjects are gone
        // from committed state, so no delivery pass can render them under the new selection.
        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
        Assert.Equal("account.b", selected.Read().HomeAccountId);
    }

    [Fact]
    public void Signing_in_again_to_the_same_account_keeps_its_mailbox_and_its_generation()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("account.a"));
        long seeded = fixture.SeedState(SnapshotFor("account.a"));

        // The ordinary re-sign-in: an expired token, the same mailbox. Clearing here would blank the
        // widget and force a refetch of data that is still correct, and would report a cache clear on
        // the card that nothing asked for.
        StateCommitResult commit = Publish(fixture, selected, "account.a");

        Assert.True(commit.IsCommitted);
        Assert.Equal(CacheReadStatus.Success, fixture.Cache.Read().Status);
        Assert.Equal(seeded, fixture.Cache.ReadGeneration());
        Assert.Equal(seeded, commit.Generation);
        Assert.Equal("account.a", selected.Read().HomeAccountId);
    }

    [Fact]
    public void A_payload_that_names_no_account_is_treated_as_foreign_and_removed()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);

        // Present, readable, and not a snapshot. It cannot be attributed to any account, so invariant
        // 5's direction applies: remove it rather than assume it is safe to keep.
        fixture.SeedState("not a snapshot"u8.ToArray());

        Assert.True(Publish(fixture, selected, "account.b").IsCommitted);

        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
        Assert.Equal("account.b", selected.Read().HomeAccountId);
    }

    [Fact]
    public void A_first_sign_in_records_the_account_without_clearing_a_cache_that_never_existed()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);

        Assert.True(Publish(fixture, selected, "account.a").IsCommitted);

        // Absent, not Cleared. A fresh install must not present itself as a completed sign-out, and
        // the generation stays where it was because committed mailbox state did not change.
        Assert.Equal(CacheReadStatus.Absent, fixture.Cache.Read().Status);
        Assert.Equal(0, fixture.Cache.ReadGeneration());
        Assert.Equal("account.a", selected.Read().HomeAccountId);
    }

    [Fact]
    public void Publication_retires_a_stale_approval_required_record()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        AuthorizationStateStore.Write(
            fixture.Paths,
            Registration,
            TokenAcquisitionStatus.ApprovalRequired,
            DateTimeOffset.UtcNow,
            fixture.Logger);

        Assert.True(Publish(fixture, selected, "account.a").IsCommitted);

        // Consent that was refused has plainly stopped being refused, and leaving the record would
        // relabel the next unrelated interaction-required as needing an administrator.
        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));
    }

    [Fact]
    public void A_failed_identifier_replacement_retains_the_prior_complete_selection()
    {
        using var fixture = new CoordinationFixture();
        var selected = new SelectedAccountStore(fixture.Paths, Registration, fixture.Logger);
        Assert.True(selected.Write("account.a"));
        fixture.SeedState(SnapshotFor("account.a"));

        var failingWriter = new SelectedAccountStore(
            fixture.Paths,
            Registration,
            fixture.Logger,
            new FailingProtector());

        StateCommitResult commit = fixture.Commits.CommitDisclosureChange(
            new CommitInteractiveSelectionAction(
                fixture.Paths,
                fixture.Cache,
                failingWriter,
                "account.b",
                fixture.Logger),
            Diagnostics.OperationalEventId.SignInPublicationFailed);

        Assert.Equal(StateCommitOutcome.CommitFailed, commit.Outcome);

        // The identifier is replaced last, so a partial failure leaves a retry scoped to one account
        // rather than falling back to removing every account cached for this registration.
        Assert.Equal("account.a", selected.Read().HomeAccountId);

        // The foreign mailbox is still gone. That ordering is deliberate: the prior account's mail
        // must not survive a publication that got far enough to remove it.
        Assert.Equal(CacheReadStatus.Cleared, fixture.Cache.Read().Status);
    }

    private sealed class FailingProtector : IDataProtector
    {
        public byte[] Protect(byte[] plaintext, byte[] entropy) =>
            throw new System.Security.Cryptography.CryptographicException("simulated");

        public byte[] Unprotect(byte[] ciphertext, byte[] entropy) => ciphertext;
    }
}
