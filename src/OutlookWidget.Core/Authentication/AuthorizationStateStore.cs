using System.Text.Json;
using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Diagnostics;

namespace OutlookWidget.Core.Authentication;

/// <summary>
/// Carries a terminal interactive authorization outcome from the companion to the provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>This exists because one state is not rediscoverable, by design.</b> Classification is
/// phase-aware: during <em>silent</em> acquisition a consent failure is deliberately reported as
/// <see cref="TokenAcquisitionStatus.InteractionRequired"/>, because self-consent may still be available
/// and telling the user to find an administrator would be wrong. So the provider — which only ever
/// acquires silently — <em>cannot</em> conclude <see cref="TokenAcquisitionStatus.ApprovalRequired"/> on
/// its own, however many times it tries.
/// </para>
/// <para>
/// Only the companion learns it, by attempting interactive acquisition and being refused. Without
/// somewhere to put that, the knowledge died when the companion closed and a pinned card kept saying
/// "sign in required" to a user whose sign-in could never succeed — the exact conflation section 8
/// forbids, arrived at through a cross-process gap rather than a classification bug.
/// </para>
/// <para>
/// <b>Deliberately not DPAPI-protected, unlike the snapshot.</b> The record holds one enum member and a
/// timestamp. There is no mailbox content, no account, no tenant, and no token in it, so encrypting it
/// would imply a protection requirement that does not exist and suggest to a later reader that something
/// sensitive lives here. It is inside the package store, so uninstall removes it like everything else.
/// </para>
/// <para>
/// <b>Staleness is self-correcting, which is why there is no expiry.</b> If an administrator grants
/// consent afterwards, silent acquisition starts succeeding, and a caller only consults this record when
/// silent acquisition did <em>not</em> succeed. A stale record therefore cannot override a working token.
/// The companion also clears it explicitly on a successful acquisition.
/// </para>
/// </remarks>
public static class AuthorizationStateStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Records a terminal authorization outcome for one registration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Rejects anything other than <see cref="TokenAcquisitionStatus.ApprovalRequired"/>. The narrow
    /// contract is the point: this is not a general cache of the last authentication result, and widening
    /// it would let a transient failure persist as though it were a policy decision.
    /// </para>
    /// <para>
    /// <b>The registration is part of the record, and omitting it was a bug.</b> Consent is granted to a
    /// specific application in a specific tenant, so a record that names only a status is not about
    /// anything. State lives under package identity, which does not change when
    /// <c>authentication.json</c> is repointed at another client or tenant, so the old registration's
    /// refusal would have been applied to the new one — telling the user an administrator is required
    /// before self-consent had ever been attempted for it. That is the same harm as claiming
    /// approval-required on an ambiguous signal, arrived at by outliving its subject.
    /// </para>
    /// <para>
    /// The identifiers are written in the clear, which adds no exposure: they are not secrets, they
    /// appear in network traffic, and <c>authentication.json</c> already ships them beside both
    /// executables in this same package. Hashing them would only obscure a diagnostic.
    /// </para>
    /// </remarks>
    public static void Write(
        CoordinationPaths paths,
        AuthenticationOptions options,
        TokenAcquisitionStatus status,
        DateTimeOffset recordedAt,
        IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        if (status != TokenAcquisitionStatus.ApprovalRequired)
        {
            throw new ArgumentOutOfRangeException(
                nameof(status),
                "Only a terminal approval-required outcome is recorded here.");
        }

        try
        {
            Directory.CreateDirectory(paths.RootDirectory);

            string json = JsonSerializer.Serialize(
                new AuthorizationRecord
                {
                    Status = status.ToString(),
                    RecordedAtUtc = recordedAt,
                    TenantId = options.TenantId,
                    ClientId = options.ClientId,
                });

            File.WriteAllText(paths.AuthorizationStateFilePath, json);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Failing to record it means the provider shows the less specific sign-in-required card,
            // which is worse but not wrong. It is not worth failing a sign-in the user just completed.
            (logger ?? NullOperationalLogger.Instance)
                .Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
        }
    }

    /// <summary>Removes any recorded outcome. Safe when none exists.</summary>
    public static void Clear(CoordinationPaths paths, IOperationalLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(paths);

        try
        {
            File.Delete(paths.AuthorizationStateFilePath);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            (logger ?? NullOperationalLogger.Instance)
                .Record(OperationalEventId.StateCommitFailed, OperationalOutcome.Failed);
        }
    }

    /// <summary>
    /// Reads the recorded outcome, or <see langword="null"/> if there is none or it cannot be read.
    /// </summary>
    /// <remarks>
    /// <b>Absent and unreadable are the same answer, and that direction is deliberate.</b> Elsewhere in
    /// this product unreadable disclosure state fails closed, because the risk there is showing mail that
    /// should be hidden. Here the risk runs the other way: wrongly claiming approval-required asserts an
    /// administrator is needed and withdraws the retry a user may simply want. So an unreadable record
    /// yields no refinement, and the caller keeps the less specific state it already had.
    /// </remarks>
    public static TokenAcquisitionStatus? TryRead(
        CoordinationPaths paths,
        AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        try
        {
            string json = File.ReadAllText(paths.AuthorizationStateFilePath);

            AuthorizationRecord? record =
                JsonSerializer.Deserialize<AuthorizationRecord>(json, Options);

            if (record is null)
            {
                return null;
            }

            // A record from another registration says nothing about this one. Treated as absent rather
            // than deleted: this process may not be the one that owns it, and a read has no business
            // mutating state.
            if (record.TenantId != options.TenantId || record.ClientId != options.ClientId)
            {
                return null;
            }

            return Enum.TryParse(record.Status, out TokenAcquisitionStatus status)
                   && status == TokenAcquisitionStatus.ApprovalRequired
                ? status
                : null;
        }
        catch (Exception e) when (e is IOException
                                     or UnauthorizedAccessException
                                     or JsonException
                                     or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Refines a silent outcome using a recorded interactive one.
    /// </summary>
    /// <remarks>
    /// Only <see cref="TokenAcquisitionStatus.InteractionRequired"/> is refined. A successful acquisition
    /// is never overridden — that is what makes a stale record harmless — and no other failure is, because
    /// a broker problem or a transient error is not evidence about consent.
    /// </remarks>
    public static TokenAcquisitionStatus Refine(
        TokenAcquisitionStatus silentStatus,
        CoordinationPaths paths,
        AuthenticationOptions options)
    {
        ArgumentNullException.ThrowIfNull(paths);
        ArgumentNullException.ThrowIfNull(options);

        return silentStatus == TokenAcquisitionStatus.InteractionRequired
               && TryRead(paths, options) == TokenAcquisitionStatus.ApprovalRequired
            ? TokenAcquisitionStatus.ApprovalRequired
            : silentStatus;
    }

    private sealed class AuthorizationRecord
    {
        public string? Status { get; init; }

        public DateTimeOffset RecordedAtUtc { get; init; }

        /// <summary>
        /// The registration this outcome belongs to. Default (empty) on a record written before these
        /// fields existed, which therefore matches no real registration and is ignored — the correct
        /// outcome, since such a record cannot be attributed to one.
        /// </summary>
        public Guid TenantId { get; init; }

        public Guid ClientId { get; init; }
    }
}
