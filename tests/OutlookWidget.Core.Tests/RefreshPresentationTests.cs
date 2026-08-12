using OutlookWidget.Core.Graph;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Core.Tests;

public sealed class RefreshPresentationTests
{
    [Theory]
    [InlineData(GraphMailStatus.Unauthorized)]
    [InlineData(GraphMailStatus.Forbidden)]
    [InlineData(GraphMailStatus.MailboxNotSupported)]
    public void Authorization_suppression_survives_the_next_loading_state(GraphMailStatus status)
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(status);

        RefreshPresentationState previous = presentation.Begin();

        Assert.True(previous.AuthorizationInvalidatesDetails);
        Assert.Equal(RefreshPresentationStatus.Loading, presentation.Current.Status);
        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
    }

    [Fact]
    public void Only_a_successful_read_lifts_prior_authorization_suppression()
    {
        // The single result that establishes this token may read this mailbox.
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);
        presentation.Begin();

        presentation.ReportGraphStatus(GraphMailStatus.Success);

        Assert.False(presentation.Current.AuthorizationInvalidatesDetails);
    }

    [Theory]
    [InlineData(GraphMailStatus.NetworkFailure)]
    [InlineData(GraphMailStatus.TimedOut)]
    [InlineData(GraphMailStatus.Cancelled)]
    [InlineData(GraphMailStatus.InvalidResponse)]
    [InlineData(GraphMailStatus.ServiceFailure)]

    // Throttling and a missing item were briefly treated as recovery, on the reasoning that a 429 or
    // a 404 is a reply from a service that accepted the credential. A throttle can be applied at the
    // gateway before authorization is evaluated, and GraphMailClient already ranks Throttled below
    // Unauthorized as the less conclusive answer. Neither is worth the risk when being conservative
    // costs only a counts-only card until the next successful read.
    [InlineData(GraphMailStatus.Throttled)]
    [InlineData(GraphMailStatus.ItemNotFound)]
    public void An_inconclusive_result_preserves_prior_authorization_suppression(
        GraphMailStatus status)
    {
        // NetworkFailure was previously asserted to LIFT suppression, and that assertion encoded a
        // disclosure bug: a 401 followed by a dropped connection cleared the sticky decision, so the
        // next delivery serialized cached senders and subjects on the strength of a request that
        // never reached Graph. None of these statuses says anything about authorization — the
        // attempt did not complete — and the absence of evidence is not evidence.
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);
        presentation.Begin();

        presentation.ReportGraphStatus(status);

        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
    }

    [Theory]
    [InlineData(GraphMailStatus.NetworkFailure)]
    [InlineData(GraphMailStatus.TimedOut)]
    [InlineData(GraphMailStatus.ServiceFailure)]
    public void An_inconclusive_result_does_not_invent_suppression_that_was_never_there(
        GraphMailStatus status)
    {
        // The other direction of the same rule. Preserving the prior decision must not mean
        // defaulting to suppression: a healthy widget that loses its connection keeps showing the
        // mail it already had.
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Success);

        presentation.ReportGraphStatus(status);

        Assert.False(presentation.Current.AuthorizationInvalidatesDetails);
    }

    [Fact]
    public void A_debounced_retry_restores_both_prior_presentation_facts()
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Forbidden);
        RefreshPresentationState previous = presentation.Begin();

        presentation.Complete(Result(RefreshOutcome.SkippedDebounce), previous);

        Assert.Same(previous, presentation.Current);
    }

    [Fact]
    public void Peer_completion_uses_its_identity_and_clears_authorization_suppression()
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);
        RefreshPresentationState previous = presentation.Begin();
        long peerWaitId = presentation.Complete(Result(RefreshOutcome.SkippedLeaseHeld), previous)!.Value;

        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
        Assert.Equal(peerWaitId, presentation.Current.PeerWaitId);
        Assert.True(presentation.ResolvePeerWait(peerWaitId, peerCommitted: true));
        Assert.Equal(RefreshPresentationStatus.Idle, presentation.Current.Status);
        Assert.False(presentation.Current.AuthorizationInvalidatesDetails);
        Assert.Null(presentation.Current.PeerWaitId);
        Assert.False(presentation.ResolvePeerWait(peerWaitId, peerCommitted: false));
    }

    [Fact]
    public void An_uncommitted_peer_preserves_prior_authorization_suppression()
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);
        RefreshPresentationState previous = presentation.Begin();
        long peerWaitId = presentation.Complete(Result(RefreshOutcome.SkippedLeaseHeld), previous)!.Value;

        Assert.True(presentation.ResolvePeerWait(peerWaitId, peerCommitted: false));
        Assert.Equal(RefreshPresentationStatus.StatusUnknown, presentation.Current.Status);
        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
        Assert.Null(presentation.Current.PeerWaitId);
    }

    [Fact]
    public void Peer_monitor_identity_survives_loading_and_can_resolve_before_debounce_restores()
    {
        var presentation = new RefreshPresentation();
        RefreshPresentationState first = presentation.Begin();
        long peerWaitId = presentation.Complete(Result(RefreshOutcome.SkippedLeaseHeld), first)!.Value;

        RefreshPresentationState beforeDebounce = presentation.Begin();

        Assert.Equal(RefreshPresentationStatus.Loading, presentation.Current.Status);
        Assert.Equal(peerWaitId, presentation.Current.PeerWaitId);
        Assert.True(presentation.ResolvePeerWait(peerWaitId, peerCommitted: false));

        presentation.Complete(Result(RefreshOutcome.SkippedDebounce), beforeDebounce);

        Assert.Equal(RefreshPresentationStatus.StatusUnknown, presentation.Current.Status);
        Assert.Null(presentation.Current.PeerWaitId);
    }

    [Fact]
    public void A_stale_peer_monitor_cannot_overwrite_a_replacement_wait()
    {
        var presentation = new RefreshPresentation();
        RefreshPresentationState first = presentation.Begin();
        long firstPeerWaitId = presentation.Complete(
            Result(RefreshOutcome.SkippedLeaseHeld),
            first)!.Value;

        RefreshPresentationState second = presentation.Begin();
        long secondPeerWaitId = presentation.Complete(
            Result(RefreshOutcome.SkippedLeaseHeld),
            second)!.Value;

        Assert.NotEqual(firstPeerWaitId, secondPeerWaitId);
        Assert.False(presentation.ResolvePeerWait(firstPeerWaitId, peerCommitted: false));
        Assert.Equal(RefreshPresentationStatus.RefreshInProgress, presentation.Current.Status);
        Assert.Equal(secondPeerWaitId, presentation.Current.PeerWaitId);
    }

    private static RefreshResult Result(RefreshOutcome outcome) =>
        new(outcome, DeliveryRequestOutcome.NotRequested, 0, TimeSpan.Zero);
}
