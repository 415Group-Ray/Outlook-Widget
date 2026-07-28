using Microsoft.Windows.Widgets.Providers;
using OutlookWidget.Core.Delivery;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Provider.Cards;

namespace OutlookWidget.Provider;

/// <summary>
/// The only code in the product that hands content to the Widgets host.
/// </summary>
/// <remarks>
/// <para>
/// <b>This file is the enforced single call site.</b> Invariant 6 says only the provider may call
/// <c>WidgetManager.UpdateWidget</c>, and a static-analysis test asserts that no other source
/// file in the core or the provider so much as names it. That is not stylistic: with no lease
/// held during delivery, a second caller could put two host calls in flight, and a slow older
/// call landing after a newer logout or privacy commit would leave withheld content on screen.
/// A payload already handed to the host cannot be retracted, so ordering has to be established
/// before the call — which only works if there is exactly one place the call can happen.
/// </para>
/// <para>
/// <b>Called only from the serialized delivery worker, and allowed to block.</b>
/// <c>DeliveryWorker</c> runs one pass at a time on its own thread and re-reads the snapshot,
/// generation, and tombstone immediately before invoking this. <c>UpdateWidget</c> is a
/// synchronous void call into the host with no documented timeout and no cancellation, so a
/// wedged host parks this thread indefinitely. That is tolerable precisely because delivery sits
/// outside the refresh transaction and the lease bound: a wedged host degrades rendering and
/// cannot drag a refresh past the lease horizon.
/// </para>
/// <para>
/// <b>One failing instance must not cost the others.</b> Each instance is updated in its own
/// try block. A host that rejects one widget ID — an instance deleted between the snapshot and
/// the call is the ordinary case — would otherwise abort the pass and leave every later instance
/// stale until something else happened to request delivery.
/// </para>
/// </remarks>
internal sealed class WidgetDeliverySink : IWidgetDeliverySink
{
    private readonly WidgetInstanceRegistry _registry;
    private readonly IOperationalLogger _logger;

    public WidgetDeliverySink(WidgetInstanceRegistry registry, IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(registry);

        _registry = registry;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    public void Deliver(DeliveryState state)
    {
        // A copied snapshot, so a CreateWidget or DeleteWidget callback arriving mid-pass neither
        // crashes this enumeration nor waits on a possibly wedged host call.
        WidgetInstance[] instances = _registry.Snapshot();

        if (instances.Length == 0)
        {
            // Nothing pinned. Not a failure: the provider can outlive its last widget briefly
            // while shutting down, and calling the host with no instances would be pointless.
            return;
        }

        WidgetManager manager = WidgetManager.GetDefault();
        int delivered = 0;

        foreach (WidgetInstance instance in instances)
        {
            var options = new WidgetUpdateRequestOptions(instance.Id)
            {
                Template = SkeletonCard.Template,
                Data = SkeletonCard.Data(instance, state),

                // The generation only. Section 3 permits minimal non-mail CustomState, and this is
                // the smallest thing that is actually useful: it lets a recovered instance tell
                // whether the content it is showing predates the committed snapshot. No sender,
                // subject, message identifier, or message link may ever be placed here —
                // CustomState is host storage outside this process's control.
                CustomState = state.Generation.ToString(System.Globalization.CultureInfo.InvariantCulture),
            };

            try
            {
                manager.UpdateWidget(options);
                delivered++;
            }
            catch (Exception)
            {
                // Most often an instance the user unpinned between the snapshot and this call.
                // Recorded by category and skipped; the next pass reads a registry without it.
                _logger.Record(OperationalEventId.DeliveryFailed, OperationalOutcome.Failed);
            }
        }

        _logger.Record(
            OperationalEventId.DeliveryCompleted,
            delivered == instances.Length ? OperationalOutcome.Success : OperationalOutcome.Failed,
            recordCount: delivered);
    }
}
