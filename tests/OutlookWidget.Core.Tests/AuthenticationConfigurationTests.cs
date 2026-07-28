using OutlookWidget.Core.Authentication;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// Tests for the Entra registration configuration.
/// </summary>
/// <remarks>
/// Real files in a temporary directory rather than an abstraction over the filesystem. The
/// behaviour under test is largely about what happens to malformed, missing, and unreadable input,
/// and a mocked reader would only return what the test told it to.
/// </remarks>
public sealed class AuthenticationConfigurationTests : IDisposable
{
    private readonly string _directory;

    public AuthenticationConfigurationTests()
    {
        _directory = Path.Combine(
            Path.GetTempPath(), "OutlookWidget-authconfig-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A leaked temporary directory is not worth failing a test run over.
        }
    }

    private void Write(string content) =>
        File.WriteAllText(Path.Combine(_directory, AuthenticationConfiguration.FileName), content);

    [Fact]
    public void A_valid_file_loads_both_identifiers()
    {
        Write("""
            {
              "tenantId": "11111111-2222-3333-4444-555555555555",
              "clientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"
            }
            """);

        AuthenticationConfigurationResult result = AuthenticationConfiguration.Load(_directory);

        Assert.Equal(AuthenticationConfigurationStatus.Loaded, result.Status);
        Assert.True(result.IsLoaded);
        Assert.Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), result.Options!.TenantId);
        Assert.Equal(Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"), result.Options.ClientId);
    }

    [Fact]
    public void The_authority_is_single_tenant_and_derived_rather_than_configured()
    {
        // Derived from the tenant ID so no configuration file can redirect authentication to
        // another authority. A "common" or "organizations" authority would also silently turn a
        // single-tenant registration into a multi-tenant one.
        AuthenticationOptions options = AuthenticationOptions.TryCreate(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"))!;

        Assert.Equal(
            "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555",
            options.Authority);

        Assert.DoesNotContain("common", options.Authority, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("organizations", options.Authority, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_broker_redirect_uri_carries_the_client_id()
    {
        // Documented as required under the registration's Mobile and desktop applications platform.
        // Exposed so a diagnostic can show the exact string the portal must contain, because a
        // mismatch here fails brokered sign-in with nothing obviously wrong in the app.
        AuthenticationOptions options = AuthenticationOptions.TryCreate(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee"))!;

        Assert.Equal(
            "ms-appx-web://microsoft.aad.brokerplugin/aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            options.ExpectedBrokerRedirectUri);
    }

    [Fact]
    public void Only_Mail_ReadBasic_is_ever_requested()
    {
        // The scope decision is section 6's, made once and reviewed. This asserts the exact set,
        // not merely that Mail.Read is absent, so adding a second scope fails here rather than
        // passing a looser check.
        Assert.Equal(["Mail.ReadBasic"], AuthenticationOptions.Scopes);
    }

    [Fact]
    public void A_configuration_file_cannot_widen_the_requested_scope()
    {
        // The load path has nowhere to put a scope, so a file asking for Mail.Read, application
        // permissions, or a different authority changes nothing. This is the property that makes
        // the permission a compile-time decision rather than a deployment one - and it is worth a
        // test, because "there is no such property" is exactly the kind of thing a later
        // convenience edit would undo.
        Write("""
            {
              "tenantId": "11111111-2222-3333-4444-555555555555",
              "clientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
              "scopes": [ "Mail.Read", "Mail.ReadWrite", "User.Read" ],
              "authority": "https://login.microsoftonline.com/common",
              "clientSecret": "not-a-real-secret"
            }
            """);

        AuthenticationConfigurationResult result = AuthenticationConfiguration.Load(_directory);

        Assert.True(result.IsLoaded);
        Assert.Equal(["Mail.ReadBasic"], AuthenticationOptions.Scopes);
        Assert.Equal(
            "https://login.microsoftonline.com/11111111-2222-3333-4444-555555555555",
            result.Options!.Authority);
    }

    [Fact]
    public void An_absent_file_is_a_state_rather_than_an_exception()
    {
        // A provider the Widgets host started in the background must not die because the package
        // shipped without configuration. It renders a card instead.
        AuthenticationConfigurationResult result = AuthenticationConfiguration.Load(_directory);

        Assert.Equal(AuthenticationConfigurationStatus.Absent, result.Status);
        Assert.False(result.IsLoaded);
        Assert.Null(result.Options);
    }

    [Fact]
    public void A_missing_directory_reports_absent_rather_than_throwing()
    {
        AuthenticationConfigurationResult result = AuthenticationConfiguration.Load(
            Path.Combine(_directory, "does-not-exist"));

        Assert.Equal(AuthenticationConfigurationStatus.Absent, result.Status);
    }

    [Fact]
    public void Malformed_json_reports_malformed()
    {
        Write("{ \"tenantId\": ");

        Assert.Equal(
            AuthenticationConfigurationStatus.Malformed,
            AuthenticationConfiguration.Load(_directory).Status);
    }

    [Theory]
    [InlineData("{ }")]
    [InlineData("""{ "tenantId": "11111111-2222-3333-4444-555555555555" }""")]
    [InlineData("""{ "clientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" }""")]
    [InlineData("""{ "tenantId": "not-a-guid", "clientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee" }""")]
    [InlineData("""{ "tenantId": "11111111-2222-3333-4444-555555555555", "clientId": "" }""")]
    public void Contents_that_cannot_identify_a_registration_report_invalid(string json)
    {
        Write(json);

        Assert.Equal(
            AuthenticationConfigurationStatus.Invalid,
            AuthenticationConfiguration.Load(_directory).Status);
    }

    [Fact]
    public void An_all_zero_guid_is_rejected_even_though_it_parses()
    {
        // The template file ships with placeholder zeros. A build packaged with the template
        // unedited must fail closed rather than attempt authentication against a nonexistent
        // registration, and Guid.TryParse alone would accept it.
        Write("""
            {
              "tenantId": "00000000-0000-0000-0000-000000000000",
              "clientId": "00000000-0000-0000-0000-000000000000"
            }
            """);

        Assert.Equal(
            AuthenticationConfigurationStatus.Invalid,
            AuthenticationConfiguration.Load(_directory).Status);

        Assert.Null(AuthenticationOptions.TryCreate(Guid.Empty, Guid.NewGuid()));
        Assert.Null(AuthenticationOptions.TryCreate(Guid.NewGuid(), Guid.Empty));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated()
    {
        // The template carries explanatory comments, and a hand-edited file is the normal case
        // here. Rejecting it for a trailing comma would be a poor use of the operator's time.
        Write("""
            {
              // The registration this build authenticates against.
              "tenantId": "11111111-2222-3333-4444-555555555555",
              "clientId": "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            }
            """);

        Assert.True(AuthenticationConfiguration.Load(_directory).IsLoaded);
    }
}
