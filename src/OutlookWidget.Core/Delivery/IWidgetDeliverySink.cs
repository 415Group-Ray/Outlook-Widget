using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Delivery;

/// <summary>
/// The state one delivery pass will render, read fresh at the start of that pass.
/// </summary>
/// <param name="Generation">The committed generation this content came from.</param>
/// <param name="Mode">
/// The effective disclosure mode at the moment the pass began. Re-read per pass, so every
/// pass that has not yet entered the host call honours the current tombstone.
/// </param>
/// <param name="ReadStatus">Why the state read ended as it did.</param>
/// <param name="Payload">
/// The unprotected state, or <see langword="null"/> when absent, cleared, or unusable — in
/// which case the sink renders a signed-out, stale, or error card rather than nothing.
/// </param>
public readonly record struct DeliveryState(
    long Generation,
    DisclosureMode Mode,
    CacheReadStatus ReadStatus,
    byte[]? Payload);

/// <summary>
/// Hands rendered content to the widget host.
/// </summary>
/// <remarks>
/// <para>
/// Implemented only in the provider process, and called only from
/// <see cref="DeliveryWorker"/>'s single serialized worker. The companion commits state and
/// signals; it never delivers. That is both a correctness rule — ordering must be
/// established before the host call, because a payload already handed to
/// <c>UpdateWidget</c> cannot be retracted — and the natural division, since the provider
/// is the only process holding widget IDs and per-instance contexts.
/// </para>
/// <para>
/// <b>Implementations may block indefinitely and that is expected.</b>
/// <c>WidgetManager.UpdateWidget</c> is a synchronous void call into the Widgets host with
/// no documented timeout and no cancellation. Delivery is therefore deliberately outside
/// the refresh transaction: the lease is already clear before this is called, so a wedged
/// host degrades rendering only and cannot drag the operation past the lease horizon.
/// </para>
/// </remarks>
public interface IWidgetDeliverySink
{
    /// <summary>
    /// Renders and delivers <paramref name="state"/> to every enabled widget instance.
    /// </summary>
    void Deliver(DeliveryState state);
}
