namespace OutlookWidget.Core.Refresh;

/// <summary>
/// How much a widget may display. Ordered by strength so that "the strongest mode
/// present wins" is a comparison rather than a lookup table.
/// </summary>
/// <remarks>
/// The numeric ordering is load-bearing. When several disclosure-reducing operations
/// overlap, the effective mode is the maximum of the modes present, computed by the
/// reader at read time. That is what makes the per-operation files safe: there is no
/// read-modify-write to lose, and no way for one operation to weaken another's
/// suppression by finishing first.
/// </remarks>
public enum DisclosureMode
{
    /// <summary>No suppression. Render according to committed state and widget size.</summary>
    Full = 0,

    /// <summary>
    /// Counts only, at every widget size. Used while enabling "hide message details"
    /// is in flight, and permanently at the small size.
    /// </summary>
    CountsOnly = 1,

    /// <summary>
    /// The signed-out card, regardless of snapshot contents. Used while a logout or
    /// account switch is in flight, and whenever suppression state cannot be read.
    /// </summary>
    SignedOut = 2,
}
