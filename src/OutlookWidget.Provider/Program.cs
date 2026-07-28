using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Launching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Packaging;

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

    private static int Main()
    {
        // Package identity places all coordination state. A failure to determine it is fatal on
        // purpose: falling back to the unpackaged path would write cached mailbox data outside the
        // package store, where uninstall cannot remove it, silently breaking the privacy claim
        // that uninstall removes cached mail.
        string? packageFamilyName;

        try
        {
            packageFamilyName = PackageIdentity.TryGetFamilyName();
        }
        catch (PackageIdentityException)
        {
            return 1;
        }

        CoordinationPaths paths = CoordinationPaths.Resolve(packageFamilyName);
        paths.EnsureCreated();

        IOperationalLogger logger = NullOperationalLogger.Instance;

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

                // Blocks until DeleteWidget removes the last enabled instance. No timeout: a
                // provider that exited on its own schedule would stop updating widgets that are
                // still pinned, and the host would have to reactivate it to recover.
                lastWidgetDeleted.Wait();
            }
            finally
            {
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
