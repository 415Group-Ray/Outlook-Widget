namespace OutlookWidget.Provider.Cards;

/// <summary>
/// The complete set of action verbs this provider honours.
/// </summary>
/// <remarks>
/// <para>
/// A closed set, checked on every <c>OnActionInvoked</c> callback. The verb arrives from the
/// Widgets host as a string that originated in card JSON, so it is input rather than a
/// guarantee: an unrecognised verb is ignored, never dispatched by reflection or used to build a
/// path, a URL, or a process argument.
/// </para>
/// <para>
/// There is deliberately no verb for opening a message. Section 3 requires a message action to
/// carry only a bounded display slot and a snapshot generation, to be validated against the
/// committed snapshot and an Outlook-host allowlist before anything is launched. None of that is
/// possible until the snapshot model exists, and a verb that opened a link without those checks
/// would be the exact defect the design forbids — so the verb is absent rather than
/// provisionally implemented.
/// </para>
/// </remarks>
internal static class WidgetVerbs
{
    /// <summary>Request a refresh of the displayed content.</summary>
    public const string Refresh = "refresh";

    /// <summary>Launch New Outlook.</summary>
    public const string OpenOutlook = "openOutlook";

    /// <summary>Launch the companion application, for sign-in, settings, and recovery.</summary>
    public const string OpenCompanion = "openCompanion";
}
