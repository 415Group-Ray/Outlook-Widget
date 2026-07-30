using OutlookWidget.Core.Models;

namespace OutlookWidget.Core.Graph;

/// <summary>
/// Why one Graph read ended as it did.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every outcome is a state, not an exception</b>, for the same reason
/// <c>TokenAcquisitionResult</c> is: this runs inside a refresh in a COM server the Widgets host
/// started in the background, and an escaping exception would take the provider down and leave the
/// host displaying whatever it last cached with nothing to diagnose.
/// </para>
/// <para>
/// <b><see cref="Forbidden"/> is not the consent-policy block, and conflating them is the specific
/// mistake section 6 names.</b> A tenant that refuses self-consent fails during interactive
/// authorization, before any token exists, so no Graph call is ever made and no 403 is ever seen.
/// A 403 here means a token <em>was</em> issued and the mailbox request was refused anyway — a
/// different condition with a different remedy, and one that must never be relabelled as "ask an
/// administrator for consent".
/// </para>
/// </remarks>
public enum GraphMailStatus
{
    /// <summary>The required requests succeeded and their responses validated.</summary>
    Success,

    /// <summary>
    /// HTTP 401. The token was rejected. Reacquire rather than retry with the same one; a token
    /// that has just expired is the ordinary cause.
    /// </summary>
    Unauthorized,

    /// <summary>
    /// HTTP 403. A token was issued and the request was still refused. See the type remarks: this
    /// is not the consent-policy block, which never reaches Graph.
    /// </summary>
    Forbidden,

    /// <summary>
    /// HTTP 429 or 503. Retry later, honouring <see cref="GraphMailResult.RetryAfter"/> when the
    /// service supplied one.
    /// </summary>
    Throttled,

    /// <summary>The nested Graph timeout elapsed. Distinct from the caller cancelling.</summary>
    TimedOut,

    /// <summary>The caller's deadline or token cancelled the read.</summary>
    Cancelled,

    /// <summary>The request never reached the service, or the connection failed mid-response.</summary>
    NetworkFailure,

    /// <summary>
    /// A response arrived with a success status and could not be trusted — wrong types, a missing
    /// required field, or a value outside its documented range. Deliberately not folded into
    /// <see cref="ServiceFailure"/>: a malformed payload is a contract problem worth seeing in the
    /// operational log as its own category rather than as a generic server error.
    /// </summary>
    InvalidResponse,

    /// <summary>Any other non-success status, including 5xx other than 503.</summary>
    ServiceFailure,
}

/// <summary>The outcome of one Graph read.</summary>
/// <remarks>
/// <b>A class with an explicit <see cref="object.ToString"/>, not a record.</b> The readout carries
/// senders and subjects, and a synthesised <c>ToString</c> would print them. Same reasoning as
/// <see cref="MessagePreview"/>; the override prints the status and nothing else.
/// </remarks>
public sealed class GraphMailResult
{
    private GraphMailResult(
        GraphMailStatus status,
        MailboxReadout? readout,
        int? httpStatusCode,
        TimeSpan? retryAfter)
    {
        Status = status;
        Readout = readout;
        HttpStatusCode = httpStatusCode;
        RetryAfter = retryAfter;
    }

    /// <summary>Why the read ended as it did.</summary>
    public GraphMailStatus Status { get; }

    /// <summary>
    /// What the mailbox returned, present only on <see cref="GraphMailStatus.Success"/>.
    /// </summary>
    public MailboxReadout? Readout { get; }

    /// <summary>
    /// The HTTP status when one was received. A bare integer is a category rather than metadata,
    /// which is why <c>IOperationalLogger</c> accepts it and accepts nothing else about a response.
    /// </summary>
    public int? HttpStatusCode { get; }

    /// <summary>
    /// How long the service asked the caller to wait, when it said. Only ever set alongside
    /// <see cref="GraphMailStatus.Throttled"/>.
    /// </summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Whether a usable readout is present.</summary>
    public bool IsSuccess => Status == GraphMailStatus.Success && Readout is not null;

    public static GraphMailResult Success(MailboxReadout readout)
    {
        ArgumentNullException.ThrowIfNull(readout);
        return new GraphMailResult(GraphMailStatus.Success, readout, httpStatusCode: 200, retryAfter: null);
    }

    /// <summary>
    /// Builds a failed outcome. Rejects <see cref="GraphMailStatus.Success"/>, so a successful
    /// result can never exist without a readout to go with it.
    /// </summary>
    public static GraphMailResult Failure(
        GraphMailStatus status,
        int? httpStatusCode = null,
        TimeSpan? retryAfter = null)
    {
        if (status == GraphMailStatus.Success)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "A successful result must carry a readout; use Success instead.");
        }

        return new GraphMailResult(status, readout: null, httpStatusCode, retryAfter);
    }

    /// <summary>Status and HTTP category only. See the type remarks.</summary>
    public override string ToString() =>
        HttpStatusCode is { } code
            ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Status} ({code})")
            : Status.ToString();
}
