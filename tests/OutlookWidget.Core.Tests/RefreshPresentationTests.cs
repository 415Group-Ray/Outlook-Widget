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

    [Theory]
    [InlineData(GraphMailStatus.Success)]
    [InlineData(GraphMailStatus.NetworkFailure)]
    [InlineData(GraphMailStatus.Throttled)]
    public void A_non_invalidating_Graph_result_lifts_prior_authorization_suppression(
        GraphMailStatus status)
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);
        presentation.Begin();

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
