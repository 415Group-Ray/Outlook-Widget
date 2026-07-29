using Microsoft.Identity.Client;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Provider;

/// <summary>
/// Owns the provider's silent token acquisition, including re-acquiring after the companion signs in.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not a single call at startup, which is what it was.</b> The probe used to run exactly
/// once per provider process. That is correct for the cold-start case and wrong for the most ordinary
/// flow the product has: a pinned widget renders "sign in required", the user clicks its action, the
/// companion opens, they sign in — and the provider is still the same process, holding the same
/// <c>InteractionRequired</c> result, with nothing scheduled to look again. The card would say sign in
/// forever while a valid token sat in the broker. Because <c>Main</c> keeps the process alive until the
/// last widget is unpinned, the only escapes were unpinning the widget or killing the provider, and
/// unpinning discards the pin the whole force-shutdown upgrade path exists to preserve.
/// </para>
/// <para>
/// <b>Convergence, not polling.</b> There is no timer and no background service — invariant 3 forbids
/// leaning on process lifetime for coordination. The companion raises the existing state-changed event
/// after a successful sign-in and the provider's <c>StateChangeListener</c> turns that into
/// <see cref="RequestProbe"/>. If nothing is listening the companion's signal is a no-op, which is
/// correct: a provider that is not running will probe when it next starts.
/// </para>
/// <para>
/// <b>Re-probes in both directions, deliberately.</b> An earlier draft re-probed only while the status
/// was not <see cref="TokenAcquisitionStatus.Acquired"/>, which converges upward and never downward — a
/// sign-out would have left <c>Acquired</c> on the card. The disclosure tombstone already forces the
/// signed-out card in that case, so nothing was disclosed, but a status that can only improve is a trap
/// for whoever adds the next state. Every signal re-probes; the overlap guard is what bounds the cost.
/// </para>
/// </remarks>
internal sealed class SilentAuthProbe : IDisposable
{
    private readonly AuthenticationConfigurationResult _configuration;
    private readonly CoordinationPaths _paths;
    private readonly DeliveryWorker _delivery;
    private readonly IOperationalLogger _logger;
    private readonly CancellationTokenSource _shutdown = new();

    /// <summary>
    /// Guards against overlapping probes. Set to 1 while one is in flight.
    /// </summary>
    /// <remarks>
    /// A dropped concurrent request is not a lost update: state on disk is authoritative, and the probe
    /// in flight reads the broker after the request that was dropped was made. The one case that could
    /// matter — a sign-in completing microseconds after a probe read the cache — is covered because the
    /// companion signals after the cache is written, so the next signal produces the next probe.
    /// </remarks>
    private int _running;

    /// <summary>The MSAL client, built once and reused across probes.</summary>
    private IPublicClientApplication? _client;

    private bool _disposed;

    public SilentAuthProbe(
        AuthenticationConfigurationResult configuration,
        CoordinationPaths paths,
        DeliveryWorker delivery,
        IOperationalLogger logger)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(logger);

        _configuration = configuration;
        _paths = paths;
        _delivery = delivery;
        _logger = logger;
    }

    /// <summary>How many probes have completed. For diagnostics and source-level assertions.</summary>
    public long Completed { get; private set; }

    /// <summary>
    /// Requests a probe and returns immediately.
    /// </summary>
    /// <remarks>
    /// Must return promptly and must not throw: this is called from
    /// <c>StateChangeListener</c>'s single worker thread, where blocking would delay every subsequent
    /// notification and throwing would be swallowed as a dropped notification.
    /// </remarks>
    public void RequestProbe()
    {
        if (_disposed || _shutdown.IsCancellationRequested)
        {
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) == 1)
        {
            return;
        }

        _ = Task.Run(RunAsync);
    }

    /// <summary>
    /// Runs one probe to completion, publishes the status, and asks for a delivery pass.
    /// </summary>
    /// <remarks>
    /// Never throws. This runs unobserved on a thread-pool thread in a background COM server; an
    /// escaping exception would take down a provider the user did not start and leave the host showing
    /// stale content with nothing to explain it.
    /// </remarks>
    private async Task RunAsync()
    {
        try
        {
            TokenAcquisitionStatus status = await AcquireAsync().ConfigureAwait(false);

            SkeletonCard.SilentAuthStatus = status;
            Completed++;

            // The card changed, so ask for a pass. Guarded because the last widget may have been
            // unpinned while this was in flight, in which case the worker is being torn down and there
            // is nothing left to deliver to.
            if (!_shutdown.IsCancellationRequested)
            {
                _delivery.RequestDelivery();
            }
        }
        catch (ObjectDisposedException)
        {
            // The provider is shutting down. Nothing to report and nowhere to report it.
        }
        finally
        {
            Volatile.Write(ref _running, 0);
        }
    }

    private async Task<TokenAcquisitionStatus> AcquireAsync()
    {
        try
        {
            if (!_configuration.IsLoaded)
            {
                return TokenAcquisitionStatus.NoConfiguration;
            }

            // BrokerClient.NoParentWindow is the whole of gate 9: this process owns no window and runs
            // no message loop, so there is nothing else it could truthfully pass. The service it builds
            // exposes only silent acquisition, so no path from here can open a browser or a dialog.
            _client ??= await BrokerClient
                .CreateAsync(_configuration.Options!, _paths, BrokerClient.NoParentWindow)
                .ConfigureAwait(false);

            var silent = new SilentAuthService(_client, _logger);

            return (await silent.AcquireAsync(_shutdown.Token).ConfigureAwait(false)).Status;
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // Includes failures from building the client itself — a corrupt token cache, or a broker
            // whose native runtime will not initialise — which happen before there is any acquisition
            // to classify. The classifier handles what it recognises and everything else lands as a
            // generic failure, which is the correct card either way.
            return AuthenticationFailures.Classify(e, AuthenticationPhase.Silent);
        }
    }

    /// <summary>
    /// Cancels any probe in flight and waits a bounded time for it to finish.
    /// </summary>
    /// <remarks>
    /// Bounded per invariant 2. Called before the delivery worker is disposed, so a probe cannot request
    /// a pass against a disposed worker; a probe still running after the deadline is abandoned rather
    /// than allowed to hold the process open after its last widget was removed.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _shutdown.Cancel();

        SpinWait.SpinUntil(
            () => Volatile.Read(ref _running) == 0,
            Core.Refresh.CoordinationBounds.AsyncDeadline);

        _shutdown.Dispose();
    }
}
