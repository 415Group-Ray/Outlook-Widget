using System.Text.Json;
using System.Text.Json.Serialization;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Authentication;

/// <summary>
/// The Entra registration this build authenticates against.
/// </summary>
/// <remarks>
/// <para>
/// <b>Identity is configuration; permission is not.</b> The tenant and client IDs vary by
/// environment and are supplied from a file. <see cref="Scopes"/> is a compile-time constant and
/// deliberately unreadable from configuration, so no config file — edited, swapped, or replaced by
/// something else on the machine — can widen what this application may read. Section 6 permits
/// exactly one delegated permission, and making it configurable would turn a reviewed scope
/// decision into a deployment detail.
/// </para>
/// <para>
/// Neither ID is a secret; both are identifiers that appear in network traffic. They are kept out
/// of Git anyway, because a committed development value is the one most likely to be pointed at
/// the wrong environment by accident.
/// </para>
/// </remarks>
public sealed record AuthenticationOptions
{
    private AuthenticationOptions(Guid tenantId, Guid clientId)
    {
        TenantId = tenantId;
        ClientId = clientId;
    }

    /// <summary>The single tenant this registration belongs to.</summary>
    public Guid TenantId { get; }

    /// <summary>The public client registration's application ID.</summary>
    public Guid ClientId { get; }

    /// <summary>
    /// The only Graph permission this application ever requests.
    /// </summary>
    /// <remarks>
    /// <c>Mail.ReadBasic</c> and nothing else. It excludes message bodies, body previews,
    /// attachments, and extended properties at the API boundary, which is stronger than requesting
    /// a broader scope and choosing not to read those fields. <c>Mail.Read</c> must never appear
    /// here, and no configuration path may add to this array.
    /// </remarks>
    public static IReadOnlyList<string> Scopes { get; } = ["Mail.ReadBasic"];

    /// <summary>
    /// The single-tenant authority. Built from <see cref="TenantId"/> rather than read from
    /// configuration, so a config file cannot redirect authentication to another authority.
    /// </summary>
    public string Authority =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"https://login.microsoftonline.com/{TenantId:D}");

    /// <summary>
    /// The WAM broker redirect URI this registration must declare, for diagnostics that check the
    /// portal configuration matches the build.
    /// </summary>
    /// <remarks>
    /// Documented as required under the app registration's <em>Mobile and desktop applications</em>
    /// platform. Registering it under the Web platform instead is a silent failure: brokered
    /// sign-in simply never completes.
    /// </remarks>
    public string ExpectedBrokerRedirectUri =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"ms-appx-web://microsoft.aad.brokerplugin/{ClientId:D}");

    /// <summary>
    /// Creates options from two IDs, rejecting values that cannot identify a registration.
    /// </summary>
    /// <returns>
    /// The options, or <see langword="null"/> when either ID is empty. An empty GUID parses
    /// successfully and is never a real tenant or client, so it has to be rejected explicitly
    /// rather than relying on parsing to catch it.
    /// </returns>
    public static AuthenticationOptions? TryCreate(Guid tenantId, Guid clientId) =>
        tenantId == Guid.Empty || clientId == Guid.Empty
            ? null
            : new AuthenticationOptions(tenantId, clientId);
}

/// <summary>Why loading the authentication configuration ended as it did.</summary>
public enum AuthenticationConfigurationStatus
{
    /// <summary>A complete, valid configuration was read.</summary>
    Loaded,

    /// <summary>No configuration file is present. The build was packaged without one.</summary>
    Absent,

    /// <summary>The file exists but is not readable as JSON.</summary>
    Malformed,

    /// <summary>The file parsed but its contents cannot identify a registration.</summary>
    Invalid,

    /// <summary>The file exists and could not be opened. Distinct from absent, and transient.</summary>
    Unreadable,
}

/// <summary>The outcome of one configuration load.</summary>
/// <param name="Status">Why the load ended as it did.</param>
/// <param name="Options">The options, present only on <see cref="AuthenticationConfigurationStatus.Loaded"/>.</param>
public readonly record struct AuthenticationConfigurationResult(
    AuthenticationConfigurationStatus Status,
    AuthenticationOptions? Options)
{
    public bool IsLoaded => Status == AuthenticationConfigurationStatus.Loaded && Options is not null;
}

/// <summary>
/// Reads the Entra registration identifiers that ship beside the executable.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every failure is a state, not an exception.</b> A missing or unusable configuration means
/// this build cannot authenticate, which is a card the user should see — "sign-in unavailable,
/// open the companion" — rather than a crash in a widget provider the Widgets host started in the
/// background. Throwing here would take out the provider process on a deployment mistake.
/// </para>
/// <para>
/// The file ships in the package next to each executable rather than at the package root, so
/// neither process has to walk relative paths out of its own directory to find it.
/// </para>
/// </remarks>
public static class AuthenticationConfiguration
{
    /// <summary>The file name, identical beside both executables.</summary>
    public const string FileName = "authentication.json";

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Loads the configuration from the directory containing the running executable.
    /// </summary>
    public static AuthenticationConfigurationResult Load(IOperationalLogger? logger = null) =>
        Load(AppContext.BaseDirectory, logger);

    /// <summary>
    /// Loads the configuration from a specific directory. Injectable so tests exercise real files.
    /// </summary>
    public static AuthenticationConfigurationResult Load(
        string directory,
        IOperationalLogger? logger = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        string path = Path.Combine(directory, FileName);
        string json;

        try
        {
            json = File.ReadAllText(path);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return new AuthenticationConfigurationResult(
                AuthenticationConfigurationStatus.Absent, null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Present but unreadable. Deliberately distinct from absent: one is a packaging
            // mistake and the other is usually transient, and collapsing them would send a
            // sharing violation into a "reinstall the app" diagnostic.
            (logger ?? NullOperationalLogger.Instance)
                .Record(OperationalEventId.CacheReadFailed, OperationalOutcome.Failed);
            return new AuthenticationConfigurationResult(
                AuthenticationConfigurationStatus.Unreadable, null);
        }

        ConfigurationFile? file;

        try
        {
            file = JsonSerializer.Deserialize<ConfigurationFile>(json, ReadOptions);
        }
        catch (JsonException)
        {
            return new AuthenticationConfigurationResult(
                AuthenticationConfigurationStatus.Malformed, null);
        }

        if (file is null
            || !Guid.TryParse(file.TenantId, out Guid tenantId)
            || !Guid.TryParse(file.ClientId, out Guid clientId))
        {
            return new AuthenticationConfigurationResult(
                AuthenticationConfigurationStatus.Invalid, null);
        }

        AuthenticationOptions? options = AuthenticationOptions.TryCreate(tenantId, clientId);

        return options is null
            ? new AuthenticationConfigurationResult(AuthenticationConfigurationStatus.Invalid, null)
            : new AuthenticationConfigurationResult(
                AuthenticationConfigurationStatus.Loaded, options);
    }

    /// <summary>
    /// The file's shape. Two identifiers, and nothing else.
    /// </summary>
    /// <remarks>
    /// There is no scopes property, no authority property, and no permission property, and none may
    /// be added. Any other field in the file is ignored rather than honoured — a file that tries to
    /// request <c>Mail.Read</c> changes nothing, because there is nowhere for that value to land.
    /// </remarks>
    private sealed class ConfigurationFile
    {
        [JsonPropertyName("tenantId")]
        public string? TenantId { get; init; }

        [JsonPropertyName("clientId")]
        public string? ClientId { get; init; }
    }
}
