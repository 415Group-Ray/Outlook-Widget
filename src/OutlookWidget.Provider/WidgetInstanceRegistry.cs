using Microsoft.Windows.Widgets;

namespace OutlookWidget.Provider;

/// <summary>
/// One pinned widget instance, as far as this provider needs to remember it.
/// </summary>
/// <param name="Id">The host's widget ID. The only durable handle to the instance.</param>
/// <param name="DefinitionId">
/// Which of the package's widget definitions this instance is. Chooses the card, so it is read
/// per instance and never treated as a process-wide value.
/// </param>
/// <param name="Size">The instance's current size. Per instance, never global.</param>
/// <param name="IsActive">
/// Whether the host has said it is currently interested in updates for this instance. Per
/// instance: one widget being visible says nothing about another.
/// </param>
/// <param name="DeliveredGeneration">
/// The snapshot generation of the content the host was last given for this instance, or
/// <see langword="null"/> when nothing has been delivered yet or the host returned a value this
/// provider cannot parse.
/// </param>
/// <remarks>
/// <para>
/// <paramref name="DeliveredGeneration"/> is the round trip through the host's own storage. The
/// sink writes the generation into <c>CustomState</c> after a successful update and
/// <c>GetWidgetInfos()</c> hands it back on recovery, which is what lets a recovered provider say
/// whether the card currently on screen predates the committed snapshot. Restoring it is part of
/// the gate 4 acceptance criterion; writing it without ever reading it back — the original
/// omission — satisfied neither the criterion nor any purpose.
/// </para>
/// <para>
/// Parsed defensively and never trusted. <c>CustomState</c> is host storage outside this process's
/// control, so an absent, empty, or unparseable value is treated as "unknown" rather than as an
/// error or a zero. Zero would be a lie, because generation zero is a real generation.
/// </para>
/// </remarks>
/// <remarks>
/// A record of value copies rather than a reference to anything the host handed over. The
/// Widgets host documents that objects passed into <c>IWidgetProvider</c> callbacks are
/// guaranteed valid only for the duration of that callback, with undefined behaviour outside it,
/// so every field here is extracted inside the callback and the host's object is never retained.
/// </remarks>
internal readonly record struct WidgetInstance(
    string Id,
    string DefinitionId,
    WidgetSize Size,
    bool IsActive,
    long? DeliveredGeneration = null)
{
    /// <summary>
    /// Parses a generation out of a host-supplied <c>CustomState</c> string.
    /// </summary>
    /// <remarks>
    /// Returns <see langword="null"/> for absent, empty, or unparseable input rather than throwing
    /// or defaulting to zero. This value comes back from host storage, so it is input rather than
    /// something this process can rely on, and zero would be indistinguishable from a genuine
    /// generation zero.
    /// </remarks>
    public static long? ParseGeneration(string? customState) =>
        long.TryParse(
            customState,
            System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture,
            out long generation)
            ? generation
            : null;
}

/// <summary>
/// The provider's in-memory map of enabled widget instances.
/// </summary>
/// <remarks>
/// <para>
/// <b>Locked, because two threads genuinely reach it.</b> The host calls the
/// <c>IWidgetProvider</c> callbacks on its own threads while the serialized delivery worker
/// enumerates instances on the delivery thread. <see cref="Snapshot"/> therefore returns a
/// copied array rather than a live view: enumerating shared mutable state while a callback adds
/// or removes an instance is the collection-modified crash this exists to prevent, and holding
/// the lock for the duration of a delivery pass would let a wedged Widgets host block
/// <c>DeleteWidget</c> and so block provider shutdown.
/// </para>
/// <para>
/// <b>Empty is a signal, not just a state.</b> Provider process lifetime is tied to having at
/// least one enabled instance, so the transition to empty is reported explicitly rather than
/// left for a caller to notice by polling <see cref="Count"/>.
/// </para>
/// </remarks>
internal sealed class WidgetInstanceRegistry
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, WidgetInstance> _instances = new(StringComparer.Ordinal);

    /// <summary>How many instances are enabled.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _instances.Count;
            }
        }
    }

    /// <summary>
    /// Adds or replaces an instance. Replacement rather than rejection on a duplicate ID, because
    /// <c>GetWidgetInfos</c> recovery and a <c>CreateWidget</c> callback can legitimately describe
    /// the same instance and the callback's view is the newer one.
    /// </summary>
    public void AddOrUpdate(WidgetInstance instance)
    {
        ArgumentException.ThrowIfNullOrEmpty(instance.Id);

        lock (_gate)
        {
            _instances[instance.Id] = instance;
        }
    }

    /// <summary>
    /// Removes an instance and reports whether that emptied the registry.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when this call removed the last enabled instance. False both when
    /// instances remain and when the ID was unknown — an unknown ID must not report emptiness,
    /// because a duplicate or unmatched <c>DeleteWidget</c> would otherwise trigger process exit
    /// while widgets are still pinned.
    /// </returns>
    public bool RemoveAndReportEmpty(string widgetId)
    {
        ArgumentException.ThrowIfNullOrEmpty(widgetId);

        lock (_gate)
        {
            return _instances.Remove(widgetId) && _instances.Count == 0;
        }
    }

    /// <summary>
    /// Applies a change to one instance, if it is known.
    /// </summary>
    /// <returns>Whether the instance was found and updated.</returns>
    public bool TryUpdate(string widgetId, Func<WidgetInstance, WidgetInstance> change)
    {
        ArgumentException.ThrowIfNullOrEmpty(widgetId);
        ArgumentNullException.ThrowIfNull(change);

        lock (_gate)
        {
            if (!_instances.TryGetValue(widgetId, out WidgetInstance existing))
            {
                // An unknown ID is not an error. The host may reference an instance this provider
                // has not recovered yet, and inventing an entry from a partial callback would
                // produce a widget with a definition ID nothing chose.
                return false;
            }

            // The ID is the key and must not be rewritten by a change delegate; keeping the
            // original prevents an edit from orphaning the entry under a stale key.
            _instances[widgetId] = change(existing) with { Id = widgetId };
            return true;
        }
    }

    /// <summary>Whether an instance with this ID is enabled.</summary>
    public bool Contains(string widgetId)
    {
        lock (_gate)
        {
            return _instances.ContainsKey(widgetId);
        }
    }

    /// <summary>
    /// A point-in-time copy of the enabled instances, safe to enumerate outside the lock.
    /// </summary>
    public WidgetInstance[] Snapshot()
    {
        lock (_gate)
        {
            return [.. _instances.Values];
        }
    }
}
