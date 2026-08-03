using OutlookWidget.Core.Graph;
using OutlookWidget.Core.Models;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

public sealed class MailboxRefreshFetcherTests
{
    private static readonly Guid TenantId =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Fact]
    public async Task A_successful_read_becomes_a_versioned_snapshot_for_the_same_account()
    {
        var clock = new TestClock(
            initialUtc: new DateTimeOffset(2026, 8, 3, 15, 0, 0, TimeSpan.Zero));
        bool includedFocused = false;

        var fetcher = new MailboxRefreshFetcher(
            _ => Task.FromResult<MailboxRefreshAccess?>(new("token", "home.account")),
            (token, includeFocused, _) =>
            {
                Assert.Equal("token", token);
                includedFocused = includeFocused;
                return Task.FromResult(GraphMailResult.Success(Readout()));
            },
            TenantId,
            includeFocusedCount: true,
            clock);

        RefreshPayload? payload = await fetcher.FetchAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(payload);
        Assert.Equal(1, payload.Value.RecordCount);
        Assert.Equal("home.account", payload.Value.HomeAccountId);
        Assert.True(includedFocused);

        MailboxSnapshot? snapshot = MailboxSnapshot.TryDeserialize(payload.Value.State);
        Assert.NotNull(snapshot);
        Assert.Equal(TenantId, snapshot.TenantId);
        Assert.Equal("home.account", snapshot.HomeAccountId);
        Assert.Equal(clock.UtcNow, snapshot.RefreshedAtUtc);
        Assert.Equal(3, snapshot.FocusedUnreadCount);
    }

    [Fact]
    public async Task No_credential_means_no_Graph_request_and_no_commit_payload()
    {
        bool graphCalled = false;
        var fetcher = new MailboxRefreshFetcher(
            _ => Task.FromResult<MailboxRefreshAccess?>(null),
            (_, _, _) =>
            {
                graphCalled = true;
                return Task.FromResult(GraphMailResult.Success(Readout()));
            },
            TenantId,
            includeFocusedCount: true);

        RefreshPayload? payload = await fetcher.FetchAsync(TestContext.Current.CancellationToken);

        Assert.Null(payload);
        Assert.False(graphCalled);
    }

    [Fact]
    public async Task A_failed_Graph_read_leaves_the_prior_cache_to_the_coordinator()
    {
        var fetcher = new MailboxRefreshFetcher(
            _ => Task.FromResult<MailboxRefreshAccess?>(new("token", "home.account")),
            (_, _, _) => Task.FromResult(GraphMailResult.Failure(GraphMailStatus.Forbidden, 403)),
            TenantId,
            includeFocusedCount: true);

        Assert.Null(await fetcher.FetchAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Refresh_access_never_prints_its_credential_or_account_identity()
    {
        var access = new MailboxRefreshAccess("secret-token", "secret-account");

        Assert.DoesNotContain("secret-token", access.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-account", access.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Refresh_payload_never_prints_its_account_identity()
    {
        var payload = new RefreshPayload([0x01], 1, "secret-account");

        Assert.DoesNotContain("secret-account", payload.ToString(), StringComparison.Ordinal);
    }

    private static MailboxReadout Readout() =>
        new()
        {
            TotalItemCount = 12,
            UnreadItemCount = 4,
            FocusedUnreadCount = 3,
            Messages =
            [
                new MessagePreview
                {
                    DisplaySender = "Sender",
                    Subject = "Subject",
                    ReceivedAt = DateTimeOffset.UnixEpoch,
                    IsRead = false,
                },
            ],
        };
}
