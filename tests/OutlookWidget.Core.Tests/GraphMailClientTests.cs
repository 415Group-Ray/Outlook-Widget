using System.Net;
using OutlookWidget.Core.Graph;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Covers the Graph client's request shape, response validation, and failure classification.
/// </summary>
/// <remarks>
/// <para>
/// <b>These are unit tests against a stub handler and they establish nothing about Microsoft
/// Graph.</b> Gate 10 asks whether <c>Mail.ReadBasic</c> actually returns exactly the approved
/// properties and gate 12 asks whether the Focused filter is accepted by the service at all. Neither
/// can be answered here, because the responses below are ones this repository wrote. A green run
/// means the client handles the shapes it was told to expect.
/// </para>
/// <para>
/// What they do establish is the part that is ours: that no forbidden property is ever requested,
/// that a malformed response is refused rather than cached, and that the optional count's failure
/// cannot discard the two required results.
/// </para>
/// </remarks>
public sealed class GraphMailClientTests
{
    private const string FolderRoute = "mailFolders/inbox?$select";
    private const string MessagesRoute = "/messages?$select";
    private const string FocusedRoute = "$count=true";

    private const string FolderJson =
        """{"id":"AAA","displayName":"Inbox","totalItemCount":42,"unreadItemCount":7}""";

    private const string MessagesJson =
        """
        {"value":[
          {"id":"1","subject":"Quarterly review","from":{"emailAddress":{"name":"Dana Fry","address":"dana@example.com"}},
           "receivedDateTime":"2026-07-30T09:15:00Z","isRead":false,
           "webLink":"https://outlook.office365.com/owa/?ItemID=1"},
          {"id":"2","subject":"","sender":{"emailAddress":{"address":"noreply@example.com"}},
           "receivedDateTime":"2026-07-29T18:00:00Z","isRead":true}
        ]}
        """;

    private static GraphMailClient ClientFor(StubGraphHandler handler) =>
        new(
            new HttpClient(handler) { BaseAddress = new Uri("https://graph.microsoft.com/v1.0/") },
            ownsHttpClient: true);

    private static StubGraphHandler HealthyMailbox() =>
        new StubGraphHandler()
            .Json(FocusedRoute, """{"@odata.count":3,"value":[{"id":"1"}]}""")
            .Json(MessagesRoute, MessagesJson)
            .Json(FolderRoute, FolderJson);

    [Fact]
    public async Task A_healthy_mailbox_produces_counts_and_bounded_previews()
    {
        using GraphMailClient client = ClientFor(HealthyMailbox());

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: true, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Readout!.TotalItemCount);
        Assert.Equal(7, result.Readout.UnreadItemCount);
        Assert.Equal(3, result.Readout.FocusedUnreadCount);
        Assert.Equal(2, result.Readout.Messages.Count);
    }

    [Fact]
    public async Task No_forbidden_property_is_ever_requested()
    {
        // The privacy boundary as an assertion rather than as a comment. Mail.ReadBasic already
        // excludes these at the API, which is the stronger guarantee; this is the second line, and it
        // is the one that fails if somebody widens a $select while the scope stays where it is.
        var handler = HealthyMailbox();
        using GraphMailClient client = ClientFor(handler);

        await client.ReadAsync("token", includeFocusedCount: true, TestContext.Current.CancellationToken);

        foreach (CapturedRequest request in handler.Requests)
        {
            foreach (string forbidden in new[]
                     {
                         "body", "bodyPreview", "attachments", "toRecipients", "ccRecipients",
                         "bccRecipients", "uniqueBody", "internetMessageHeaders", "singleValueExtendedProperties",
                     })
            {
                Assert.DoesNotContain(forbidden, request.Uri, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task The_focused_count_query_sends_no_consistency_level_header()
    {
        // Gate 12 asks, among other things, whether an undocumented ConsistencyLevel: eventual header
        // is required for this filter. Sending one pre-emptively would answer the gate by hiding it,
        // so the absence is held by a test rather than left to whoever next debugs a 400.
        var handler = HealthyMailbox();
        using GraphMailClient client = ClientFor(handler);

        await client.ReadAsync("token", includeFocusedCount: true, TestContext.Current.CancellationToken);

        CapturedRequest focused = handler.Requests.Single(request => request.Uri.Contains(FocusedRoute, StringComparison.Ordinal));

        Assert.DoesNotContain(
            "ConsistencyLevel",
            focused.HeaderNames,
            StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_token_is_sent_as_a_bearer_credential_on_every_request()
    {
        var handler = HealthyMailbox();
        using GraphMailClient client = ClientFor(handler);

        await client.ReadAsync("token", includeFocusedCount: true, TestContext.Current.CancellationToken);

        Assert.All(handler.Requests, request => Assert.Equal("Bearer", request.AuthorizationScheme));
    }

    [Fact]
    public async Task The_focused_count_is_skipped_entirely_when_the_setting_is_off()
    {
        var handler = HealthyMailbox();
        using GraphMailClient client = ClientFor(handler);

        await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.DoesNotContain(handler.Requests, request => request.Uri.Contains(FocusedRoute, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_focused_count_does_not_discard_the_required_results()
    {
        // Section 7's rule, and the reason the three requests are not awaited through a single
        // Task.WhenAll that faults: an optional count is optional in failure as well as in absence.
        var handler = new StubGraphHandler()
            .Status(FocusedRoute, HttpStatusCode.BadRequest)
            .Json(MessagesRoute, MessagesJson)
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: true, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Readout!.FocusedUnreadCount);
        Assert.Equal(7, result.Readout.UnreadItemCount);
    }

    [Fact]
    public async Task A_malformed_focused_count_is_the_same_as_a_failed_one()
    {
        var handler = new StubGraphHandler()
            .Json(FocusedRoute, """{"value":[{"id":"1"}]}""")
            .Json(MessagesRoute, MessagesJson)
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: true, TestContext.Current.CancellationToken);

        // Reading the returned array's length instead of @odata.count would report 1 here, which is
        // the specific wrong answer this endpoint invites.
        Assert.True(result.IsSuccess);
        Assert.Null(result.Readout!.FocusedUnreadCount);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, GraphMailStatus.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden, GraphMailStatus.Forbidden)]
    [InlineData(HttpStatusCode.TooManyRequests, GraphMailStatus.Throttled)]
    [InlineData(HttpStatusCode.ServiceUnavailable, GraphMailStatus.Throttled)]
    [InlineData(HttpStatusCode.InternalServerError, GraphMailStatus.ServiceFailure)]
    [InlineData(HttpStatusCode.BadRequest, GraphMailStatus.ServiceFailure)]
    public async Task A_required_request_failure_is_classified_by_status(
        HttpStatusCode httpStatus,
        GraphMailStatus expected)
    {
        var handler = new StubGraphHandler()
            .Json(MessagesRoute, MessagesJson)
            .Status(FolderRoute, httpStatus);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(expected, result.Status);
        Assert.Equal((int)httpStatus, result.HttpStatusCode);
        Assert.Null(result.Readout);
    }

    [Fact]
    public async Task A_throttled_response_carries_its_retry_after()
    {
        var handler = new StubGraphHandler()
            .Json(MessagesRoute, MessagesJson)
            .Status(FolderRoute, HttpStatusCode.TooManyRequests, TimeSpan.FromSeconds(17));

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(GraphMailStatus.Throttled, result.Status);
        Assert.Equal(TimeSpan.FromSeconds(17), result.RetryAfter);
    }

    [Fact]
    public async Task A_transport_failure_is_a_network_failure_and_not_a_service_one()
    {
        var handler = new StubGraphHandler()
            .Json(MessagesRoute, MessagesJson)
            .NetworkFailure(FolderRoute);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(GraphMailStatus.NetworkFailure, result.Status);
        Assert.Null(result.HttpStatusCode);
    }

    [Fact]
    public async Task A_connection_that_dies_while_the_body_is_read_is_still_a_result_and_not_a_throw()
    {
        // The failure ResponseHeadersRead creates: status 200, headers received, connection dropped
        // mid-payload. Real .NET raises HttpIOException, which derives from IOException and NOT from
        // HttpRequestException — so an earlier version of this catch missed it entirely and let the
        // exception escape a method documented as never throwing, taking the refresh path with it.
        var handler = new StubGraphHandler()
            .Json(MessagesRoute, MessagesJson)
            .BodyFailure(FolderRoute);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(GraphMailStatus.NetworkFailure, result.Status);
        Assert.Null(result.Readout);
    }

    [Fact]
    public async Task A_dying_optional_count_still_cannot_discard_the_required_results()
    {
        // The same failure on the optional request. Worth its own case because the whole point of
        // reducing each request to a value is that an escaping exception from any one of the three
        // would abandon the other two.
        var handler = new StubGraphHandler()
            .BodyFailure(FocusedRoute)
            .Json(MessagesRoute, MessagesJson)
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: true, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Null(result.Readout!.FocusedUnreadCount);
    }

    [Fact]
    public async Task A_success_status_carrying_something_other_than_json_is_an_invalid_response()
    {
        var handler = new StubGraphHandler()
            .Json(MessagesRoute, MessagesJson)
            .Json(FolderRoute, "<html>signed in?</html>");

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(GraphMailStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task A_response_missing_a_required_field_is_refused_rather_than_partially_cached()
    {
        // A message with no receivedDateTime cannot be ordered or aged, and the 24-hour stale-detail
        // rule depends on ages being real. Dropping just that message would quietly show four where
        // five exist, which reads as a quiet mailbox rather than as a fault.
        var handler = new StubGraphHandler()
            .Json(MessagesRoute, """{"value":[{"id":"1","subject":"No date","isRead":false}]}""")
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(GraphMailStatus.InvalidResponse, result.Status);
        Assert.Null(result.Readout);
    }

    [Fact]
    public async Task A_negative_count_is_refused()
    {
        var handler = new StubGraphHandler()
            .Json(MessagesRoute, MessagesJson)
            .Json(FolderRoute, """{"totalItemCount":42,"unreadItemCount":-1}""");

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(GraphMailStatus.InvalidResponse, result.Status);
    }

    [Fact]
    public async Task More_than_five_messages_are_bounded_by_the_client()
    {
        // $top=5 is a request, not a guarantee. Section 7 requires the bound to be applied to the
        // response as well, and over-delivery is truncated rather than treated as a fault.
        string overflowing = "{\"value\":["
            + string.Join(
                ',',
                Enumerable.Range(1, 9).Select(index =>
                    $$"""{"id":"{{index}}","subject":"S{{index}}","receivedDateTime":"2026-07-30T09:00:00Z","isRead":false}"""))
            + "]}";

        var handler = new StubGraphHandler()
            .Json(MessagesRoute, overflowing)
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.True(result.IsSuccess);
        Assert.Equal(MailboxLimits.MaxMessages, result.Readout!.Messages.Count);
    }

    [Fact]
    public async Task A_caller_cancellation_is_reported_as_cancelled_rather_than_as_a_timeout()
    {
        // The two are the same exception from the client's point of view and mean different things to
        // a refresh: one is the pass being abandoned, the other is Graph being slow. Only the caller
        // knows whose token fired, which is why the reclassification happens where it does.
        //
        // The timeout half of this pair is deliberately not exercised as a wall-clock wait: it would
        // cost a real ten seconds per run to assert a branch that is one comparison.
        var handler = new StubGraphHandler().Hang(FolderRoute);
        using GraphMailClient client = ClientFor(handler);
        using var caller = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, caller.Token);

        Assert.Equal(GraphMailStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task An_empty_subject_and_an_absent_sender_get_local_labels()
    {
        var handler = new StubGraphHandler()
            .Json(
                MessagesRoute,
                """{"value":[{"id":"1","receivedDateTime":"2026-07-30T09:00:00Z","isRead":false}]}""")
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        MessagePreview preview = Assert.Single(result.Readout!.Messages);
        Assert.Equal(MailboxLimits.NoSubjectLabel, preview.Subject);
        Assert.Equal(MailboxLimits.UnknownSenderLabel, preview.DisplaySender);
    }

    [Fact]
    public async Task The_sender_name_is_preferred_over_the_address_and_from_over_sender()
    {
        using GraphMailClient client = ClientFor(HealthyMailbox());

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal("Dana Fry", result.Readout!.Messages[0].DisplaySender);

        // The second message has only a sender address, which is the documented fallback.
        Assert.Equal("noreply@example.com", result.Readout.Messages[1].DisplaySender);
    }

    [Theory]
    [InlineData("http://outlook.office365.com/owa/?ItemID=1")]
    [InlineData("javascript:alert(1)")]
    [InlineData("/owa/?ItemID=1")]
    public async Task A_link_that_is_not_absolute_https_is_dropped_and_the_message_survives(string link)
    {
        // Dropping rather than rejecting: the message is still worth showing, and an action that
        // navigates somewhere unexpected is worse than no action at all.
        var handler = new StubGraphHandler()
            .Json(
                MessagesRoute,
                $$"""{"value":[{"id":"1","subject":"S","receivedDateTime":"2026-07-30T09:00:00Z","isRead":false,"webLink":"{{link}}"}]}""")
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        MessagePreview preview = Assert.Single(result.Readout!.Messages);
        Assert.Null(preview.WebLink);
        Assert.Equal("S", preview.Subject);
    }

    [Fact]
    public async Task An_oversized_link_is_dropped_rather_than_truncated()
    {
        string link = "https://outlook.office365.com/owa/?ItemID="
                      + new string('A', MailboxLimits.MaxWebLinkLength);

        var handler = new StubGraphHandler()
            .Json(
                MessagesRoute,
                $$"""{"value":[{"id":"1","subject":"S","receivedDateTime":"2026-07-30T09:00:00Z","isRead":false,"webLink":"{{link}}"}]}""")
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Null(Assert.Single(result.Readout!.Messages).WebLink);
    }

    [Fact]
    public async Task An_oversized_subject_is_truncated_rather_than_rejected()
    {
        string subject = new('S', MailboxLimits.MaxSubjectLength + 500);

        var handler = new StubGraphHandler()
            .Json(
                MessagesRoute,
                $$"""{"value":[{"id":"1","subject":"{{subject}}","receivedDateTime":"2026-07-30T09:00:00Z","isRead":false}]}""")
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        Assert.Equal(
            MailboxLimits.MaxSubjectLength,
            Assert.Single(result.Readout!.Messages).Subject.Length);
    }

    [Fact]
    public async Task Received_times_are_normalised_to_utc()
    {
        var handler = new StubGraphHandler()
            .Json(
                MessagesRoute,
                """{"value":[{"id":"1","subject":"S","receivedDateTime":"2026-07-30T11:00:00+02:00","isRead":false}]}""")
            .Json(FolderRoute, FolderJson);

        using GraphMailClient client = ClientFor(handler);

        GraphMailResult result = await client.ReadAsync("token", includeFocusedCount: false, TestContext.Current.CancellationToken);

        MessagePreview preview = Assert.Single(result.Readout!.Messages);
        Assert.Equal(TimeSpan.Zero, preview.ReceivedAt.Offset);
        Assert.Equal(new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero), preview.ReceivedAt);
    }

    [Fact]
    public void A_result_never_prints_a_readout()
    {
        // The same rule TokenAcquisitionResult carries, for the same reason: interpolating a result
        // into a message is the most natural thing to write, and a synthesised ToString would put a
        // sender and a subject line into it.
        var readout = new MailboxReadout
        {
            TotalItemCount = 1,
            UnreadItemCount = 1,
            Messages =
            [
                new MessagePreview
                {
                    DisplaySender = "Dana Fry",
                    Subject = "Quarterly review",
                    ReceivedAt = DateTimeOffset.UnixEpoch,
                    IsRead = false,
                },
            ],
        };

        string rendered = $"{GraphMailResult.Success(readout)} {readout} {readout.Messages[0]}";

        Assert.DoesNotContain("Dana", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void A_successful_result_cannot_be_built_without_a_readout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => GraphMailResult.Failure(GraphMailStatus.Success));
    }
}
