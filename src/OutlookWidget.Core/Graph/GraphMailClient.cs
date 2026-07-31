using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OutlookWidget.Core.Diagnostics;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Refresh;

namespace OutlookWidget.Core.Graph;

/// <summary>
/// Reads the Inbox counts and newest message previews directly over Graph REST.
/// </summary>
/// <remarks>
/// <para>
/// <b>Hand-written REST rather than the Graph SDK, as section 7 requires.</b> Three GETs against
/// <c>v1.0</c> is the entire surface, and the SDK would add a large dependency, its own auth
/// abstraction, and model types carrying every property of a message — including the body fields this
/// product must never hold. A client that cannot express <c>body</c> is a stronger guarantee than one
/// that can and chooses not to.
/// </para>
/// <para>
/// <b>Concurrent, with one timeout boundary nested inside the caller's deadline.</b> The two required
/// requests and the optional Focused count run together under a single
/// <see cref="CoordinationBounds.GraphRequestTimeout"/>, itself linked to the caller's token so the
/// refresh's 20-second async deadline still governs. <c>$batch</c> is deliberately not used: section 7
/// defers it until measurements with three or more requests show a benefit, and three parallel GETs on
/// one connection is not where a single-user widget spends its time.
/// </para>
/// <para>
/// <b>The optional count cannot take the required ones down with it.</b> Each request is awaited
/// through a helper that converts every failure into a value, so nothing here can throw a faulted
/// task into <see cref="Task.WhenAll(System.Collections.Generic.IEnumerable{Task})"/> and abandon two
/// good responses because a filter query was refused. That is section 7's rule stated as a control
/// flow rather than as a comment.
/// </para>
/// <para>
/// <b>Nothing about a response leaves this type except a status code.</b> No URL, no body, no header,
/// no exception message is returned, logged, or thrown. <see cref="IOperationalLogger"/> has nowhere
/// to put any of it, and the outcome type carries a category and an integer.
/// </para>
/// </remarks>
public sealed class GraphMailClient : IDisposable
{
    private const string GraphBaseAddress = "https://graph.microsoft.com/v1.0/";

    /// <summary>
    /// The folder read. Section 7's exact <c>$select</c>.
    /// </summary>
    private const string InboxFolderPath =
        "me/mailFolders/inbox?$select=id,displayName,totalItemCount,unreadItemCount";

    /// <summary>
    /// The message read.
    /// </summary>
    /// <remarks>
    /// <c>id</c> is selected because section 7 documents it, and is then not modelled: section 8's
    /// cache-contents list does not include it and nothing in v1 addresses a message by ID. Every
    /// other selected property is one of the approved fields; there is no <c>body</c>,
    /// <c>bodyPreview</c>, <c>toRecipients</c>, or attachment property here and none may be added
    /// without a scope decision.
    /// </remarks>
    private const string InboxMessagesPath =
        "me/mailFolders/inbox/messages"
        + "?$select=id,subject,from,sender,receivedDateTime,isRead,inferenceClassification,webLink"
        + "&$orderby=receivedDateTime%20desc"
        + "&$top=5";

    /// <summary>
    /// The candidate Focused unread count, still an open Phase 0 question.
    /// </summary>
    /// <remarks>
    /// Gate 12 asks whether this filter is accepted, whether its count agrees with New Outlook,
    /// whether its latency is acceptable, and whether any undocumented header such as
    /// <c>ConsistencyLevel: eventual</c> is required. No such header is sent here, deliberately:
    /// sending one pre-emptively would make the gate unanswerable by hiding the very failure it exists
    /// to detect.
    /// </remarks>
    private const string FocusedUnreadCountPath =
        "me/mailFolders/inbox/messages"
        + "?$count=true"
        + "&$filter=isRead%20eq%20false%20and%20inferenceClassification%20eq%20%27focused%27"
        + "&$top=1"
        + "&$select=id";

    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private readonly IOperationalLogger _logger;

    public GraphMailClient(IOperationalLogger? logger = null)
        : this(CreateDefaultClient(), ownsHttpClient: true, logger)
    {
    }

    /// <summary>
    /// Injectable seam for tests, which drive real request and response handling against a stub
    /// handler rather than mocking the client whose behaviour is the thing under test.
    /// </summary>
    internal GraphMailClient(HttpClient http, bool ownsHttpClient, IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(http);

        _http = http;
        _ownsHttpClient = ownsHttpClient;
        _logger = logger ?? NullOperationalLogger.Instance;
    }

    /// <summary>
    /// Reads the mailbox once.
    /// </summary>
    /// <param name="accessToken">
    /// A delegated <c>Mail.ReadBasic</c> bearer token. Applied per request rather than stored on the
    /// client, so a client instance is never bound to one token and a renewed token cannot be missed.
    /// </param>
    /// <param name="includeFocusedCount">
    /// Whether to attempt the optional Focused unread count. Its failure never affects the result.
    /// </param>
    /// <param name="cancellationToken">The refresh's async deadline.</param>
    public async Task<GraphMailResult> ReadAsync(
        string accessToken,
        bool includeFocusedCount,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);

        long started = Stopwatch.GetTimestamp();

        using var timeout = new CancellationTokenSource(CoordinationBounds.GraphRequestTimeout);
        using CancellationTokenSource linked =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        Task<GraphResponse> folderTask = SendAsync(InboxFolderPath, accessToken, linked.Token);
        Task<GraphResponse> messagesTask = SendAsync(InboxMessagesPath, accessToken, linked.Token);
        Task<GraphResponse>? focusedTask = includeFocusedCount
            ? SendAsync(FocusedUnreadCountPath, accessToken, linked.Token)
            : null;

        GraphResponse folder = await folderTask.ConfigureAwait(false);
        GraphResponse messages = await messagesTask.ConfigureAwait(false);
        GraphResponse? focused = focusedTask is null ? null : await focusedTask.ConfigureAwait(false);

        using (folder.Document)
        using (messages.Document)
        using (focused?.Document)
        {
            return Combine(folder, messages, focused, started, cancellationToken);
        }
    }

    private GraphMailResult Combine(
        GraphResponse folder,
        GraphResponse messages,
        GraphResponse? focused,
        long started,
        CancellationToken callerToken)
    {
        // Caller cancellation outranks everything below, including a complete set of good responses.
        //
        // This is checked here rather than only on the failure path because of a case that reads as
        // success and is not: with the optional Focused count enabled, the two required requests can
        // finish, the caller can then cancel, and the still-pending optional request returns as an
        // ordinary optional failure. The result would be Success — for a pass the caller explicitly
        // abandoned — and a caller that committed it would resurrect a refresh its own deadline had
        // already ended. Discarding a genuinely complete result in the narrow race where the token
        // fires just after the last response is the cheap side of that trade.
        if (callerToken.IsCancellationRequested)
        {
            Record(GraphMailStatus.Cancelled, started, httpStatusCode: null, recordCount: null);
            return GraphMailResult.Failure(GraphMailStatus.Cancelled);
        }

        // When both required requests fail differently, the one with the more specific remedy wins.
        //
        // This used to take the folder's failure because it was named first, which is arbitrary and
        // was wrong rather than merely untidy: a 401 on one request and a 429 on the other would
        // report Throttled, and the caller would back off holding a token that will never work again.
        // Reacquiring on a 401 is cheap and, if the service is genuinely throttling too, the next
        // attempt surfaces that. The reverse order strands the user behind an expired token for as
        // long as the backoff lasts.
        GraphResponse? failed = MoreActionable(folder, messages);

        if (failed is not null)
        {
            // No cancellation reclassification here any more. The send helper reports every
            // OperationCanceledException as TimedOut because it sees one linked token and cannot tell
            // the nested Graph bound from the caller abandoning the pass — and the check above has
            // already answered that question for the whole method, so a TimedOut reaching this point
            // is genuinely the nested bound.
            Record(failed.Status, started, failed.HttpStatusCode, recordCount: null);
            return GraphMailResult.Failure(failed.Status, failed.HttpStatusCode, failed.RetryAfter);
        }

        if (!GraphResponseReader.TryReadFolderCounts(
                folder.Document!.RootElement,
                out int totalItemCount,
                out int unreadItemCount)
            || !GraphResponseReader.TryReadMessages(
                messages.Document!.RootElement,
                out IReadOnlyList<MessagePreview> previews))
        {
            Record(GraphMailStatus.InvalidResponse, started, messages.HttpStatusCode, recordCount: null);
            return GraphMailResult.Failure(GraphMailStatus.InvalidResponse, messages.HttpStatusCode);
        }

        // Every failure mode of the optional count lands on null, including a response that arrived
        // and could not be validated. Section 7 requires its failure not to discard the two required
        // results, and "arrived but was malformed" is a failure like any other.
        int? focusedUnreadCount =
            focused is { Status: GraphMailStatus.Success, Document: not null }
            && GraphResponseReader.TryReadODataCount(focused.Document.RootElement, out int count)
                ? count
                : null;

        var readout = new MailboxReadout
        {
            TotalItemCount = totalItemCount,
            UnreadItemCount = unreadItemCount,
            FocusedUnreadCount = focusedUnreadCount,
            Messages = previews,
        };

        Record(GraphMailStatus.Success, started, httpStatusCode: 200, recordCount: previews.Count);
        return GraphMailResult.Success(readout);
    }

    /// <summary>
    /// Issues one GET and reduces everything that can happen to it into a value.
    /// </summary>
    /// <remarks>
    /// <b>This method does not throw</b>, which is what lets three requests run concurrently without
    /// one taking the others down. <see cref="HttpRequestException"/> covers connection and DNS
    /// failures before a response, <see cref="IOException"/> covers a connection that dies while the
    /// body is being read — see the catch itself, which is where that distinction is easy to get wrong
    /// and was — <see cref="OperationCanceledException"/> covers both cancellation sources, and
    /// <see cref="JsonException"/> covers a success status carrying something that is not JSON.
    /// Anything else is a programming error and is left to propagate.
    /// </remarks>
    private async Task<GraphResponse> SendAsync(
        string path,
        string accessToken,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using HttpResponseMessage response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            int statusCode = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                return new GraphResponse(
                    await ClassifyFailureAsync(response, cancellationToken).ConfigureAwait(false),
                    Document: null,
                    statusCode,
                    ReadRetryAfter(response));
            }

            await using Stream content =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            JsonDocument document =
                await JsonDocument.ParseAsync(content, cancellationToken: cancellationToken)
                    .ConfigureAwait(false);

            return new GraphResponse(GraphMailStatus.Success, document, statusCode, RetryAfter: null);
        }
        catch (JsonException)
        {
            return new GraphResponse(GraphMailStatus.InvalidResponse, null, null, null);
        }
        catch (OperationCanceledException)
        {
            // Reported as a timeout unconditionally; the caller reclassifies it when its own token is
            // the one that fired. See Combine.
            return new GraphResponse(GraphMailStatus.TimedOut, null, null, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException)
        {
            // Both, and the second one is the bug this catch originally had. `ResponseHeadersRead`
            // means the body is still on the wire when ParseAsync consumes it, and a connection that
            // dies at that point surfaces as HttpIOException — which derives from IOException, not
            // from HttpRequestException. Catching only the latter left a perfectly ordinary
            // mid-response network failure escaping a method whose whole contract is that it does not
            // throw, and it would have taken the refresh path down with it.
            return new GraphResponse(GraphMailStatus.NetworkFailure, null, null, null);
        }
    }

    /// <summary>
    /// The failure a caller should act on when the two required requests disagree.
    /// </summary>
    /// <remarks>
    /// Answers <see langword="null"/> when neither failed. The ranking below is by <em>remedy</em>
    /// rather than by severity, because that is what a caller does with the answer.
    /// </remarks>
    private static GraphResponse? MoreActionable(GraphResponse first, GraphResponse second)
    {
        bool firstFailed = first.Status != GraphMailStatus.Success;
        bool secondFailed = second.Status != GraphMailStatus.Success;

        if (!firstFailed)
        {
            return secondFailed ? second : null;
        }

        if (!secondFailed)
        {
            return first;
        }

        int firstRank = RemedyRank(first.Status);
        int secondRank = RemedyRank(second.Status);

        if (secondRank != firstRank)
        {
            return secondRank < firstRank ? second : first;
        }

        // Equal ranks used to fall back to position, which is harmless for every status except this
        // one. Two throttled responses can carry different Retry-After values, and returning the
        // shorter tells the caller to retry before the other endpoint's service-supplied backoff has
        // expired — a throttle the product would then have earned rather than observed. The longer
        // delay satisfies both services; the shorter satisfies only one.
        if (first.Status == GraphMailStatus.Throttled)
        {
            return (second.RetryAfter ?? TimeSpan.Zero) > (first.RetryAfter ?? TimeSpan.Zero)
                ? second
                : first;
        }

        return first;
    }

    /// <summary>
    /// How specific a status's remedy is. Lower wins.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="GraphMailStatus.Unauthorized"/> first: it is the only status whose remedy the caller
    /// must not defer, because every retry with the same token repeats it. Then
    /// <see cref="GraphMailStatus.MailboxNotSupported"/>, the most definitive answer available — the
    /// account has no mailbox to read, so nothing below it can be the more useful thing to report.
    /// Then <see cref="GraphMailStatus.Forbidden"/>, which needs surfacing rather than retrying, and
    /// <see cref="GraphMailStatus.Throttled"/>, which carries explicit service guidance.
    /// <see cref="GraphMailStatus.ItemNotFound"/> sits below those because it is the mildest of the
    /// named states: the mailbox is healthy and the snapshot is merely stale.
    /// </para>
    /// <para>
    /// The rest rank by how much they narrow the problem: a malformed payload is a contract fault
    /// worth seeing, a network failure and a timeout are transient and near-identical to a caller,
    /// and <see cref="GraphMailStatus.ServiceFailure"/> is last because it is the bucket everything
    /// unrecognised falls into and says the least.
    /// </para>
    /// </remarks>
    private static int RemedyRank(GraphMailStatus status) => status switch
    {
        GraphMailStatus.Unauthorized => 0,
        GraphMailStatus.MailboxNotSupported => 1,
        GraphMailStatus.Forbidden => 2,
        GraphMailStatus.Throttled => 3,
        GraphMailStatus.ItemNotFound => 4,
        GraphMailStatus.InvalidResponse => 5,
        GraphMailStatus.NetworkFailure => 6,
        GraphMailStatus.TimedOut => 7,
        GraphMailStatus.Cancelled => 8,
        _ => 9,
    };

    /// <summary>
    /// The two Graph error codes section 10 requires as their own states.
    /// </summary>
    /// <remarks>
    /// A closed allowlist, and that is what makes reading an error body acceptable here. Nothing else
    /// in the response is looked at, no other code is recognised, and no string from the body reaches
    /// a caller — <see cref="TryReadKnownErrorAsync"/> returns a <see cref="GraphMailStatus"/> or
    /// nothing, so the service's own text cannot escape even by accident.
    /// </remarks>
    private static readonly (string Code, GraphMailStatus Status)[] KnownErrorCodes =
    [
        ("MailboxNotEnabledForRESTAPI", GraphMailStatus.MailboxNotSupported),
        ("ErrorItemNotFound", GraphMailStatus.ItemNotFound),
    ];

    /// <summary>
    /// How much of an error body is read before giving up. Generous for a Graph error envelope and
    /// bounded so a hostile or broken response cannot be buffered without limit.
    /// </summary>
    private const int MaxErrorBodyBytes = 8 * 1024;

    /// <summary>
    /// Classifies a failure by its error code where section 10 names one, and by status otherwise.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The error body is now read, narrowly, and the previous version's blanket refusal to read it
    /// was wrong.</b> That version discarded it entirely on the grounds that it carries a request URL
    /// and a correlation identifier — true, and it also carries <c>error.code</c>, which section 10's
    /// recovery table depends on. Collapsing <c>MailboxNotEnabledForRESTAPI</c> into a generic 404
    /// leaves a user with no supported mailbox reading "try again" forever, and collapsing
    /// <c>ErrorItemNotFound</c> hides an ordinary stale-snapshot condition inside a service failure.
    /// </para>
    /// <para>
    /// Section 10 is explicit that the remedy is to read the code and <em>not</em> to expose raw
    /// service text, which is exactly what happens: one enumerated field, matched against a fixed
    /// allowlist, converted to a status, and the rest of the body discarded unread.
    /// </para>
    /// </remarks>
    private static async Task<GraphMailStatus> ClassifyFailureAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        return await TryReadKnownErrorAsync(response, cancellationToken).ConfigureAwait(false)
               ?? ClassifyStatus(response.StatusCode);
    }

    /// <summary>
    /// Extracts <c>error.code</c> if it is one this product acts on, or answers <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Malformed and transport failure modes land on null and fall back to status classification: a
    /// body that is not JSON, one truncated past the cap, one with no <c>error.code</c>, a code that is
    /// not on the allowlist, or a connection that dies mid-body. Cancellation deliberately escapes to
    /// <see cref="SendAsync"/>, which distinguishes the nested timeout from caller cancellation; an
    /// HTTP status must not hide that the body exceeded the client's deadline.
    /// </remarks>
    private static async Task<GraphMailStatus?> TryReadKnownErrorAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        try
        {
            await using Stream body =
                await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            byte[] buffer = new byte[MaxErrorBodyBytes];

            int read = await body
                .ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);

            using JsonDocument document = JsonDocument.Parse(buffer.AsMemory(0, read));

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("error", out JsonElement error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("code", out JsonElement code)
                || code.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            string? value = code.GetString();

            foreach ((string known, GraphMailStatus status) in KnownErrorCodes)
            {
                if (string.Equals(value, known, StringComparison.OrdinalIgnoreCase))
                {
                    return status;
                }
            }

            return null;
        }
        catch (Exception e) when (e is JsonException
                                     or HttpRequestException
                                     or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Maps an HTTP status onto a product state.
    /// </summary>
    /// <remarks>
    /// 503 is grouped with 429 rather than with the other 5xx codes because Graph's throttling
    /// guidance treats both as retry-with-backoff conditions that carry <c>Retry-After</c>, and the
    /// caller's response to them is identical. See the remarks on
    /// <see cref="GraphMailStatus.Forbidden"/> for why 403 is not the consent-policy block.
    /// </remarks>
    private static GraphMailStatus ClassifyStatus(HttpStatusCode statusCode) => statusCode switch
    {
        HttpStatusCode.Unauthorized => GraphMailStatus.Unauthorized,
        HttpStatusCode.Forbidden => GraphMailStatus.Forbidden,
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => GraphMailStatus.Throttled,
        _ => GraphMailStatus.ServiceFailure,
    };

    /// <summary>
    /// Reads <c>Retry-After</c> in either documented form, or answers null.
    /// </summary>
    /// <remarks>
    /// The header is specified as either a delay in seconds or an HTTP date, and Graph sends the
    /// former. The date form is handled anyway because handling it is three lines and misreading a
    /// date as "no guidance" would turn a polite backoff into a retry storm. A date in the past
    /// yields null rather than a negative delay.
    /// </remarks>
    private static TimeSpan? ReadRetryAfter(HttpResponseMessage response)
    {
        RetryConditionHeaderValue? header = response.Headers.RetryAfter;

        if (header is null)
        {
            return null;
        }

        if (header.Delta is { } delta)
        {
            return delta > TimeSpan.Zero ? delta : null;
        }

        if (header.Date is { } date)
        {
            TimeSpan remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero ? remaining : null;
        }

        return null;
    }

    private void Record(GraphMailStatus status, long started, int? httpStatusCode, int? recordCount)
    {
        (OperationalEventId id, OperationalOutcome outcome) = status switch
        {
            GraphMailStatus.Success =>
                (OperationalEventId.GraphRequestCompleted, OperationalOutcome.Success),
            GraphMailStatus.Throttled =>
                (OperationalEventId.GraphThrottled, OperationalOutcome.Failed),
            GraphMailStatus.Cancelled =>
                (OperationalEventId.GraphRequestFailed, OperationalOutcome.Cancelled),
            GraphMailStatus.TimedOut =>
                (OperationalEventId.GraphRequestFailed, OperationalOutcome.Timeout),
            _ => (OperationalEventId.GraphRequestFailed, OperationalOutcome.Failed),
        };

        _logger.Record(
            id,
            outcome,
            Stopwatch.GetElapsedTime(started),
            recordCount,
            httpStatusCode);
    }

    private static HttpClient CreateDefaultClient() =>
        new()
        {
            BaseAddress = new Uri(GraphBaseAddress),

            // Cancellation is owned by the linked token, not by HttpClient. Its own timeout would
            // surface as the same OperationCanceledException from a different source, which would make
            // "the caller gave up" and "the service is slow" indistinguishable at the catch site.
            Timeout = Timeout.InfiniteTimeSpan,
        };

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _http.Dispose();
        }
    }

    /// <summary>One request's outcome, before the required and optional ones are combined.</summary>
    /// <remarks>
    /// A record here is safe where it is not elsewhere in this product: the synthesised
    /// <c>ToString</c> prints each member, and <see cref="JsonDocument"/> does not override
    /// <see cref="object.ToString"/>, so it contributes its type name rather than the response. The
    /// type is private and never escapes this class either way.
    /// </remarks>
    private sealed record GraphResponse(
        GraphMailStatus Status,
        JsonDocument? Document,
        int? HttpStatusCode,
        TimeSpan? RetryAfter);
}
