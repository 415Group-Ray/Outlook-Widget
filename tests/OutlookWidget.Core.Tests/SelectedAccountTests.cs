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

    private static SelectedAccountResult SignedOut =>
        new(SelectedAccountStatus.SignedOut, null);

    [Fact]
    public void A_recorded_selection_round_trips()
    {
        var store = new SelectedAccountStore(Paths, Registration);

        Assert.True(store.Write("object-id.tenant-id"));
        Assert.Equal(Recorded("object-id.tenant-id"), store.Read());
    }

    [Fact]
    public void An_explicit_signed_out_marker_never_falls_back_to_a_cached_or_windows_account()
    {
        IAccount only = new StubAccount("only.tenant");

        Assert.Null(SilentAuthService.Select([], SignedOut));
        Assert.Null(SilentAuthService.Select([only], SignedOut));
    }

    [Fact]
    public void The_record_is_protected_on_disk()
    {
        // Section 4 step 6 requires the selected home-account and tenant identifiers to live in
        // DPAPI-protected state. Asserting on the bytes rather than trusting the call: the identifier
        // must not be readable in the file, which is the whole of what "protected" buys here.
        new SelectedAccountStore(Paths, Registration).Write("object-id.tenant-id");

        byte[] raw = File.ReadAllBytes(Paths.SelectedAccountFilePath);

        Assert.DoesNotContain(
            "object-id.tenant-id",
            System.Text.Encoding.UTF8.GetString(raw),
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_blob_that_will_not_unprotect_is_unreadable_rather_than_absent()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Paths.SelectedAccountFilePath, [0x01, 0x02, 0x03, 0x04]);

        Assert.Equal(Unreadable, new SelectedAccountStore(Paths, Registration).Read());
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

    /// <summary>
    /// A store whose protection is a no-op, so a test can plant an exact payload.
    /// </summary>
    /// <remarks>
    /// Needed once the record became DPAPI-protected: writing plaintext JSON to the file now fails to
    /// unprotect, so a content-shape test would pass on the <em>encryption</em> failing rather than on
    /// the content check it claims to exercise — right answer, wrong reason, and it would keep passing
    /// if the content checks were deleted.
    /// </remarks>
    private SelectedAccountStore PlaintextStore(AuthenticationOptions options) =>
        new(Paths, options, logger: null, PassThroughProtector.Instance);

    private void PlantPlaintext(string json)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Paths.SelectedAccountFilePath, System.Text.Encoding.UTF8.GetBytes(json));
    }

    [Fact]
    public void A_corrupt_record_is_unreadable_rather_than_absent()
    {
        // The finding this distinction exists for. Reporting corruption as "no selection" sends the
        // caller down the first-cached-account fallback, which on a multi-account machine reads a
        // different mailbox and looks exactly like success.
        PlantPlaintext("{ not json");

        Assert.Equal(Unreadable, PlaintextStore(Registration).Read());
    }

    [Fact]
    public void A_record_for_this_registration_carrying_no_identifier_is_unreadable()
    {
        // Ours, and malformed. A half-written file is the likeliest way to reach this, and it fails
        // closed for the same reason a corrupt one does.
        PlantPlaintext(
            $$"""{"homeAccountId":"","tenantId":"{{Registration.TenantId}}","clientId":"{{Registration.ClientId}}"}""");

        Assert.Equal(Unreadable, PlaintextStore(Registration).Read());
    }

    [Fact]
    public void A_json_null_document_is_unreadable()
    {
        PlantPlaintext("null");

        Assert.Equal(Unreadable, PlaintextStore(Registration).Read());
    }

    [Fact]
    public void A_readable_record_for_another_registration_is_absent()
    {
        // Reached through the content check rather than through a decryption failure, which is the
        // distinction the plaintext store exists to preserve.
        PlantPlaintext(
            $$"""{"homeAccountId":"a.b","tenantId":"{{OtherRegistration.TenantId}}","clientId":"{{OtherRegistration.ClientId}}"}""");

        Assert.Equal(Absent, PlaintextStore(Registration).Read());
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
    public void With_no_recorded_selection_and_exactly_one_cached_account_that_account_is_used()
    {
        // The fallback survives only where there is nothing to be wrong about: first-and-only is the
        // account. A fresh install on a single-account machine must still work without a prompt.
        IAccount only = new StubAccount("only.tenant");

        Assert.Same(only, SilentAuthService.Select([only], Absent));
    }

    [Fact]
    public void With_no_recorded_selection_and_more_than_one_cached_account_it_refuses()
    {
        // The gap a failed write leaves. The companion can report a successful sign-in having been
        // unable to persist the selection, and what it meant to leave behind is simply not there —
        // indistinguishable from a fresh install. Refusing on ambiguity closes that without needing to
        // know which happened, and closes the same gap for state written before the record existed.
        IAccount first = new StubAccount("first.tenant");
        IAccount second = new StubAccount("second.tenant");

        Assert.Null(SilentAuthService.Select([first, second], Absent));
    }

    [Fact]
    public void A_failed_write_reports_false_rather_than_claiming_success()
    {
        // A directory that cannot be created, because the path is occupied by a file. The point is
        // that Write says so: silently absorbing it is what made a failed write look like a fresh
        // install in the first place.
        Directory.CreateDirectory(Path.GetDirectoryName(_root)!);
        File.WriteAllText(_root, "not a directory");

        Assert.False(new SelectedAccountStore(Paths, Registration).Write("object-id.tenant-id"));
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

    [Fact]
    public void The_companion_never_discards_the_selection_write_result()
    {
        // A source-level check because the test project deliberately does not reference
        // OutlookWidget.App — the same reason the interactive-authentication boundary is enforced this
        // way. It is worth having: ignoring this result is not a compile error and not a warning, and
        // the consequence is a sign-in the user is told worked that can never converge, because
        // Select refuses to guess when the record is missing and more than one account is cached.
        //
        // Matching on a bare statement rather than on the call: `_selectedAccounts.Write(x);` on its
        // own line is the defect, while any use of the value — a condition, an assignment, a return —
        // is fine and should not be legislated into one shape.
        foreach (string path in TestInfrastructure.RepositorySources.AppSourceFiles())
        {
            string source = TestInfrastructure.RepositorySources.StripCommentsAndStrings(
                File.ReadAllText(path));

            foreach (string line in source.Split('\n'))
            {
                string trimmed = line.Trim();

                Assert.False(
                    trimmed.StartsWith("_selectedAccounts.Write(", StringComparison.Ordinal)
                    && trimmed.EndsWith(");", StringComparison.Ordinal),
                    $"{Path.GetFileName(path)} discards the result of the selection write. A failed "
                    + "write leaves what a fresh install leaves, and silent acquisition then refuses "
                    + "to guess, so the sign-in must be reported as failed rather than succeeding "
                    + "into a state that cannot converge.");
            }
        }
    }

    [Fact]
    public void The_companion_does_not_short_circuit_a_silent_sign_in_without_a_recorded_selection()
    {
        // A regression guard, and weaker than the tests above — it reads source rather than running
        // the flow, because the test project deliberately does not reference the companion. Stated as
        // such rather than dressed up: it catches the shortcut being restored to its old unguarded
        // form, and it would not catch a subtler way of reaching the same result.
        //
        // The behaviour it guards: taking the silent result whenever it succeeded meant a failed
        // selection write never healed. The next attempt acquired silently and returned before the
        // write was reached, so the companion reported Acquired with no selection on disk — and the
        // comment beside it claimed the retry re-attempted the write, which it did not.
        string source = TestInfrastructure.RepositorySources.StripCommentsAndStrings(
            File.ReadAllText(
                Path.Combine(
                    TestInfrastructure.RepositorySources.AppSourceDirectory,
                    "InteractiveAuthService.cs")));

        Assert.DoesNotContain("silent.IsAcquired ||", source, StringComparison.Ordinal);

        Assert.Contains(
            nameof(SelectedAccountStatus.Recorded),
            source,
            StringComparison.Ordinal);
    }

    /// <summary>Protection that protects nothing, so a test can plant an exact payload.</summary>
    private sealed class PassThroughProtector : IDataProtector
    {
        public static PassThroughProtector Instance { get; } = new();

        public byte[] Protect(byte[] payload, byte[] entropy) => payload;

        public byte[] Unprotect(byte[] payload, byte[] entropy) => payload;
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
