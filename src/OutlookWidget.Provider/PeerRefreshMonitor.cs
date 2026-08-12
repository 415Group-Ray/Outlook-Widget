using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Provider;

/// <summary>
/// Observes one identified peer-held refresh lease until authoritative lease and generation state
/// resolves it, independently of temporary card presentation changes.
/// </summary>
internal sealed class PeerRefreshMonitor
{
    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private readonly Func<bool> _isRefreshInProgress;
    private readonly Func<long?> _readGeneration;
    private readonly Action _requestDelivery;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public PeerRefreshMonitor(
        Func<bool> isRefreshInProgress,
        Func<long?> readGeneration,
        Action requestDelivery,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        ArgumentNullException.ThrowIfNull(isRefreshInProgress);
        ArgumentNullException.ThrowIfNull(readGeneration);
        ArgumentNullException.ThrowIfNull(requestDelivery);

        _isRefreshInProgress = isRefreshInProgress;
        _readGeneration = readGeneration;
        _requestDelivery = requestDelivery;
        _delay = delay ?? Task.Delay;
    }

    /// <returns>
    /// <see langword="true"/> when this monitor observed the peer commit. The caller needs that
    /// answer as well as the card transition: a sign-in whose own pass only found the lease held is
    /// still awaiting acknowledgement, and the peer's commit is what discharges it.
    /// </returns>
    public async Task<bool> RunAsync(
        RefreshPresentation presentation,
        long peerWaitId,
        long? generationBeforeRefresh,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        try
        {
            while (presentation.Current.PeerWaitId == peerWaitId)
            {
                // The lease may already be near expiry when this process first observes it. Waiting
                // a fresh full horizon would leave the card claiming work is active for almost 30
                // seconds after the peer has gone. A bounded one-second poll keeps disk reads modest
                // while converging promptly after the authoritative lease stops being live.
                await _delay(PollInterval, cancellationToken).ConfigureAwait(false);

                // Display status may temporarily become Loading and return through debounce. The
                // peer-wait identity is the lifecycle authority: exit only when a newer local
                // result or replacement peer wait has superseded this monitor.
                if (presentation.Current.PeerWaitId != peerWaitId)
                {
                    return false;
                }

                if (_isRefreshInProgress())
                {
                    continue;
                }

                // The state-change event is only an accelerant. If it was missed, the monotonic
                // cache generation is still authoritative evidence that the peer committed. Both
                // observations must be known: treating an unreadable generation as zero can turn
                // the same unchanged cache into a false advance once it becomes readable again.
                long? generationAfterRefresh = _readGeneration();
                bool peerCommitted = generationBeforeRefresh is { } before
                    && generationAfterRefresh is { } after
                    && after > before;

                if (presentation.ResolvePeerWait(peerWaitId, peerCommitted))
                {
                    _requestDelivery();
                    return peerCommitted;
                }

                // A newer local result or replacement wait superseded this observation, so it is
                // not this monitor's to report.
                return false;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Provider shutdown. There is no remaining card to converge.
        }

        return false;
    }
}
