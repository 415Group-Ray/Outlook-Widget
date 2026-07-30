using Microsoft.Identity.Client;
using OutlookWidget.Core.Authentication;
using OutlookWidget.Core.Caching;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Covers the recorded account selection and the rule that consumes it.
/// </summary>
/// <remarks>
/// This is the limitation the plan calls a prerequisite for gate 10 rather than cleanup: silent
/// acquisition used to take the <em>first</em> cached account, and MSAL guarantees no ordering. These
/// tests are about which account is asked for, and establish nothing about WAM, the broker, or whether
/// a token is issued.
/// </remarks>
public sealed class SelectedAccountTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "OutlookWidgetTests",
        Guid.NewGuid().ToString("N"));

    private static readonly AuthenticationOptions Registration =
        AuthenticationOptions.TryCreate(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"))!;

    private static readonly AuthenticationOptions OtherRegistration =
        AuthenticationOptions.TryCreate(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            Guid.Parse("44444444-4444-4444-4444-444444444444"))!;

    private CoordinationPaths Paths => new(_root, scope: "test");

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leftover temp directory is not worth failing a test run over.
        }
    }

    [Fact]
    public void A_recorded_selection_round_trips()
    {
        var store = new SelectedAccountStore(Paths, Registration);

        store.Write("object-id.tenant-id");

        Assert.Equal("object-id.tenant-id", store.TryRead());
    }

    [Fact]
    public void An_absent_record_reads_as_no_selection()
    {
        Assert.Null(new SelectedAccountStore(Paths, Registration).TryRead());
    }

    [Fact]
    public void A_record_written_for_another_registration_is_ignored()
    {
        // State lives under package identity, which does not change when authentication.json is
        // repointed at a different client or tenant. Honouring the old registration's selection would
        // have the provider ask for an account that means nothing to the new one.
        new SelectedAccountStore(Paths, OtherRegistration).Write("object-id.tenant-id");

        Assert.Null(new SelectedAccountStore(Paths, Registration).TryRead());
    }

    [Fact]
    public void A_corrupt_record_reads_as_no_selection_rather_than_throwing()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SelectedAccountFilePath, "{ not json");

        Assert.Null(new SelectedAccountStore(Paths, Registration).TryRead());
    }

    [Fact]
    public void Clearing_removes_the_selection_and_is_safe_when_there_is_none()
    {
        var store = new SelectedAccountStore(Paths, Registration);

        store.Clear();
        store.Write("object-id.tenant-id");
        store.Clear();

        Assert.Null(store.TryRead());
    }

    [Fact]
    public void The_selection_file_is_not_the_snapshot_or_the_authorization_record()
    {
        // Three files answering three questions. Folding this into the snapshot would clear it on
        // logout, when silent acquisition still needs it; folding it into the authorization record
        // would make one file mean two things and get it cleared for the wrong reason.
        CoordinationPaths paths = Paths;

        Assert.NotEqual(paths.StateFilePath, paths.SelectedAccountFilePath);
        Assert.NotEqual(paths.AuthorizationStateFilePath, paths.SelectedAccountFilePath);
    }

    [Fact]
    public void With_no_recorded_selection_the_first_cached_account_is_still_used()
    {
        // The prior behaviour, preserved for a fresh install and for state written before the
        // selection existed. It is a guess about intent, which is why it is now the last resort.
        IAccount first = new StubAccount("first.tenant");
        IAccount second = new StubAccount("second.tenant");

        Assert.Same(first, SilentAuthService.Select([first, second], selectedHomeAccountId: null));
    }

    [Fact]
    public void With_no_cached_account_and_no_selection_the_operating_system_account_is_the_fallback()
    {
        Assert.Same(
            PublicClientApplication.OperatingSystemAccount,
            SilentAuthService.Select([], selectedHomeAccountId: null));
    }

    [Fact]
    public void A_recorded_selection_wins_over_enumeration_order()
    {
        // The point of the whole record: MSAL guarantees no ordering, so the account the user actually
        // chose has to be named rather than found by position.
        IAccount first = new StubAccount("first.tenant");
        IAccount chosen = new StubAccount("chosen.tenant");

        Assert.Same(chosen, SilentAuthService.Select([first, chosen], "chosen.tenant"));
    }

    [Fact]
    public void A_recorded_selection_that_is_no_longer_cached_refuses_rather_than_falling_back()
    {
        // The case this exists to prevent. Falling back to `first` here would read a different
        // mailbox and look exactly like success; null becomes interaction-required, and a fresh
        // sign-in is the honest remedy.
        IAccount first = new StubAccount("first.tenant");

        Assert.Null(SilentAuthService.Select([first], "chosen.tenant"));
    }

    [Fact]
    public void A_recorded_selection_with_an_empty_account_cache_also_refuses()
    {
        // Notably it does NOT fall through to the operating-system account. That fallback is a guess
        // about intent, and a recorded selection means intent is known — guessing over it would be a
        // regression dressed as resilience.
        Assert.Null(SilentAuthService.Select([], "chosen.tenant"));
    }

    /// <summary>The three properties of <see cref="IAccount"/>, and no MSAL machinery.</summary>
    private sealed class StubAccount(string identifier) : IAccount
    {
        public string Username => "unused";

        public string Environment => "login.microsoftonline.com";

        public AccountId HomeAccountId { get; } =
            new(identifier, identifier.Split('.')[0], identifier.Split('.')[1]);
    }
}
