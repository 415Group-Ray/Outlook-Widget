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
    public void Peer_completion_requires_the_waiting_state_and_clears_authorization_suppression()
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);
        RefreshPresentationState previous = presentation.Begin();
        presentation.Complete(Result(RefreshOutcome.SkippedLeaseHeld), previous);

        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
        Assert.True(presentation.MarkCompleteIfWaiting());
        Assert.Equal(RefreshPresentationStatus.Idle, presentation.Current.Status);
        Assert.False(presentation.Current.AuthorizationInvalidatesDetails);
        Assert.False(presentation.MarkUnknownIfWaiting());
    }

    [Fact]
    public void An_uncommitted_peer_preserves_prior_authorization_suppression()
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(GraphMailStatus.Unauthorized);
        RefreshPresentationState previous = presentation.Begin();
        presentation.Complete(Result(RefreshOutcome.SkippedLeaseHeld), previous);

        Assert.True(presentation.MarkUnknownIfWaiting());
        Assert.Equal(RefreshPresentationStatus.StatusUnknown, presentation.Current.Status);
        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
    }

    private static RefreshResult Result(RefreshOutcome outcome) =>
        new(outcome, DeliveryRequestOutcome.NotRequested, 0, TimeSpan.Zero);
}
