using Microsoft.Windows.Widgets;
using Microsoft.Windows.Widgets.Providers;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Launching;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Provider;

/// <summary>
/// The Windows Widgets provider: the complete <see cref="IWidgetProvider"/> contract over the
/// coordination subsystem.
/// </summary>
/// <remarks>
/// <para>
/// <b>Callbacks record state and request delivery; they never render.</b> Every callback here
/// updates the instance registry and sets the delivery worker's pending marker. None of them
/// builds a card or calls the host, because content must be chosen at delivery time from freshly
/// read state rather than captured when the request was made — that is what stops a slow older
/// update from landing after a newer logout or privacy commit. It also keeps the callbacks fast,
/// which matters because the host's <c>Activate</c>/<c>Deactivate</c> window can be very short.
/// </para>
/// <para>
/// <b>Nothing the host passes in is retained.</b> The Widgets host documents that objects handed
/// to these callbacks are guaranteed valid only for the duration of the call. Every field is
/// therefore copied out immediately into <see cref="WidgetInstance"/>, and no
/// <c>WidgetContext</c> or args object outlives its callback.
/// </para>
/// <para>
/// <b>Per instance, never global.</b> Size and active state are properties of one pinned widget.
/// A provider that tracked either process-wide would render two instances at whichever size was
/// last reported, which is exactly the failure gate 5 tests for.
/// </para>
/// <para>
/// <b>Phase 0 scope.</b> There is no authentication, no Graph call, and no refresh here yet: a
/// refresh action requests a delivery pass over current cached state rather than fetching mail.
/// The lifecycle, the registration, the single delivery call site, and the cross-process
/// invalidation path are what this establishes.
/// </para>
/// </remarks>
internal sealed class WidgetProvider : IWidgetProvider
{
    private readonly WidgetInstanceRegistry _registry;
    private readonly DeliveryWorker _delivery;
    private readonly OutlookLauncher _outlook;
    private readonly CompanionLauncher _companion;
    private readonly IOperationalLogger _logger;
    private readonly ManualResetEventSlim _lastWidgetDeleted;

    public WidgetProvider(
        WidgetInstanceRegistry registry,
        DeliveryWorker delivery,
        OutlookLauncher outlook,
        CompanionLauncher companion,
        ManualResetEventSlim lastWidgetDeleted,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(outlook);
        ArgumentNullException.ThrowIfNull(companion);
        ArgumentNullException.ThrowIfNull(lastWidgetDeleted);

        _registry = registry;
        _delivery = delivery;
        _outlook = outlook;
        _companion = companion;
        _lastWidgetDeleted = lastWidgetDeleted;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>
    /// Rebuilds the instance map from the host's own record of enabled widgets.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Called once during process startup, before the class object is registered, so no callback
    /// can arrive against an empty registry. This is the recovery path after a reboot, a package
    /// update, or a provider crash: the host does not replay <c>CreateWidget</c> for widgets that
    /// were already pinned, so a provider that started empty would render nothing until the user
    /// happened to resize or reactivate each one.
    /// </para>
    /// <para>
    /// A failure here is survivable and must not prevent registration. If <c>GetWidgetInfos</c>
    /// throws, the provider starts with an empty registry and recovers as the host activates
    /// instances; refusing to start would turn a transient host problem into a widget that is
    /// permanently blank.
    /// </para>
    /// </remarks>
    public int RecoverEnabledInstances()
    {
        try
        {
            WidgetInfo[] infos = WidgetManager.GetDefault().GetWidgetInfos();

            foreach (WidgetInfo info in infos)
            {
                WidgetContext context = info.WidgetContext;

                _registry.AddOrUpdate(new WidgetInstance(
                    Id: context.Id,
                    DefinitionId: context.DefinitionId,
                    Size: context.Size,

                    // The host reports which widgets exist, not which are currently being viewed.
                    // Starting every recovered instance inactive is the honest default; Activate
                    // corrects it for any the user is actually looking at.
                    IsActive: false,

                    // The generation the host was last given for this instance, completing the
                    // round trip the sink starts when it writes CustomState. Gate 4 requires
                    // CustomState to be restored, not merely written, and without this the card
                    // currently on screen after a recovery could not be compared against the
                    // committed snapshot at all.
                    DeliveredGeneration: WidgetInstance.ParseGeneration(info.CustomState)));
            }

            return infos.Length;
        }
        catch (Exception)
        {
            _logger.Record(OperationalEventId.DeliveryFailed, OperationalOutcome.Recovered);
            return 0;
        }
    }

    /// <summary>
    /// The user pinned a widget. Registers the instance and renders cached content immediately.
    /// </summary>
    public void CreateWidget(WidgetContext widgetContext)
    {
        _registry.AddOrUpdate(new WidgetInstance(
            Id: widgetContext.Id,
            DefinitionId: widgetContext.DefinitionId,
            Size: widgetContext.Size,

            // A freshly pinned widget is being looked at, but the host still sends Activate. Not
            // assuming it here keeps active state driven by exactly one source.
            IsActive: false));

        // Cached-first: request a pass now rather than waiting for a refresh, so a newly pinned
        // widget shows committed state instead of an empty frame.
        _delivery.RequestDelivery();
    }

    /// <summary>
    /// The user unpinned a widget. Signals process exit when it was the last one.
    /// </summary>
    /// <remarks>
    /// The <c>customState</c> the host returns is deliberately ignored. It is the state this
    /// provider wrote, it is being discarded along with the instance, and reading it back on the
    /// way out could only invite treating host-stored data as authoritative.
    /// </remarks>
    public void DeleteWidget(string widgetId, string customState)
    {
        if (_registry.RemoveAndReportEmpty(widgetId))
        {
            // Only on the transition to empty, and only for an ID that was actually present. A
            // duplicate or unmatched DeleteWidget must not signal exit while widgets are still
            // pinned.
            _lastWidgetDeleted.Set();
        }
    }

    /// <summary>
    /// The host is interested in updates for this instance. Marks only this one active.
    /// </summary>
    public void Activate(WidgetContext widgetContext)
    {
        string widgetId = widgetContext.Id;
        WidgetSize size = widgetContext.Size;
        string definitionId = widgetContext.DefinitionId;

        // Activate can be the first callback for an instance the host knows about and this
        // provider has not recovered — after a crash, or when recovery failed. Adding it rather
        // than only updating is what keeps that instance from staying blank.
        if (!_registry.TryUpdate(widgetId, existing => existing with { IsActive = true, Size = size }))
        {
            _registry.AddOrUpdate(new WidgetInstance(widgetId, definitionId, size, IsActive: true));
        }

        _delivery.RequestDelivery();
    }

    /// <summary>
    /// The host is no longer requesting updates for this instance. Marks only this one inactive.
    /// </summary>
    /// <remarks>
    /// No delivery request: nothing about the content changed, and updating a widget the host just
    /// said it is not watching would spend a host call to no effect.
    /// </remarks>
    public void Deactivate(string widgetId) =>
        _registry.TryUpdate(widgetId, existing => existing with { IsActive = false });

    /// <summary>
    /// The user invoked an action. Validates the instance and the verb before dispatching.
    /// </summary>
    /// <remarks>
    /// Both checks matter. The verb arrives as a string that originated in card JSON, and the
    /// widget ID identifies which instance it came from; an action for an unknown instance is
    /// either a stale card or something this provider did not create, and either way there is
    /// nothing legitimate to do with it.
    /// </remarks>
    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        string verb = actionInvokedArgs.Verb;
        string widgetId = actionInvokedArgs.WidgetContext.Id;

        if (!_registry.Contains(widgetId))
        {
            return;
        }

        switch (verb)
        {
            case WidgetVerbs.Refresh:
                // Phase 0: a pass over currently committed state, not a Graph fetch. The refresh
                // transaction, its debounce, and its lease arrive with authentication in slice 2.
                _logger.Record(OperationalEventId.RefreshRequested, OperationalOutcome.Success);
                _delivery.RequestDelivery();
                break;

            case WidgetVerbs.OpenOutlook:
                _outlook.Launch();
                break;

            case WidgetVerbs.OpenCompanion:
                _companion.Launch();
                break;

            default:
                // An unrecognised verb is ignored rather than dispatched. There is no reflection,
                // no path construction, and no process argument built from this string.
                break;
        }
    }

    /// <summary>
    /// The instance's size changed. Records the new size and re-renders that instance.
    /// </summary>
    /// <remarks>
    /// The size is read from this instance's own <c>WidgetContext</c>. The current release calls
    /// this only for a resize, but the size is still read rather than inferred, so a future
    /// context change cannot leave the registry describing the wrong layout.
    /// </remarks>
    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        WidgetContext context = contextChangedArgs.WidgetContext;
        string widgetId = context.Id;
        WidgetSize size = context.Size;

        if (_registry.TryUpdate(widgetId, existing => existing with { Size = size }))
        {
            _delivery.RequestDelivery();
        }
    }
}
