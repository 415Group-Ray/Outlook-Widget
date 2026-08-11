using OutlookWidget.Core.Refresh;
using OutlookWidget.Provider;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Core.Tests;

public sealed class PeerRefreshMonitorTests
{
    [Fact]
    public async Task A_debounced_retry_cannot_end_the_peer_monitor()
    {
        var presentation = new RefreshPresentation();
        long peerWaitId = BeginPeerWait(presentation);
        int deliveries = 0;

        var monitor = new PeerRefreshMonitor(
            isRefreshInProgress: () => false,
            readGeneration: () => 10,
            requestDelivery: () => deliveries++,
            delay: (_, _) =>
            {
                RefreshPresentationState previous = presentation.Begin();
                presentation.Complete(Result(RefreshOutcome.SkippedDebounce), previous);
                return Task.CompletedTask;
            });

        await monitor.RunAsync(presentation, peerWaitId, 10, CancellationToken.None);

        Assert.Equal(RefreshPresentationStatus.StatusUnknown, presentation.Current.Status);
        Assert.Null(presentation.Current.PeerWaitId);
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task A_stale_monitor_exits_without_polling_or_delivery()
    {
        var presentation = new RefreshPresentation();
        long stalePeerWaitId = BeginPeerWait(presentation);
        long currentPeerWaitId = BeginPeerWait(presentation);
        int polls = 0;
        int deliveries = 0;

        var monitor = new PeerRefreshMonitor(
            isRefreshInProgress: () =>
            {
                polls++;
                return false;
            },
            readGeneration: () => 1,
            requestDelivery: () => deliveries++,
            delay: (_, _) => Task.CompletedTask);

        await monitor.RunAsync(presentation, stalePeerWaitId, 0, CancellationToken.None);

        Assert.Equal(0, polls);
        Assert.Equal(0, deliveries);
        Assert.Equal(currentPeerWaitId, presentation.Current.PeerWaitId);
    }

    [Fact]
    public async Task A_live_lease_is_polled_until_generation_proves_peer_commit()
    {
        var presentation = new RefreshPresentation();
        long peerWaitId = BeginPeerWait(presentation);
        int leaseReads = 0;
        int deliveries = 0;

        var monitor = new PeerRefreshMonitor(
            isRefreshInProgress: () => ++leaseReads == 1,
            readGeneration: () => 12,
            requestDelivery: () => deliveries++,
            delay: (_, _) => Task.CompletedTask);

        await monitor.RunAsync(presentation, peerWaitId, 11, CancellationToken.None);

        Assert.Equal(2, leaseReads);
        Assert.Equal(RefreshPresentationStatus.Idle, presentation.Current.Status);
        Assert.Null(presentation.Current.PeerWaitId);
        Assert.Equal(1, deliveries);
    }

    [Fact]
    public async Task An_unknown_initial_generation_cannot_clear_authorization_suppression()
    {
        var presentation = new RefreshPresentation();
        presentation.ReportGraphStatus(OutlookWidget.Core.Graph.GraphMailStatus.Unauthorized);
        long peerWaitId = BeginPeerWait(presentation);
        int deliveries = 0;

        var monitor = new PeerRefreshMonitor(
            isRefreshInProgress: () => false,
            readGeneration: () => 12,
            requestDelivery: () => deliveries++,
            delay: (_, _) => Task.CompletedTask);

        await monitor.RunAsync(presentation, peerWaitId, null, CancellationToken.None);

        Assert.Equal(RefreshPresentationStatus.StatusUnknown, presentation.Current.Status);
        Assert.True(presentation.Current.AuthorizationInvalidatesDetails);
        Assert.Null(presentation.Current.PeerWaitId);
        Assert.Equal(1, deliveries);
    }

    private static long BeginPeerWait(RefreshPresentation presentation)
    {
        RefreshPresentationState previous = presentation.Begin();
        return presentation.Complete(Result(RefreshOutcome.SkippedLeaseHeld), previous)!.Value;
    }

    private static RefreshResult Result(RefreshOutcome outcome) =>
        new(outcome, DeliveryRequestOutcome.NotRequested, 0, TimeSpan.Zero);
}
