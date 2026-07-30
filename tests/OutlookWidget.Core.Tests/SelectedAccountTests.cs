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

    private static SelectedAccountResult Recorded(string id) =>
        new(SelectedAccountStatus.Recorded, id);

    private static SelectedAccountResult Absent =>
        new(SelectedAccountStatus.Absent, null);

    private static SelectedAccountResult Unreadable =>
        new(SelectedAccountStatus.Unreadable, null);

    [Fact]
    public void A_recorded_selection_round_trips()
    {
        var store = new SelectedAccountStore(Paths, Registration);

        store.Write("object-id.tenant-id");

        Assert.Equal(Recorded("object-id.tenant-id"), store.Read());
    }

    [Fact]
    public void A_missing_file_is_absent_and_not_unreadable()
    {
        // The one genuinely absent case: a fresh install. It has to stay distinguishable from the
        // fail-closed one, because it is the only status that permits the fallback.
        Assert.Equal(Absent, new SelectedAccountStore(Paths, Registration).Read());
    }

    [Fact]
    public void A_record_written_for_another_registration_is_absent_rather_than_unreadable()
    {
        // A perfectly good record about something else. State lives under package identity, which does
        // not change when authentication.json is repointed at a different client or tenant, so this
        // registration genuinely has no selection — the fallback is correct here.
        new SelectedAccountStore(Paths, OtherRegistration).Write("object-id.tenant-id");

        Assert.Equal(Absent, new SelectedAccountStore(Paths, Registration).Read());
    }

    [Fact]
    public void A_corrupt_record_is_unreadable_rather_than_absent()
    {
        // The finding this distinction exists for. Reporting corruption as "no selection" sends the
        // caller down the first-cached-account fallback, which on a multi-account machine reads a
        // different mailbox and looks exactly like success.
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SelectedAccountFilePath, "{ not json");

        Assert.Equal(Unreadable, new SelectedAccountStore(Paths, Registration).Read());
    }

    [Fact]
    public void A_record_for_this_registration_carrying_no_identifier_is_unreadable()
    {
        // Ours, and malformed. A half-written file is the likeliest way to reach this, and it fails
        // closed for the same reason a corrupt one does.
        Directory.CreateDirectory(_root);
        File.WriteAllText(
            Paths.SelectedAccountFilePath,
            $$"""{"homeAccountId":"","tenantId":"{{Registration.TenantId}}","clientId":"{{Registration.ClientId}}"}""");

        Assert.Equal(Unreadable, new SelectedAccountStore(Paths, Registration).Read());
    }

    [Fact]
    public void A_json_null_document_is_unreadable()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Paths.SelectedAccountFilePath, "null");

        Assert.Equal(Unreadable, new SelectedAccountStore(Paths, Registration).Read());
    }

    [Fact]
    public void Clearing_removes_the_selection_and_is_safe_when_there_is_none()
    {
        var store = new SelectedAccountStore(Paths, Registration);

        store.Clear();
        store.Write("object-id.tenant-id");
        store.Clear();

        Assert.Equal(Absent, store.Read());
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

        Assert.Same(first, SilentAuthService.Select([first, second], Absent));
    }

    [Fact]
    public void With_no_cached_account_and_no_selection_the_operating_system_account_is_the_fallback()
    {
        Assert.Same(
            PublicClientApplication.OperatingSystemAccount,
            SilentAuthService.Select([], Absent));
    }

    [Fact]
    public void An_unreadable_record_refuses_rather_than_falling_back()
    {
        // The heart of the fix. Before this, an unreadable record was reported as "no selection" and
        // this call returned `first` — a different mailbox, silently, on any machine with more than
        // one cached account. Failing closed costs a sign-in prompt; the alternative costs the wrong
        // person's mail on screen.
        IAccount first = new StubAccount("first.tenant");
        IAccount second = new StubAccount("second.tenant");

        Assert.Null(SilentAuthService.Select([first, second], Unreadable));
    }

    [Fact]
    public void An_unreadable_record_refuses_even_with_a_single_cached_account()
    {
        // Deliberately refusing in the case where guessing would almost certainly be right. "Almost
        // certainly" is the wrong standard for which mailbox to display, and a rule with an exception
        // for the common case is one that stops holding exactly when a machine grows a second account.
        IAccount only = new StubAccount("only.tenant");

        Assert.Null(SilentAuthService.Select([only], Unreadable));
    }

    [Fact]
    public void A_recorded_selection_wins_over_enumeration_order()
    {
        // The point of the whole record: MSAL guarantees no ordering, so the account the user actually
        // chose has to be named rather than found by position.
        IAccount first = new StubAccount("first.tenant");
        IAccount chosen = new StubAccount("chosen.tenant");

        Assert.Same(chosen, SilentAuthService.Select([first, chosen], Recorded("chosen.tenant")));
    }

    [Fact]
    public void A_recorded_selection_that_is_no_longer_cached_refuses_rather_than_falling_back()
    {
        // The case this exists to prevent. Falling back to `first` here would read a different
        // mailbox and look exactly like success; null becomes interaction-required, and a fresh
        // sign-in is the honest remedy.
        IAccount first = new StubAccount("first.tenant");

        Assert.Null(SilentAuthService.Select([first], Recorded("chosen.tenant")));
    }

    [Fact]
    public void A_recorded_selection_with_an_empty_account_cache_also_refuses()
    {
        // Notably it does NOT fall through to the operating-system account. That fallback is a guess
        // about intent, and a recorded selection means intent is known — guessing over it would be a
        // regression dressed as resilience.
        Assert.Null(SilentAuthService.Select([], Recorded("chosen.tenant")));
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
