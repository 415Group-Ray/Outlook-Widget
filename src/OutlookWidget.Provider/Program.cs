using System.Runtime.InteropServices;
using Microsoft.Identity.Client;
using Microsoft.Windows.Widgets.Providers;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Launching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Packaging;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Provider;

/// <summary>
/// The provider process: composition, COM registration, and lifetime.
/// </summary>
/// <remarks>
/// <para>
/// <b>Lifetime is owned here and nowhere else.</b> The Widgets host starts this process by COM
/// activation when it needs the provider, and expects it to exit once the last enabled widget is
/// deleted. That gives the process a single shape: compose, recover instances, register the class
/// object, wait for the empty-registry signal, revoke, dispose. There is no message loop, no
/// watchdog, and no background service — invariant 3 forbids relying on process lifetime for
/// coordination correctness, and cross-process single-flight uses the expiring lease record
/// instead.
/// </para>
/// <para>
/// <b>Order matters twice.</b> Instances are recovered <em>before</em> the class object is
/// registered, so the host cannot deliver a callback against an empty registry. And registration
/// is revoked <em>before</em> the coordination stack is disposed, so no callback can arrive
/// against a disposed delivery worker.
/// </para>
/// <para>
/// <b>MTA, by having no <c>[STAThread]</c>.</b> An out-of-process WinRT server should not put its
/// activation on a single-threaded apartment: the host calls the provider's callbacks on its own
/// threads, and an STA would funnel them through a message pump this process does not run,
/// deadlocking the first call.
/// </para>
/// </remarks>
internal static partial class Program
{
    /// <summary>
    /// The CLSID the Widgets host activates.
    /// </summary>
    /// <remarks>
    /// This exact GUID also appears twice in the package manifest — as the <c>com:Class</c>
    /// <c>Id</c> and as the widget extension's <c>CreateInstance ClassId</c>. All three must
    /// match: a mismatch produces a widget that appears in the picker and then fails to activate,
    /// with no error surfaced anywhere obvious. A test asserts the three agree, because nothing
    /// else would catch it before a manual pin attempt.
    /// </remarks>
    internal static readonly Guid ProviderClassId = new("254395D8-5EAC-4A2D-9971-90C99BFFD192");

    /// <summary>CLSCTX_LOCAL_SERVER: this process serves the class out of process.</summary>
    private const uint ClsCtxLocalServer = 0x4;

    /// <summary>
    /// REGCLS_MULTIPLEUSE: one registration serves every activation request, so the host reuses
    /// this process rather than starting another for each widget.
    /// </summary>
    private const uint RegClsMultipleUse = 0x1;

    /// <summary>The identity query failed for a reason other than the process being unpackaged.</summary>
    private const int ExitIdentityQueryFailed = 1;

    /// <summary>
    /// The process has no package identity. Distinct exit codes because the two diagnose
    /// differently: one is a broken query, the other means this executable was started directly
    /// rather than activated from an installed package.
    /// </summary>
    private const int ExitUnpackaged = 2;

    private static int Main()
    {
        // Package identity places all coordination state, and anything other than a resolved
        // identity is fatal on purpose. Falling back to the unpackaged path would write cached
        // mailbox data outside the package store, where uninstall cannot remove it, silently
        // breaking the privacy claim that uninstall removes cached mail.
        //
        // Both failure paths run through PackagedState rather than being composed here. An earlier
        // version of this method treated only the thrown case as fatal and passed a null family
        // name straight into CoordinationPaths.Resolve, which answers with the ordinary per-user
        // path — then created those directories and carried on, directly under this comment
        // explaining why that must not happen. The reasoning was right; the null case was simply
        // not covered.
        PackagedStateResult state = PackagedState.Locate();

        if (!state.IsResolved)
        {
            // Nothing has been created at this point, so a refusing provider leaves no trace on
            // disk. The Widgets host retries activation; a provider that ran with the wrong state
            // location would look healthy and be wrong.
            return state.Status == PackagedStateStatus.Unpackaged
                ? ExitUnpackaged
                : ExitIdentityQueryFailed;
        }

        string? packageFamilyName = state.PackageFamilyName;
        CoordinationPaths paths = state.Paths!;
        paths.EnsureCreated();

        IOperationalLogger logger = NullOperationalLogger.Instance;

        // Read once at startup rather than on the delivery path, and deliberately NOT fatal. A
        // package shipped without configuration cannot authenticate, which is a card the user
        // should see rather than a provider process that dies in the background where the Widgets
        // host started it.
        AuthenticationConfigurationResult configuration = AuthenticationConfiguration.Load(logger);
        SkeletonCard.ConfigurationStatus = configuration.Status;

        var cache = new ProtectedCache(paths, logger);
        var tombstones = new DisclosureTombstoneStore(paths, logger);
        var registry = new WidgetInstanceRegistry();

        // The sole UpdateWidget call site, reached only through the serialized worker below. It is
        // given the tombstone read rather than the store, so it can re-check disclosure before each
        // host call without acquiring the ability to write or clear one.
        var sink = new WidgetDeliverySink(registry, tombstones.GetEffectiveMode, logger);

        using var delivery = new DeliveryWorker(cache, tombstones, sink, logger);

        // The cross-process half of gate 11: the companion commits state and signals, and this is
        // what turns that signal into a delivery pass. Until this listener existed nothing created
        // the named events at all, so every signal the companion sent was discarded.
        using var listener = new StateChangeListener(paths, delivery.RequestDelivery, logger);

        using var lastWidgetDeleted = new ManualResetEventSlim(initialState: false);

        var provider = new WidgetProvider(
            registry,
            delivery,
            new OutlookLauncher(logger),
            new CompanionLauncher(packageFamilyName, logger),
            lastWidgetDeleted,
            logger);

        // Before registration, so no callback can arrive against an empty registry.
        int recovered = provider.RecoverEnabledInstances();

        // A single shared provider instance rather than one per activation. The registry, the
        // delivery worker, and the last-widget signal are process-wide by design, and handing out
        // several providers over the same state would give each an incomplete view of it.
        var factory = new ProviderFactory(() => provider);

        // Marshalled to a raw IUnknown by hand rather than declared as an object parameter.
        // LibraryImport generates only blittable signatures and does not support
        // UnmanagedType.IUnknown at all, and the classic DllImport that would accept it is the
        // legacy path the analyzers reject under warnings-as-errors. This keeps the P/Invoke
        // blittable and makes the reference count explicit.
        IntPtr factoryUnknown = Marshal.GetIUnknownForObject(factory);

        // The revoke result becomes this process's exit code. A failed revoke is not something the
        // provider can act on — it is already shutting down — but reporting it beats discarding it,
        // because a host that believes the class object is still registered will try to activate a
        // process that no longer exists, and the exit code is the only place that is visible.
        int revoke = 0;

        // Bounds the silent-authentication probe below. Cancelled before revoke, so a broker call that
        // never returns cannot keep this process alive after its last widget was removed.
        using var authProbeCancellation = new CancellationTokenSource();
        Task authProbe = Task.CompletedTask;

        try
        {
            int registration = CoRegisterClassObject(
                ProviderClassId,
                factoryUnknown,
                ClsCtxLocalServer,
                RegClsMultipleUse,
                out uint cookie);

            if (registration != 0)
            {
                // Nothing can reach the provider, so there is no degraded mode to offer. Exiting
                // lets the host retry activation rather than leaving a process that looks alive and
                // serves nothing.
                return registration;
            }

            try
            {
                if (recovered > 0)
                {
                    // Recovered instances are still showing whatever the host cached from the
                    // previous process. Requesting a pass now is what makes a reboot or package
                    // update re-render from committed state instead of leaving stale content on
                    // screen.
                    delivery.RequestDelivery();
                }

                // Started AFTER the class object is registered, and deliberately not awaited.
                //
                // Building the MSAL client opens the shared token cache and a silent acquisition can
                // reach the network to refresh a token, so doing either before CoRegisterClassObject
                // would add that latency to every cold activation — against the nonfunctional
                // activation targets — for a result no callback needs in order to render.
                authProbe = ProbeSilentAuthenticationAsync(
                    configuration,
                    paths,
                    delivery,
                    logger,
                    authProbeCancellation.Token);

                // Blocks until DeleteWidget removes the last enabled instance. No timeout: a
                // provider that exited on its own schedule would stop updating widgets that are
                // still pinned, and the host would have to reactivate it to recover.
                lastWidgetDeleted.Wait();
            }
            finally
            {
                // Cancelled and drained before the delivery worker is disposed, so the probe cannot
                // request a pass against a disposed worker. Bounded, per invariant 2: the async
                // deadline is the same ceiling every other cross-process wait uses, and a probe still
                // running after it is abandoned rather than allowed to hold the process open.
                authProbeCancellation.Cancel();
                authProbe.Wait(CoordinationBounds.AsyncDeadline);

                // Revoked before the using-declared stack unwinds, so no callback can arrive
                // against a disposed delivery worker or listener.
                revoke = CoRevokeClassObject(cookie);
            }
        }
        finally
        {
            Marshal.Release(factoryUnknown);
        }

        return revoke;
    }

    /// <summary>
    /// Attempts one silent token acquisition and publishes the classified outcome to the card.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>This is the whole of gate 9, and it is silent-only by construction.</b>
    /// <see cref="BrokerClient.NoParentWindow"/> is what makes the zero handle explicit and searchable:
    /// this process owns no window and runs no message loop, so there is nothing else it could
    /// truthfully pass. The service it builds exposes only silent acquisition, so no code path from
    /// here can open a browser or a dialog — and a source-level test fails the build if the interactive
    /// API ever appears in this project.
    /// </para>
    /// <para>
    /// <b>It never throws.</b> Every failure becomes a status on the card. This runs unobserved in a
    /// background COM server; an escaping exception would take down a provider the user did not start
    /// and leave the host showing stale content with nothing to explain it.
    /// </para>
    /// </remarks>
    private static async Task ProbeSilentAuthenticationAsync(
        AuthenticationConfigurationResult configuration,
        CoordinationPaths paths,
        DeliveryWorker delivery,
        IOperationalLogger logger,
        CancellationToken cancellationToken)
    {
        TokenAcquisitionStatus status;

        try
        {
            if (!configuration.IsLoaded)
            {
                status = TokenAcquisitionStatus.NoConfiguration;
            }
            else
            {
                IPublicClientApplication client = await BrokerClient
                    .CreateAsync(configuration.Options!, paths, BrokerClient.NoParentWindow)
                    .ConfigureAwait(false);

                var silent = new SilentAuthService(client, logger);

                status = (await silent.AcquireAsync(cancellationToken).ConfigureAwait(false)).Status;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException and not StackOverflowException)
        {
            // Includes failures from building the client itself — a corrupt token cache, or a broker
            // whose native runtime will not initialise — which happen before there is any acquisition
            // to classify. The classifier handles what it recognises and everything else lands as a
            // generic failure, which is the correct card either way.
            status = AuthenticationFailures.Classify(e, AuthenticationPhase.Silent);
        }

        SkeletonCard.SilentAuthStatus = status;

        // The card changed, so ask for a pass. Guarded because the last widget may have been unpinned
        // while this was in flight, in which case the worker is being torn down and there is nothing
        // left to deliver to.
        try
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                delivery.RequestDelivery();
            }
        }
        catch (ObjectDisposedException)
        {
            // The provider is shutting down. Nothing to report and nowhere to report it.
        }
    }

    [LibraryImport("ole32.dll")]
    private static partial int CoRegisterClassObject(
        in Guid rclsid,
        IntPtr pUnk,
        uint dwClsContext,
        uint flags,
        out uint lpdwRegister);

    [LibraryImport("ole32.dll")]
    private static partial int CoRevokeClassObject(uint dwRegister);
}
