using System.Text;
using OutlookWidget.Core.Models;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Covers the cached payload's shape, its round trip, and what it refuses to load.
/// </summary>
public sealed class MailboxSnapshotTests
{
    private static MailboxSnapshot Sample(int schemaVersion = MailboxSnapshot.CurrentSchemaVersion) =>
        new()
        {
            SchemaVersion = schemaVersion,
            TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555"),
            HomeAccountId = "0000.1111",
            TotalItemCount = 42,
            UnreadItemCount = 7,
            FocusedUnreadCount = 3,
            RefreshedAtUtc = new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero),
            Messages =
            [
                new MessagePreview
                {
                    DisplaySender = "Dana Fry",
                    Subject = "Quarterly review",
                    ReceivedAt = new DateTimeOffset(2026, 7, 30, 8, 0, 0, TimeSpan.Zero),
                    IsRead = false,
                    WebLink = "https://outlook.office365.com/owa/?ItemID=1",
                },
            ],
        };

    [Fact]
    public void A_snapshot_round_trips_through_the_cache_payload()
    {
        MailboxSnapshot original = Sample();

        MailboxSnapshot? restored = MailboxSnapshot.TryDeserialize(original.Serialize());

        Assert.NotNull(restored);
        Assert.Equal(original.TenantId, restored.TenantId);
        Assert.Equal(original.HomeAccountId, restored.HomeAccountId);
        Assert.Equal(original.UnreadItemCount, restored.UnreadItemCount);
        Assert.Equal(original.FocusedUnreadCount, restored.FocusedUnreadCount);
        Assert.Equal(original.RefreshedAtUtc, restored.RefreshedAtUtc);

        MessagePreview preview = Assert.Single(restored.Messages);
        Assert.Equal("Dana Fry", preview.DisplaySender);
        Assert.Equal("Quarterly review", preview.Subject);
        Assert.False(preview.IsRead);
        Assert.Equal(original.Messages[0].WebLink, preview.WebLink);
    }

    [Fact]
    public void An_absent_focused_count_survives_the_round_trip_as_absent()
    {
        // Null is a value here, not a missing field: it is the difference between "the setting is off
        // or the optional request failed" and "there are no focused unread messages". Serialising it
        // away and reading it back as zero would render a confident zero for an unknown.
        MailboxSnapshot original = Sample();

        var withoutFocused = new MailboxSnapshot
        {
            TenantId = original.TenantId,
            HomeAccountId = original.HomeAccountId,
            TotalItemCount = original.TotalItemCount,
            UnreadItemCount = original.UnreadItemCount,
            FocusedUnreadCount = null,
            Messages = original.Messages,
            RefreshedAtUtc = original.RefreshedAtUtc,
        };

        MailboxSnapshot? restored = MailboxSnapshot.TryDeserialize(withoutFocused.Serialize());

        Assert.NotNull(restored);
        Assert.Null(restored.FocusedUnreadCount);
    }

    [Fact]
    public void An_unrecognised_schema_version_is_discarded_rather_than_migrated()
    {
        byte[] payload = Sample(schemaVersion: MailboxSnapshot.CurrentSchemaVersion + 1).Serialize();

        Assert.Null(MailboxSnapshot.TryDeserialize(payload));
    }

    [Fact]
    public void A_payload_that_is_not_json_is_discarded()
    {
        Assert.Null(MailboxSnapshot.TryDeserialize(Encoding.UTF8.GetBytes("not json at all")));
    }

    [Theory]
    [InlineData("""{"schemaVersion":1,"tenantId":"11111111-2222-3333-4444-555555555555","homeAccountId":"a.b","totalItemCount":1,"unreadItemCount":1,"messages":null,"refreshedAtUtc":"2026-07-30T09:00:00Z"}""")]
    [InlineData("""{"schemaVersion":1,"tenantId":"11111111-2222-3333-4444-555555555555","homeAccountId":"a.b","totalItemCount":1,"unreadItemCount":1,"messages":[null],"refreshedAtUtc":"2026-07-30T09:00:00Z"}""")]
    [InlineData("""{"schemaVersion":1,"tenantId":"11111111-2222-3333-4444-555555555555","homeAccountId":null,"totalItemCount":1,"unreadItemCount":1,"messages":[],"refreshedAtUtc":"2026-07-30T09:00:00Z"}""")]
    [InlineData("""{"schemaVersion":1,"tenantId":"11111111-2222-3333-4444-555555555555","homeAccountId":"a.b","totalItemCount":1,"unreadItemCount":1,"messages":[{"displaySender":null,"subject":"S","receivedAt":"2026-07-30T09:00:00Z","isRead":false}],"refreshedAtUtc":"2026-07-30T09:00:00Z"}""")]
    public void An_explicit_null_where_a_required_value_belongs_is_discarded(string json)
    {
        // `required` enforces that a property is *set*, not that it is set to something, and
        // System.Text.Json does not enforce nullable-reference annotations. So each of these
        // deserialises cleanly with a null where the type says there cannot be one. Counting or
        // rendering it threw NullReferenceException, which no filter catches — a malformed cache
        // crashed the load instead of being discarded, which is the one thing TryDeserialize promises
        // never to do.
        Assert.Null(MailboxSnapshot.TryDeserialize(Encoding.UTF8.GetBytes(json)));
    }

    [Fact]
    public void A_payload_carrying_more_than_five_messages_is_discarded()
    {
        // The client bounds the response, so a payload over the ceiling did not come from it. Loading
        // it anyway would let a tampered or hand-edited cache render more than the design allows.
        MailboxSnapshot original = Sample();

        var oversized = new MailboxSnapshot
        {
            TenantId = original.TenantId,
            HomeAccountId = original.HomeAccountId,
            TotalItemCount = original.TotalItemCount,
            UnreadItemCount = original.UnreadItemCount,
            Messages = [.. Enumerable.Repeat(original.Messages[0], MailboxLimits.MaxMessages + 1)],
            RefreshedAtUtc = original.RefreshedAtUtc,
        };

        Assert.Null(MailboxSnapshot.TryDeserialize(oversized.Serialize()));
    }

    [Fact]
    public void The_serialised_payload_holds_no_field_outside_the_approved_set()
    {
        // Reading the payload as text is the point: this is what lands on disk, and the assertion is
        // about what is not in it. A property added to the model without a scope decision fails here
        // rather than in a privacy review after it has been cached on a real machine.
        string json = Encoding.UTF8.GetString(Sample().Serialize());

        foreach (string forbidden in new[]
                 {
                     "body", "bodyPreview", "attachment", "toRecipients", "ccRecipients",
                     "accessToken", "userPrincipalName", "upn",
                 })
        {
            Assert.DoesNotContain(forbidden, json, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void A_snapshot_never_prints_a_sender_or_a_subject()
    {
        string rendered = Sample().ToString();

        Assert.DoesNotContain("Dana", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Quarterly", rendered, StringComparison.Ordinal);
        Assert.Contains("1 messages", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Create_refuses_a_readout_with_no_account()
    {
        var readout = new MailboxReadout
        {
            TotalItemCount = 0,
            UnreadItemCount = 0,
            Messages = [],
        };

        Assert.Throws<ArgumentException>(
            () => MailboxSnapshot.Create(readout, Guid.NewGuid(), "  ", DateTimeOffset.UnixEpoch));
    }
}
