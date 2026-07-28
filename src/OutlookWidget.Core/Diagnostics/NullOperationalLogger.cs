namespace OutlookWidget.Core.Diagnostics;

/// <summary>
/// Discards every event. Used where a component is constructed without logging
/// configured, so that no call site needs a null check.
/// </summary>
public sealed class NullOperationalLogger : IOperationalLogger
{
    public static NullOperationalLogger Instance { get; } = new();

    public void Record(
        OperationalEventId id,
        OperationalOutcome outcome,
        TimeSpan? duration = null,
        int? recordCount = null,
        int? httpStatusCode = null)
    {
        // Intentionally empty.
    }
}
