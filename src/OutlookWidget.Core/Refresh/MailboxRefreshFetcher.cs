using OutlookWidget.Core.Graph;
using OutlookWidget.Core.Models;

namespace OutlookWidget.Core.Refresh;

/// <summary>The transient credential and account identity used by one mailbox refresh.</summary>
public sealed class MailboxRefreshAccess
{
    public MailboxRefreshAccess(string accessToken, string homeAccountId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(homeAccountId);

        AccessToken = accessToken;
        HomeAccountId = homeAccountId;
    }

    public string AccessToken { get; }

    public string HomeAccountId { get; }

    /// <summary>Never print the credential or identity.</summary>
    public override string ToString() => nameof(MailboxRefreshAccess);
}

/// <summary>
/// Turns one silent credential acquisition and one validated Graph read into cache-ready state.
/// </summary>
/// <remarks>
/// This is the narrow production bridge between the already-tested Graph client and refresh
/// transaction. It owns no token cache, stores no credential, and returns no partial snapshot:
/// authentication or Graph failure means there is nothing to commit and the prior cache stands.
/// </remarks>
public sealed class MailboxRefreshFetcher : IRefreshFetcher
{
    private readonly Func<CancellationToken, Task<MailboxRefreshAccess?>> _acquire;
    private readonly Func<string, bool, CancellationToken, Task<GraphMailResult>> _read;
    private readonly Guid _tenantId;
    private readonly ISystemClock _clock;
    private readonly bool _includeFocusedCount;

    public MailboxRefreshFetcher(
        Func<CancellationToken, Task<MailboxRefreshAccess?>> acquire,
        Func<string, bool, CancellationToken, Task<GraphMailResult>> read,
        Guid tenantId,
        bool includeFocusedCount,
        ISystemClock? clock = null)
    {
        ArgumentNullException.ThrowIfNull(acquire);
        ArgumentNullException.ThrowIfNull(read);
        ArgumentOutOfRangeException.ThrowIfEqual(tenantId, Guid.Empty);

        _acquire = acquire;
        _read = read;
        _tenantId = tenantId;
        _includeFocusedCount = includeFocusedCount;
        _clock = clock ?? SystemClock.Instance;
    }

    public async Task<RefreshPayload?> FetchAsync(CancellationToken cancellationToken)
    {
        MailboxRefreshAccess? access = await _acquire(cancellationToken).ConfigureAwait(false);

        if (access is null)
        {
            return null;
        }

        GraphMailResult result = await _read(
                access.AccessToken,
                _includeFocusedCount,
                cancellationToken)
            .ConfigureAwait(false);

        if (!result.IsSuccess || result.Readout is not { } readout)
        {
            return null;
        }

        MailboxSnapshot snapshot = MailboxSnapshot.Create(
            readout,
            _tenantId,
            access.HomeAccountId,
            _clock.UtcNow);

        return new RefreshPayload(
            snapshot.Serialize(),
            readout.Messages.Count,
            access.HomeAccountId);
    }
}
