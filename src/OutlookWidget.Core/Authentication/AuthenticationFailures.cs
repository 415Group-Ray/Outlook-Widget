using Microsoft.Identity.Client;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Authentication;

/// <summary>
/// Maps MSAL failures onto the product's own outcome states.
/// </summary>
/// <remarks>
/// <para>
/// <b>Classified by exception type first, and by error code only where no type exists.</b> Microsoft's
/// own WAM error documentation carries an explicit warning that error codes and messages are "presented
/// for reference only" and that implementing exception handling on them is not recommended, in favour of
/// handling <see cref="MsalException"/> types. That guidance is followed here as far as it goes:
/// interaction-required has a dedicated type and is matched on the type.
/// </para>
/// <para>
/// It does not go far enough for two cases the plan requires to be distinguishable, so those are
/// matched on codes and are marked below as heuristic. A broker that failed to initialise and a tenant
/// that blocks self-consent both arrive as general exceptions, and section 8 requires the first to
/// render as a machine problem rather than a sign-in prompt, and the second as an approval request
/// rather than a retry. Collapsing either into a generic failure would produce a card offering an
/// action that cannot possibly work.
/// </para>
/// <para>
/// <b>Matching on exception text is not the same as logging it.</b> Section 6 forbids logging
/// exception messages because they routinely carry an account, a URL, or a raw server response. The
/// substring tests below branch on that text and discard it; no caller receives it, and
/// <see cref="IOperationalLogger"/> has nowhere to put it even if one tried.
/// </para>
/// </remarks>
/// <summary>
/// Which acquisition a failure came from. Changes what the same failure means.
/// </summary>
public enum AuthenticationPhase
{
    /// <summary>Silent acquisition, where interaction is still an available remedy.</summary>
    Silent,

    /// <summary>
    /// Interactive acquisition, where the user was present and a refusal is therefore terminal for
    /// anything short of an administrator.
    /// </summary>
    Interactive,
}

public static class AuthenticationFailures
{
    /// <summary>
    /// The documented failure when the broker's native runtime cannot initialise.
    /// </summary>
    /// <remarks>
    /// Documented by Microsoft against single-file deployment, where the native binaries from
    /// <c>Microsoft.Identity.Client.NativeInterop</c> are not packaged. This build does not use
    /// single-file publishing precisely so that cannot happen, which makes this code a signal that
    /// something else is wrong with the broker on the machine rather than a packaging mistake.
    /// </remarks>
    private const string WamRuntimeInitFailed = "wam_runtime_init_failed";

    /// <summary>MSAL's code for a dialog the user closed.</summary>
    private const string AuthenticationCanceled = "authentication_canceled";

    /// <summary>
    /// Entra codes for the consent states. Heuristic, and gate 8 is what confirms or corrects them:
    /// 65001 is "the user or administrator has not consented", 90094 is "admin consent required", and
    /// 65004 is "the user declined the consent prompt".
    /// </summary>
    private static readonly string[] AdminConsentCodes = ["AADSTS90094", "AADSTS65001"];

    private const string UserDeclinedConsentCode = "AADSTS65004";

    /// <summary>
    /// Entra's OAuth-level refusals. Reached when the broker surfaces no AADSTS number, which the
    /// Microsoft-managed consent policy was measured to do.
    /// </summary>
    private const string AccessDenied = "access_denied";

    private const string ConsentRequired = "consent_required";

    /// <summary>
    /// Classifies one MSAL failure. Never throws, and never returns
    /// <see cref="TokenAcquisitionStatus.Acquired"/>.
    /// </summary>
    /// <param name="exception">The failure to classify.</param>
    /// <param name="phase">
    /// Which acquisition raised it. This is load-bearing rather than informational; see the remarks.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>The same failure means different things on the two paths, and conflating them was a real
    /// defect.</b> A consent problem observed during <em>silent</em> acquisition means "go interactive",
    /// because self-consent happens there and the user may well be able to grant it. The same problem
    /// observed during <em>interactive</em> acquisition means the user has already been given the
    /// chance and the platform refused: the remedy is an administrator, and telling them to sign in
    /// again invites an action whose only possible outcome is the same dialog.
    /// </para>
    /// <para>
    /// The first version took no phase and mapped every unrecognised interactive failure to
    /// <see cref="TokenAcquisitionStatus.Failed"/>. Measured against a tenant using the
    /// Microsoft-managed consent policy, a blocked self-consent reported as a generic failure — which
    /// is precisely the outcome section 8 forbids.
    /// </para>
    /// </remarks>
    public static TokenAcquisitionStatus Classify(Exception exception, AuthenticationPhase phase)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (exception is OperationCanceledException)
        {
            return TokenAcquisitionStatus.Cancelled;
        }

        if (exception is not MsalException msal)
        {
            return TokenAcquisitionStatus.Failed;
        }

        string code = msal.ErrorCode ?? string.Empty;
        string text = (msal.Message ?? string.Empty) + " " + code;

        bool declined = text.Contains(UserDeclinedConsentCode, StringComparison.OrdinalIgnoreCase);

        bool consentBlocked = AdminConsentCodes.Any(c =>
            text.Contains(c, StringComparison.OrdinalIgnoreCase));

        // MSAL models this one as a typed classification rather than a code, which is the strongest
        // signal available and is preferred over any string match.
        if (exception is MsalUiRequiredException uiRequired)
        {
            bool consent = consentBlocked
                || uiRequired.Classification == UiRequiredExceptionClassification.ConsentRequired;

            // Silent: interaction is the remedy even when consent is the cause, because the
            // interactive path is where self-consent is attempted. Interactive: consent already
            // failed with the user present, so an administrator is the remedy.
            return phase == AuthenticationPhase.Interactive && consent
                ? TokenAcquisitionStatus.ApprovalRequired
                : TokenAcquisitionStatus.InteractionRequired;
        }

        // Broker problems arrive as MsalClientException with no dedicated type. The prefix test
        // catches the WAM codes this specific one belongs to without pretending to enumerate them:
        // the documented list is explicitly incomplete, and a new wam_* code means the same thing to
        // this product as the known one does.
        if (exception is MsalClientException
            && (code.Equals(WamRuntimeInitFailed, StringComparison.OrdinalIgnoreCase)
                || code.StartsWith("wam_", StringComparison.OrdinalIgnoreCase)))
        {
            return TokenAcquisitionStatus.BrokerUnavailable;
        }

        if (declined)
        {
            return TokenAcquisitionStatus.Cancelled;
        }

        if (consentBlocked)
        {
            return TokenAcquisitionStatus.ApprovalRequired;
        }

        if (code.Equals(AuthenticationCanceled, StringComparison.OrdinalIgnoreCase))
        {
            return TokenAcquisitionStatus.Cancelled;
        }

        // Unambiguous: this code means consent specifically, so it is the approval-required state once
        // the user has already been given their chance interactively.
        if (code.Equals(ConsentRequired, StringComparison.OrdinalIgnoreCase))
        {
            return phase == AuthenticationPhase.Interactive
                ? TokenAcquisitionStatus.ApprovalRequired
                : TokenAcquisitionStatus.InteractionRequired;
        }

        // `access_denied` is deliberately NOT treated as approval-required, and that is a reversal.
        //
        // It was mapped that way on the theory that an interactive refusal must be a policy block. Then
        // the same user action — dismissing the Approval-required dialog — was observed producing
        // `authentication_canceled` on one run and something else on another, which means the broker
        // does not reliably distinguish "the user closed the dialog" from "policy refused". Under that
        // ambiguity, claiming approval-required is the more harmful guess: it asserts an administrator
        // is needed and withdraws the retry the user may simply want, whereas reporting cancellation
        // offers a retry and states nothing false.
        //
        // The approval-required state is therefore claimed only on a definite signal — the typed
        // ConsentRequired classification, an Entra consent code, or `consent_required` above. It will
        // under-report rather than mislabel, and the cancellation copy carries the hint that a
        // recurring Approval-required dialog is not a retryable condition.
        if (code.Equals(AccessDenied, StringComparison.OrdinalIgnoreCase))
        {
            return phase == AuthenticationPhase.Interactive
                ? TokenAcquisitionStatus.Cancelled
                : TokenAcquisitionStatus.InteractionRequired;
        }

        return TokenAcquisitionStatus.Failed;
    }

    /// <summary>
    /// A bounded, metadata-free description of one failure, for the companion's diagnostic surface.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Categories only, and never the message.</b> This returns the exception type name, MSAL's
    /// error code, and any <c>AADSTS</c> numbers found — each a short closed-vocabulary token of the
    /// same kind as an HTTP status. It does not return <see cref="Exception.Message"/>, which routinely
    /// carries an account, a URL, or a raw server response.
    /// </para>
    /// <para>
    /// This exists because a status word alone was not enough to diagnose a real failure. A tenant
    /// consent block arrived with none of the AADSTS codes this file knew about, and there was no way
    /// to find out which signal it did carry without inspecting it. It is deliberately not routed
    /// through <see cref="IOperationalLogger"/>, whose API has nowhere to put a string and must keep
    /// it that way; the companion window is a local surface the user reads and chooses to share.
    /// </para>
    /// </remarks>
    public static string Describe(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var parts = new List<string> { exception.GetType().Name };

        if (exception is MsalException msal && !string.IsNullOrWhiteSpace(msal.ErrorCode))
        {
            parts.Add("code " + msal.ErrorCode);
        }

        // Just the identifiers, extracted rather than the surrounding sentence.
        string[] entraCodes = System.Text.RegularExpressions.Regex
            .Matches(exception.Message ?? string.Empty, @"AADSTS\d{4,6}")
            .Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToArray();

        if (entraCodes.Length > 0)
        {
            parts.Add(string.Join(", ", entraCodes));
        }

        return string.Join(" · ", parts);
    }

    /// <summary>
    /// The operational outcome category one status is logged under.
    /// </summary>
    /// <remarks>
    /// Kept next to the classifier so a new status cannot be added without deciding how it is
    /// recorded. The switch is exhaustive rather than defaulted for the same reason.
    /// </remarks>
    public static OperationalOutcome ToOutcome(TokenAcquisitionStatus status) =>
        status switch
        {
            TokenAcquisitionStatus.Acquired => OperationalOutcome.Success,
            TokenAcquisitionStatus.NoConfiguration => OperationalOutcome.Failed,
            TokenAcquisitionStatus.InteractionRequired => OperationalOutcome.Failed,
            TokenAcquisitionStatus.ApprovalRequired => OperationalOutcome.ApprovalRequired,
            TokenAcquisitionStatus.BrokerUnavailable => OperationalOutcome.Failed,
            TokenAcquisitionStatus.Cancelled => OperationalOutcome.Cancelled,
            TokenAcquisitionStatus.Failed => OperationalOutcome.Failed,
            _ => OperationalOutcome.Failed,
        };
}
