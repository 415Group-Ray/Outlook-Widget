using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Graph;
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

        // Declared before the listener so the listener's callback can reach it, and disposed after it
        // for the same reason in reverse: the probe must outlive the thing that can ask for one.
        using var authProbe = new SilentAuthProbe(configuration, paths, delivery, logger);

        using var mutation = new MutationMutex(paths.MutationMutexName, logger);
        var leases = new RefreshLeaseStore(paths, mutation, logger: logger);
        var commits = new StateCommitCoordinator(paths, mutation, logger);
        using var graph = new GraphMailClient(logger);

        ProviderRefreshWorker? refresh = null;

        if (configuration is { IsLoaded: true, Options: { } options })
        {
            var selectedAccounts = new SelectedAccountStore(paths, options, logger);
            var coordinator = new RefreshCoordinator(
                cache,
                leases,
                commits,
                delivery,
                logger: logger,
                selectedAccounts: selectedAccounts);

            var fetcher = new MailboxRefreshFetcher(
                async cancellationToken =>
                {
                    TokenAcquisitionResult token =
                        await authProbe.AcquireTokenAsync(cancellationToken).ConfigureAwait(false);

                    return token.IsAcquired && token.HomeAccountId is { Length: > 0 } homeAccountId
                        ? new MailboxRefreshAccess(token.AccessToken!, homeAccountId)
                        : null;
                },
                graph.ReadAsync,
                options.TenantId,
                includeFocusedCount: true);

            refresh = new ProviderRefreshWorker(
                coordinator,
                fetcher,
                cache,
                selectedAccounts,
                delivery,
                logger);
        }

        using var refreshLifetime = refresh;

        // The cross-process half of gate 11: the companion commits state and signals, and this is
        // what turns that signal into a delivery pass. Until this listener existed nothing created
        // the named events at all, so every signal the companion sent was discarded.
        //
        // The signal now also re-probes authentication, which is what makes the ordinary sign-in flow
        // converge. A pinned widget rendering "sign in required" launches the companion; the user signs
        // in; the companion signals; this re-probes and delivers. Without it the provider — still the
        // same process, since it lives until the last widget is unpinned — would hold its original
        // result forever with a valid token sitting in the broker.
        //
        // Ordering: the probe is requested first and returns immediately, then delivery is requested.
        // The probe asks for its own pass on completion, so the immediate one renders whatever changed
        // in committed state without waiting on a token acquisition that may reach the network.
        using var listener = new StateChangeListener(
            paths,
            () =>
            {
                authProbe.RequestProbe();
                delivery.RequestDelivery();
                // Account-aware staleness makes a new interactive selection refresh immediately
                // without turning every ordinary cache-commit signal into an infinite refresh loop.
                refresh?.RequestIfStale(RefreshTrigger.SignIn);
            },
            logger);

        using var lastWidgetDeleted = new ManualResetEventSlim(initialState: false);

        var provider = new WidgetProvider(
            registry,
            delivery,
            refresh,
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

                // Requested AFTER the class object is registered, and deliberately not awaited.
                //
                // Building the MSAL client opens the shared token cache and a silent acquisition can
                // reach the network to refresh a token, so doing either before CoRegisterClassObject
                // would add that latency to every cold activation — against the nonfunctional
                // activation targets — for a result no callback needs in order to render.
                authProbe.RequestProbe();

                // Blocks until DeleteWidget removes the last enabled instance. No timeout: a
                // provider that exited on its own schedule would stop updating widgets that are
                // still pinned, and the host would have to reactivate it to recover.
                lastWidgetDeleted.Wait();
            }
            finally
            {
                // Revoked before the using-declared stack unwinds, so no callback can arrive
                // against a disposed delivery worker or listener. The probe is drained by its own
                // bounded Dispose as that stack unwinds, which runs before the delivery worker's.
                revoke = CoRevokeClassObject(cookie);
            }
        }
        finally
        {
            Marshal.Release(factoryUnknown);
        }

        return revoke;
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
