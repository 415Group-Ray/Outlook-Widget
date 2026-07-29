using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Covers the one authorization state the provider cannot derive for itself.
/// </summary>
/// <remarks>
/// Classification is phase-aware, so a consent failure during silent acquisition is reported as
/// interaction-required on purpose. That means the provider can never conclude approval-required however
/// many times it retries: only the companion learns it, by being refused interactively. This store is how
/// that crosses the process boundary, and these tests pin the asymmetries that make it safe.
/// </remarks>
public sealed class AuthorizationStateStoreTests
{
    private static readonly DateTimeOffset When = DateTimeOffset.UnixEpoch.AddYears(56);

    [Fact]
    public void A_recorded_outcome_round_trips()
    {
        using var fixture = new CoordinationFixture();

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths));

        AuthorizationStateStore.Write(
            fixture.Paths, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthorizationStateStore.TryRead(fixture.Paths));
    }

    [Fact]
    public void Clearing_removes_it_and_is_safe_when_absent()
    {
        using var fixture = new CoordinationFixture();

        // Clearing first, with nothing written, must not throw: the companion clears on every successful
        // sign-in, which is usually the case where nothing was ever recorded.
        AuthorizationStateStore.Clear(fixture.Paths);

        AuthorizationStateStore.Write(
            fixture.Paths, TokenAcquisitionStatus.ApprovalRequired, When);
        AuthorizationStateStore.Clear(fixture.Paths);

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths));
    }

    [Theory]
    [InlineData(TokenAcquisitionStatus.Acquired)]
    [InlineData(TokenAcquisitionStatus.InteractionRequired)]
    [InlineData(TokenAcquisitionStatus.BrokerUnavailable)]
    [InlineData(TokenAcquisitionStatus.Cancelled)]
    [InlineData(TokenAcquisitionStatus.Failed)]
    public void Only_a_terminal_approval_outcome_may_be_recorded(TokenAcquisitionStatus status)
    {
        // The narrow contract is the point. If a transient failure could be written here it would
        // persist as though it were a policy decision, and the provider would keep reporting it long
        // after the network recovered.
        using var fixture = new CoordinationFixture();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => AuthorizationStateStore.Write(fixture.Paths, status, When));
    }

    [Fact]
    public void Refinement_upgrades_interaction_required_to_approval_required()
    {
        // The whole purpose: the provider's silent classifier said "sign in", the companion knows better,
        // and the card must present the approval guidance rather than a sign-in it cannot complete.
        using var fixture = new CoordinationFixture();

        AuthorizationStateStore.Write(
            fixture.Paths, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthorizationStateStore.Refine(
                TokenAcquisitionStatus.InteractionRequired, fixture.Paths));
    }

    [Fact]
    public void Refinement_never_overrides_an_acquired_token()
    {
        // This is what makes a stale record harmless and why the store needs no expiry. If an
        // administrator grants consent afterwards, silent acquisition starts succeeding, and the record
        // must not drag a working widget back to an approval-required card.
        using var fixture = new CoordinationFixture();

        AuthorizationStateStore.Write(
            fixture.Paths, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(
            TokenAcquisitionStatus.Acquired,
            AuthorizationStateStore.Refine(TokenAcquisitionStatus.Acquired, fixture.Paths));
    }

    [Theory]
    [InlineData(TokenAcquisitionStatus.BrokerUnavailable)]
    [InlineData(TokenAcquisitionStatus.NoConfiguration)]
    [InlineData(TokenAcquisitionStatus.Failed)]
    public void Refinement_leaves_unrelated_failures_alone(TokenAcquisitionStatus silent)
    {
        // A broker that will not initialise is not evidence about consent, and relabelling it would send
        // the user to an administrator for a machine problem.
        using var fixture = new CoordinationFixture();

        AuthorizationStateStore.Write(
            fixture.Paths, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(silent, AuthorizationStateStore.Refine(silent, fixture.Paths));
    }

    [Fact]
    public void An_unreadable_record_produces_no_refinement()
    {
        // Deliberately the opposite direction from disclosure state, which fails closed. There the risk
        // is showing mail that should be hidden; here the risk is asserting an administrator is required
        // and withdrawing a retry the user may simply want. So corruption yields the less specific state
        // rather than the more alarming one.
        using var fixture = new CoordinationFixture();

        File.WriteAllText(fixture.Paths.AuthorizationStateFilePath, "{ not json at all");

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths));

        Assert.Equal(
            TokenAcquisitionStatus.InteractionRequired,
            AuthorizationStateStore.Refine(
                TokenAcquisitionStatus.InteractionRequired, fixture.Paths));
    }

    [Fact]
    public void A_record_naming_some_other_status_is_ignored()
    {
        // Guards against a hand-edited or future-version file widening what this store can assert.
        using var fixture = new CoordinationFixture();

        File.WriteAllText(
            fixture.Paths.AuthorizationStateFilePath,
            """{ "status": "Acquired", "recordedAtUtc": "2026-07-29T00:00:00+00:00" }""");

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths));
    }

    [Fact]
    public void The_record_lives_beside_the_other_coordination_state()
    {
        // Inside the package store, so uninstall removes it with everything else.
        using var fixture = new CoordinationFixture();

        Assert.StartsWith(
            fixture.Paths.RootDirectory,
            fixture.Paths.AuthorizationStateFilePath,
            StringComparison.Ordinal);
    }
}
