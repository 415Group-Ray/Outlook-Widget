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

    /// <summary>The registration under test.</summary>
    private static readonly AuthenticationOptions Registration =
        AuthenticationOptions.TryCreate(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"))!;

    /// <summary>A different one, for the repointed-configuration case.</summary>
    private static readonly AuthenticationOptions OtherRegistration =
        AuthenticationOptions.TryCreate(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"))!;

    [Fact]
    public void A_recorded_outcome_round_trips()
    {
        using var fixture = new CoordinationFixture();

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));

        AuthorizationStateStore.Write(
            fixture.Paths, Registration, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthorizationStateStore.TryRead(fixture.Paths, Registration));
    }

    [Fact]
    public void Clearing_removes_it_and_is_safe_when_absent()
    {
        using var fixture = new CoordinationFixture();

        // Clearing first, with nothing written, must not throw: the companion clears on every successful
        // sign-in, which is usually the case where nothing was ever recorded.
        AuthorizationStateStore.Clear(fixture.Paths);

        AuthorizationStateStore.Write(
            fixture.Paths, Registration, TokenAcquisitionStatus.ApprovalRequired, When);
        AuthorizationStateStore.Clear(fixture.Paths);

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));
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
            () => AuthorizationStateStore.Write(fixture.Paths, Registration, status, When));
    }

    [Fact]
    public void Refinement_upgrades_interaction_required_to_approval_required()
    {
        // The whole purpose: the provider's silent classifier said "sign in", the companion knows better,
        // and the card must present the approval guidance rather than a sign-in it cannot complete.
        using var fixture = new CoordinationFixture();

        AuthorizationStateStore.Write(
            fixture.Paths, Registration, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthorizationStateStore.Refine(
                TokenAcquisitionStatus.InteractionRequired, fixture.Paths, Registration));
    }

    [Fact]
    public void A_resolved_consent_decision_must_not_outlive_its_resolution()
    {
        // Refine never overriding Acquired is necessary and was not sufficient, which is why the provider
        // now clears on success. Once an administrator grants consent the provider simply succeeds
        // silently, and the companion's success path -- the only other place that clears -- may never run
        // interactively again. The record would then survive indefinitely and relabel the next
        // *unrelated* interaction-required, such as a Conditional Access re-authentication or a removed
        // account, as still needing an administrator.
        //
        // This asserts the sequence rather than a single call: refusal recorded, consent granted and the
        // record cleared, then a later unrelated interaction-required stays interaction-required.
        using var fixture = new CoordinationFixture();

        AuthorizationStateStore.Write(
            fixture.Paths, Registration, TokenAcquisitionStatus.ApprovalRequired, When);

        // What the provider does on observing a successful silent acquisition.
        Assert.NotNull(AuthorizationStateStore.TryRead(fixture.Paths, Registration));
        AuthorizationStateStore.Clear(fixture.Paths);

        Assert.Equal(
            TokenAcquisitionStatus.InteractionRequired,
            AuthorizationStateStore.Refine(
                TokenAcquisitionStatus.InteractionRequired, fixture.Paths, Registration));
    }

    [Fact]
    public void Refinement_never_overrides_an_acquired_token()
    {
        // This is what makes a stale record harmless and why the store needs no expiry. If an
        // administrator grants consent afterwards, silent acquisition starts succeeding, and the record
        // must not drag a working widget back to an approval-required card.
        using var fixture = new CoordinationFixture();

        AuthorizationStateStore.Write(
            fixture.Paths, Registration, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(
            TokenAcquisitionStatus.Acquired,
            AuthorizationStateStore.Refine(
                TokenAcquisitionStatus.Acquired, fixture.Paths, Registration));
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
            fixture.Paths, Registration, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Equal(
            silent,
            AuthorizationStateStore.Refine(silent, fixture.Paths, Registration));
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

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));

        Assert.Equal(
            TokenAcquisitionStatus.InteractionRequired,
            AuthorizationStateStore.Refine(
                TokenAcquisitionStatus.InteractionRequired, fixture.Paths, Registration));
    }

    [Fact]
    public void A_record_from_another_registration_is_ignored()
    {
        // Consent is granted to a specific application in a specific tenant, so a record that outlives
        // its subject is not evidence about the new one.
        //
        // This is reachable in practice: coordination state lives under package identity, which does not
        // change when authentication.json is repointed at another client or tenant. Without the identity
        // check the old refusal would be applied to the new registration, telling the user an
        // administrator is required before self-consent had ever been attempted for it — the same harm as
        // claiming approval-required on an ambiguous signal, reached by a different route.
        using var fixture = new CoordinationFixture();

        AuthorizationStateStore.Write(
            fixture.Paths, Registration, TokenAcquisitionStatus.ApprovalRequired, When);

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, OtherRegistration));

        Assert.Equal(
            TokenAcquisitionStatus.InteractionRequired,
            AuthorizationStateStore.Refine(
                TokenAcquisitionStatus.InteractionRequired, fixture.Paths, OtherRegistration));

        // And the original registration still sees it: the record is scoped, not invalidated.
        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthorizationStateStore.TryRead(fixture.Paths, Registration));
    }

    [Fact]
    public void A_record_predating_the_identity_fields_is_ignored()
    {
        // Such a record deserializes with default (empty) GUIDs, which match no real registration. That
        // is the correct outcome rather than a compatibility gap: a record that cannot be attributed to a
        // registration cannot support a claim about one.
        using var fixture = new CoordinationFixture();

        File.WriteAllText(
            fixture.Paths.AuthorizationStateFilePath,
            """{ "status": "ApprovalRequired", "recordedAtUtc": "2026-07-29T00:00:00+00:00" }""");

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));
    }

    [Fact]
    public void A_record_naming_some_other_status_is_ignored()
    {
        // Guards against a hand-edited or future-version file widening what this store can assert.
        using var fixture = new CoordinationFixture();

        File.WriteAllText(
            fixture.Paths.AuthorizationStateFilePath,
            """{ "status": "Acquired", "recordedAtUtc": "2026-07-29T00:00:00+00:00" }""");

        Assert.Null(AuthorizationStateStore.TryRead(fixture.Paths, Registration));
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
