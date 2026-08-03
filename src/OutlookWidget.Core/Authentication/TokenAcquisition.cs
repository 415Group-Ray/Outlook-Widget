namespace OutlookWidget.Core.Authentication;

/// <summary>
/// Why a token acquisition ended as it did.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every outcome is a state, not an exception.</b> The provider acquires silently in a process
/// the Widgets host started in the background; an exception escaping there kills the provider and
/// leaves whatever the host last cached on screen, with nothing to diagnose. Each member below maps
/// to a card the user can act on.
/// </para>
/// <para>
/// The members are deliberately not collapsed. <see cref="InteractionRequired"/> and
/// <see cref="BrokerUnavailable"/> both mean "no token", and section 8 requires them to be visibly
/// distinguishable: one is fixed by opening the companion and signing in, the other is a machine
/// problem no amount of signing in will fix. <see cref="ApprovalRequired"/> is separated for the
/// reason recorded in the plan's risk table — consent blocked by tenant policy must never surface as
/// a Graph 403, because the remedy is an administrator request rather than a retry.
/// </para>
/// </remarks>
public enum TokenAcquisitionStatus
{
    /// <summary>A token was acquired.</summary>
    Acquired,

    /// <summary>
    /// This build has no usable Entra registration, so no request was attempted. Distinct from a
    /// failure: nothing is wrong with the account or the machine, the package shipped incomplete.
    /// </summary>
    NoConfiguration,

    /// <summary>
    /// No token could be issued without user interaction, including the case where no account is
    /// known yet. Terminal for the provider; the companion's interactive path is the remedy.
    /// </summary>
    InteractionRequired,

    /// <summary>
    /// Consent has not been granted and cannot be self-granted. Requires an administrator, so it is
    /// not something a retry or a fresh sign-in resolves.
    /// </summary>
    ApprovalRequired,

    /// <summary>
    /// The Windows authentication broker could not be used. Not recoverable in-process and not a
    /// sign-in problem.
    /// </summary>
    BrokerUnavailable,

    /// <summary>The user dismissed the authentication dialog, or declined consent.</summary>
    Cancelled,

    /// <summary>
    /// Anything else, including network and service failures. Usually transient.
    /// </summary>
    Failed,
}

/// <summary>
/// The outcome of one token acquisition.
/// </summary>
/// <remarks>
/// <para>
/// <b>Deliberately a class and not a record, and it overrides
/// <see cref="object.ToString"/>.</b> A positional record synthesises a <c>ToString</c> that prints
/// every member, so <c>$"{result}"</c> in a log line, an exception message, or a debugger-adjacent
/// diagnostic would emit the access token. That is not a hypothetical: interpolating a result object
/// is the most natural thing to write, and the synthesised implementation would make it a
/// credential leak that compiles and passes review. The override below prints the status and the
/// expiry and never the token.
/// </para>
/// <para>
/// <see cref="AccessToken"/> is present only on <see cref="TokenAcquisitionStatus.Acquired"/>. No
/// other member of this type may ever expose the account, the tenant, the raw MSAL result, or the
/// exception, per section 6's logging and privacy rules.
/// </para>
/// </remarks>
public sealed class TokenAcquisitionResult
{
    private TokenAcquisitionResult(
        TokenAcquisitionStatus status,
        string? accessToken,
        DateTimeOffset? expiresOn,
        string? homeAccountId)
    {
        Status = status;
        AccessToken = accessToken;
        ExpiresOn = expiresOn;
        HomeAccountId = homeAccountId;
    }

    /// <summary>Why the acquisition ended as it did.</summary>
    public TokenAcquisitionStatus Status { get; }

    /// <summary>
    /// The bearer token, or <see langword="null"/> for every status other than
    /// <see cref="TokenAcquisitionStatus.Acquired"/>.
    /// </summary>
    public string? AccessToken { get; }

    /// <summary>When the token expires, for callers deciding whether to reacquire.</summary>
    public DateTimeOffset? ExpiresOn { get; }

    /// <summary>The opaque account identifier paired with this exact token acquisition.</summary>
    public string? HomeAccountId { get; }

    /// <summary>Whether a token is present and usable.</summary>
    public bool IsAcquired => Status == TokenAcquisitionStatus.Acquired && AccessToken is not null;

    /// <summary>
    /// Whether the companion's interactive path is the remedy. False for
    /// <see cref="TokenAcquisitionStatus.BrokerUnavailable"/> and
    /// <see cref="TokenAcquisitionStatus.ApprovalRequired"/>, where offering sign-in would invite an
    /// action whose only possible outcome is the same failure.
    /// </summary>
    public bool IsResolvedBySigningIn => Status is TokenAcquisitionStatus.InteractionRequired
        or TokenAcquisitionStatus.Cancelled;

    public static TokenAcquisitionResult Acquired(
        string accessToken,
        DateTimeOffset expiresOn,
        string? homeAccountId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accessToken);
        return new TokenAcquisitionResult(
            TokenAcquisitionStatus.Acquired,
            accessToken,
            expiresOn,
            homeAccountId);
    }

    /// <summary>
    /// Builds a tokenless outcome. Rejects <see cref="TokenAcquisitionStatus.Acquired"/>, so an
    /// "acquired" result can never exist without a token to go with it.
    /// </summary>
    public static TokenAcquisitionResult Unavailable(TokenAcquisitionStatus status)
    {
        if (status == TokenAcquisitionStatus.Acquired)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "An acquired result must carry a token; use Acquired instead.");
        }

        return new TokenAcquisitionResult(status, accessToken: null, expiresOn: null, homeAccountId: null);
    }

    /// <summary>
    /// Status and expiry only. See the type remarks: this override is the reason this type is not a
    /// record, and removing it would turn any interpolation of a result into a token leak.
    /// </summary>
    public override string ToString() =>
        ExpiresOn is { } expiry
            ? string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{Status} (expires {expiry:u})")
            : Status.ToString();
}
