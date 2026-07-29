using Microsoft.Identity.Client;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Covers how MSAL failures become product states, and the disclosure rules on the result type.
/// </summary>
/// <remarks>
/// These are unit tests over classification and shape. They establish nothing about WAM, the broker,
/// the tenant, or whether a zero parent handle works — gates 8 and 9 are the only things that can, and
/// a green run here must never be read as evidence for either.
/// </remarks>
public sealed class AuthenticationOutcomeTests
{
    [Fact]
    public void Interaction_required_is_matched_on_the_exception_type()
    {
        // The one outcome MSAL models as its own type, and the most consequential to get right: it is
        // the difference between a card saying "sign in" and one saying "something is broken".
        Assert.Equal(
            TokenAcquisitionStatus.InteractionRequired,
            AuthenticationFailures.Classify(
                new MsalUiRequiredException("code", "message"),
                AuthenticationPhase.Silent));
    }

    [Fact]
    public void On_the_silent_path_a_consent_code_still_means_go_interactive()
    {
        // This looks wrong and is not. A silent acquisition against an app that has never been
        // consented to throws MsalUiRequiredException carrying AADSTS65001, and the correct remedy is
        // still to go interactive: self-consent is attempted there, and only if the tenant refuses it
        // does the approval-required state apply. Classifying this as ApprovalRequired would tell the
        // user to contact an administrator for something they may be able to grant themselves.
        var exception = new MsalUiRequiredException(
            "invalid_grant",
            "AADSTS65001: The user or administrator has not consented.");

        Assert.Equal(
            TokenAcquisitionStatus.InteractionRequired,
            AuthenticationFailures.Classify(exception, AuthenticationPhase.Silent));
    }

    [Fact]
    public void On_the_interactive_path_the_same_consent_code_means_approval_required()
    {
        // The other half of the pair above, and the distinction the phase parameter exists for. The
        // user was present and saw a dialog; consent failed anyway. Sending them round the same loop
        // is the one thing that definitely will not work.
        var exception = new MsalUiRequiredException(
            "invalid_grant",
            "AADSTS65001: The user or administrator has not consented.");

        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthenticationFailures.Classify(exception, AuthenticationPhase.Interactive));
    }

    [Fact]
    public void The_typed_consent_classification_is_honoured_without_any_aadsts_code()
    {
        // MSAL's own typed classification, which is a stronger signal than any string match and the
        // one Microsoft's guidance points at. Measured need: a real tenant consent block arrived
        // carrying no AADSTS number at all.
        var exception = new MsalUiRequiredException(
            "invalid_grant",
            "no entra code in this message",
            innerException: null,
            classification: UiRequiredExceptionClassification.ConsentRequired);

        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthenticationFailures.Classify(exception, AuthenticationPhase.Interactive));
    }

    [Fact]
    public void The_consent_required_code_is_approval_required_once_interactive_has_been_tried()
    {
        // Unambiguous by name: this code is about consent specifically, so no inference is needed.
        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthenticationFailures.Classify(
                new MsalServiceException("consent_required", "no entra code here"),
                AuthenticationPhase.Interactive));

        // On the silent path the same refusal is not yet terminal: interactive is still worth trying.
        Assert.Equal(
            TokenAcquisitionStatus.InteractionRequired,
            AuthenticationFailures.Classify(
                new MsalServiceException("consent_required", "no entra code here"),
                AuthenticationPhase.Silent));
    }

    [Fact]
    public void A_generic_access_denied_is_not_claimed_as_approval_required()
    {
        // This encodes a reversal, so it is worth stating why rather than just asserting the value.
        //
        // `access_denied` was briefly mapped to ApprovalRequired on the interactive path, reasoning
        // that a refusal with the user present must be a policy block. Measurement contradicted the
        // premise: dismissing the same Approval-required dialog produced `authentication_canceled` on
        // one run and a different signal on another, so the broker does not reliably separate "the user
        // closed the dialog" from "policy refused".
        //
        // Given that ambiguity, ApprovalRequired is the more harmful guess. It asserts an administrator
        // is required and removes the retry affordance, so a user who merely closed a dialog is told
        // something false and given no way forward. Cancellation offers the retry and claims nothing
        // that was not observed. The state is claimed only on a definite signal.
        Assert.Equal(
            TokenAcquisitionStatus.Cancelled,
            AuthenticationFailures.Classify(
                new MsalServiceException("access_denied", "no entra code here"),
                AuthenticationPhase.Interactive));

        // But a definite consent signal in the same exception still wins, because then it is not a
        // guess. This is what keeps the reversal from also discarding the real detection.
        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthenticationFailures.Classify(
                new MsalServiceException("access_denied", "AADSTS90094: admin consent required"),
                AuthenticationPhase.Interactive));
    }

    [Fact]
    public void The_observed_cancellation_signal_classifies_as_cancellation()
    {
        // Measured on the reference tenant: dismissing the Approval-required dialog produced exactly
        // this. Recorded as a test because it is the one signal for this path that was actually
        // observed rather than reasoned about, and because an earlier report of a generic failure for
        // the same user action is not explained by it — this code was always handled.
        Assert.Equal(
            TokenAcquisitionStatus.Cancelled,
            AuthenticationFailures.Classify(
                new MsalClientException("authentication_canceled", "user closed the dialog"),
                AuthenticationPhase.Interactive));
    }

    [Theory]
    [InlineData("AADSTS90094: Admin consent is required.")]
    [InlineData("AADSTS65001: The user or administrator has not consented.")]
    public void Consent_failures_outside_the_ui_required_type_are_approval_required(string message)
    {
        // Section 8 requires this to be visibly distinguishable from a Graph 403 and from a retryable
        // failure, because the remedy is an administrator request. Collapsing it into Failed would put
        // a Refresh button on a card where refreshing can never work.
        var exception = new MsalServiceException("invalid_grant", message);

        Assert.Equal(
            TokenAcquisitionStatus.ApprovalRequired,
            AuthenticationFailures.Classify(exception, AuthenticationPhase.Interactive));
    }

    [Theory]
    [InlineData("AADSTS90094: Admin consent is required.")]
    [InlineData("AADSTS65001: The user or administrator has not consented.")]
    public void The_same_consent_failure_during_silent_acquisition_still_offers_interaction(
        string message)
    {
        // Regression test for a bug with a nasty shape. This branch was not phase-aware, so a silent
        // acquisition surfacing a consent code as an MsalServiceException — rather than as the typed
        // MsalUiRequiredException the branch above handles — returned ApprovalRequired. That status
        // reports IsResolvedBySigningIn == false, so InteractiveAuthService returned early and never
        // opened the prompt at all.
        //
        // On a tenant where the user could self-consent, the product would therefore have declared an
        // administrator necessary without ever asking them. The failure is silent, looks like correct
        // fail-closed behaviour, and only appears on tenants where the flow should have worked.
        var exception = new MsalServiceException("invalid_grant", message);

        TokenAcquisitionStatus status =
            AuthenticationFailures.Classify(exception, AuthenticationPhase.Silent);

        Assert.Equal(TokenAcquisitionStatus.InteractionRequired, status);

        // The property that actually gates the escalation, asserted directly rather than inferred.
        Assert.True(TokenAcquisitionResult.Unavailable(status).IsResolvedBySigningIn);
    }

    [Fact]
    public void A_declined_consent_prompt_is_cancellation_rather_than_approval_required()
    {
        // The user said no. Nothing is misconfigured, an administrator is not needed, and pressing the
        // button again is a reasonable thing to offer.
        var exception = new MsalServiceException(
            "invalid_grant",
            "AADSTS65004: User declined to consent to access the app.");

        Assert.Equal(
            TokenAcquisitionStatus.Cancelled,
            AuthenticationFailures.Classify(exception, AuthenticationPhase.Interactive));
    }

    [Theory]
    [InlineData("wam_runtime_init_failed")]
    [InlineData("wam_some_code_that_did_not_exist_when_this_was_written")]
    public void Broker_failures_are_classified_by_code_prefix(string code)
    {
        // Broker problems arrive as MsalClientException with no dedicated type, and Microsoft's own
        // error list is explicitly incomplete. A new wam_* code means the same thing to this product
        // as the documented one, so the prefix is matched rather than an enumeration that would
        // silently degrade to a generic failure the first time Windows added one.
        Assert.Equal(
            TokenAcquisitionStatus.BrokerUnavailable,
            AuthenticationFailures.Classify(
                new MsalClientException(code, "message"),
                AuthenticationPhase.Silent));
    }

    [Fact]
    public void A_dismissed_dialog_is_cancellation()
    {
        Assert.Equal(
            TokenAcquisitionStatus.Cancelled,
            AuthenticationFailures.Classify(
                new MsalClientException("authentication_canceled", "message"),
                AuthenticationPhase.Interactive));
    }

    [Fact]
    public void A_cancelled_operation_is_cancellation_even_without_msal()
    {
        // The provider bounds its probe with a token, so this is a real path rather than a formality.
        Assert.Equal(
            TokenAcquisitionStatus.Cancelled,
            AuthenticationFailures.Classify(
                new OperationCanceledException(),
                AuthenticationPhase.Silent));
    }

    [Fact]
    public void An_unrecognised_failure_is_generic_rather_than_a_sign_in_prompt()
    {
        // Failing to a state that tells the user to sign in would be worse than failing to a generic
        // one: it invites an action that cannot address whatever actually went wrong.
        Assert.Equal(
            TokenAcquisitionStatus.Failed,
            AuthenticationFailures.Classify(
                new InvalidOperationException("something else"),
                AuthenticationPhase.Interactive));

        Assert.Equal(
            TokenAcquisitionStatus.Failed,
            AuthenticationFailures.Classify(
                new MsalServiceException("invalid_grant", "AADSTS50000"),
                AuthenticationPhase.Interactive));
    }

    [Fact]
    public void The_failure_description_carries_categories_and_never_the_message()
    {
        // The diagnostic surface exists because a status word could not diagnose a real failure. It
        // must not become a channel for the exception text, which routinely carries an account, a URL,
        // or a raw server response.
        const string secret = "user@contoso.com asked https://graph.microsoft.com/v1.0/me/messages";

        var exception = new MsalServiceException(
            "invalid_grant",
            $"AADSTS65001: consent missing. {secret}");

        string described = AuthenticationFailures.Describe(exception);

        Assert.Contains("MsalServiceException", described, StringComparison.Ordinal);
        Assert.Contains("invalid_grant", described, StringComparison.Ordinal);
        Assert.Contains("AADSTS65001", described, StringComparison.Ordinal);

        Assert.DoesNotContain("contoso", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("graph.microsoft.com", described, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("consent missing", described, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Only_approval_required_is_logged_as_approval_required()
    {
        // The operational log's status category is one of the two places section 8 requires the
        // consent state to be distinguishable, the card being the other.
        Assert.Equal(
            OperationalOutcome.ApprovalRequired,
            AuthenticationFailures.ToOutcome(TokenAcquisitionStatus.ApprovalRequired));

        foreach (TokenAcquisitionStatus status in Enum.GetValues<TokenAcquisitionStatus>())
        {
            if (status == TokenAcquisitionStatus.ApprovalRequired)
            {
                continue;
            }

            Assert.NotEqual(
                OperationalOutcome.ApprovalRequired,
                AuthenticationFailures.ToOutcome(status));
        }
    }

    [Fact]
    public void Every_status_has_a_deliberate_log_outcome()
    {
        // ToOutcome has a default arm so it cannot throw at runtime, which would otherwise mean a new
        // status silently logs as Failed. This is what makes adding one a decision rather than an
        // omission: the enum and the mapping are asserted to be the same size.
        Assert.All(
            Enum.GetValues<TokenAcquisitionStatus>(),
            status => Assert.True(Enum.IsDefined(AuthenticationFailures.ToOutcome(status))));

        Assert.Equal(7, Enum.GetValues<TokenAcquisitionStatus>().Length);
    }

    [Fact]
    public void The_result_never_prints_the_access_token()
    {
        // This is the reason TokenAcquisitionResult is a class with an explicit ToString rather than a
        // record. A positional record synthesises a ToString that prints every member, so
        // $"{result}" — the most natural thing anyone would write in a diagnostic — would emit the
        // bearer token. Deleting the override reintroduces that, and this test is what catches it.
        const string token = "eyJ0-this-must-never-be-printed";

        TokenAcquisitionResult result = TokenAcquisitionResult.Acquired(
            token,
            DateTimeOffset.UnixEpoch.AddYears(56));

        Assert.DoesNotContain(token, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(token, $"{result}", StringComparison.Ordinal);
        Assert.Contains("Acquired", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_tokenless_result_cannot_claim_to_have_acquired_one()
    {
        // Guards the pairing rather than trusting call sites: an Acquired status with a null token
        // would pass IsAcquired's status check and then hand null to a caller building an
        // Authorization header.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TokenAcquisitionResult.Unavailable(TokenAcquisitionStatus.Acquired));

        Assert.False(
            TokenAcquisitionResult.Unavailable(TokenAcquisitionStatus.Failed).IsAcquired);
    }

    [Theory]
    [InlineData(TokenAcquisitionStatus.InteractionRequired, true)]
    [InlineData(TokenAcquisitionStatus.Cancelled, true)]
    [InlineData(TokenAcquisitionStatus.BrokerUnavailable, false)]
    [InlineData(TokenAcquisitionStatus.ApprovalRequired, false)]
    [InlineData(TokenAcquisitionStatus.NoConfiguration, false)]
    public void Only_states_a_sign_in_can_fix_offer_one(
        TokenAcquisitionStatus status,
        bool expected)
    {
        // The companion escalates to an interactive prompt on this property, and the card offers its
        // sign-in action on the same distinction. A broker that will not initialise and a tenant that
        // requires admin consent both produce the same failure again, so prompting is worse than
        // reporting.
        Assert.Equal(expected, TokenAcquisitionResult.Unavailable(status).IsResolvedBySigningIn);
    }

    [Fact]
    public void The_shared_token_cache_sits_inside_the_package_store()
    {
        // MSAL's own MSIX example places its cache under %LocalAppData%\{AppName}, which measurement
        // on this machine showed is NOT redirected into the package store for a packaged full-trust
        // app. Following it would leave account metadata behind after uninstall, contradicting section
        // 11. The cache is placed with the rest of the coordination state instead.
        const string family = "415Group.OutlookInboxWidget_dgbvqhastx60y";

        CoordinationPaths paths = CoordinationPaths.Resolve(family);

        Assert.Equal(paths.RootDirectory, paths.TokenCacheDirectory);
        Assert.StartsWith(paths.RootDirectory, paths.TokenCacheFilePath, StringComparison.Ordinal);

        Assert.Contains(
            Path.Combine("Packages", family),
            paths.TokenCacheFilePath,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_token_cache_is_scoped_like_every_other_coordination_file()
    {
        // Two scopes must not share a cache, for the same reason they do not share a snapshot: tests
        // run concurrently against real files, and a shared MSAL cache would have them contending on
        // the helper's cross-process lock.
        var first = new CoordinationPaths("C:\\root", "scope-a");
        var second = new CoordinationPaths("C:\\root", "scope-b");

        Assert.NotEqual(first.TokenCacheFileName, second.TokenCacheFileName);
    }
}
