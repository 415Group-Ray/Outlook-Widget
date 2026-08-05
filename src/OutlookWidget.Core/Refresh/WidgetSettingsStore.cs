using System.Text.Json;
using System.Text.Json.Serialization;
using OutlookWidget.Core.Caching;

namespace OutlookWidget.Core.Refresh;

/// <summary>The user's rendering preferences.</summary>
/// <remarks>
/// One setting today. It is a record so adding a second is a compile-time change at every
/// construction site rather than a silently defaulted field.
/// </remarks>
public sealed record WidgetSettings
{
    /// <summary>
    /// Whether every widget size renders counts only, hiding sender and subject.
    /// </summary>
    /// <remarks>
    /// Section 8: this converts all sizes to counts-only. Turning it <em>on</em> reduces
    /// disclosure and is therefore a suppress-first operation; turning it off increases disclosure
    /// and commits normally, because there is no safety argument for pre-emptively revealing more.
    /// </remarks>
    public bool HideMessageDetails { get; init; }

    /// <summary>What a fresh install renders before anyone chooses.</summary>
    public static WidgetSettings Default { get; } = new();
}

/// <summary>Whether the stored settings could be established, and how.</summary>
public enum SettingsReadStatus
{
    /// <summary>The file was read and understood.</summary>
    Success,

    /// <summary>Nothing has been written yet. The defaults apply and that is not a failure.</summary>
    Absent,

    /// <summary>Present and unusable, or unreadable. The caller must fail closed.</summary>
    Unreadable,
}

/// <summary>The outcome of one settings read.</summary>
/// <param name="Status">Whether the value below is what the user chose.</param>
/// <param name="Settings">
/// The stored settings, the defaults when absent, or the most restrictive interpretation when
/// unreadable — never <see langword="null"/>, so a caller that ignores the status still cannot
/// disclose more than it should.
/// </param>
public readonly record struct SettingsReadResult(SettingsReadStatus Status, WidgetSettings Settings);

/// <summary>
/// Reads and writes the rendering preferences shared by the companion and the provider.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads fail closed, and that is the whole design.</b> A settings file that is present and
/// unparseable, or that cannot be opened, is answered with <c>HideMessageDetails = true</c> rather
/// than with the defaults. The asymmetry is deliberate: guessing "show details" when the user may
/// have asked to hide them discloses mailbox content on the strength of a corrupt file, while
/// guessing the other way costs a reader some subjects until the file is rewritten. This matches
/// the tombstone store's policy and deliberately does not match the lease record's, which must be
/// ignorable — the two are documented as separate policies precisely because their harms differ.
/// </para>
/// <para>
/// An <em>absent</em> file is not a failure and does not fail closed. A fresh install has written
/// nothing, and starting a first-run widget in counts-only would be indistinguishable from a
/// defect. Absence is unambiguous; corruption is not.
/// </para>
/// <para>
/// Written with temporary-file-plus-atomic-replace, so a failed or interrupted write cannot leave a
/// truncated file that the next read has to interpret. Writing is the companion's job; the provider
/// only ever reads.
/// </para>
/// </remarks>
public sealed class WidgetSettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private readonly CoordinationPaths _paths;

    public WidgetSettingsStore(CoordinationPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        _paths = paths;
    }

    /// <summary>The most restrictive reading, used whenever the stored value cannot be trusted.</summary>
    private static WidgetSettings FailClosed { get; } = new() { HideMessageDetails = true };

    /// <summary>Reads the stored settings, or says why it could not.</summary>
    public SettingsReadResult Read()
    {
        try
        {
            if (!File.Exists(_paths.SettingsFilePath))
            {
                return new SettingsReadResult(SettingsReadStatus.Absent, WidgetSettings.Default);
            }

            // Share both write and delete: the companion may be replacing this file at the moment
            // the provider reads it, and a sharing violation here must not become a disclosure
            // decision. Matches how every other cross-process reader in this project opens a file.
            using var stream = new FileStream(
                _paths.SettingsFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            WidgetSettings? settings = JsonSerializer.Deserialize<WidgetSettings>(
                stream,
                SerializerOptions);

            return settings is null
                ? new SettingsReadResult(SettingsReadStatus.Unreadable, FailClosed)
                : new SettingsReadResult(SettingsReadStatus.Success, settings);
        }
        catch (Exception exception) when (
            exception is IOException
                or UnauthorizedAccessException
                or JsonException
                or NotSupportedException)
        {
            // Every one of these means "the stored preference is unknown", and the answer to that
            // is the same regardless of which occurred.
            return new SettingsReadResult(SettingsReadStatus.Unreadable, FailClosed);
        }
    }

    /// <summary>
    /// Replaces the stored settings. <b>Not for production callers.</b>
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>internal</c> for the reason <c>SelectedAccountStore.Write</c> is: this is one step of an
    /// ordered operation, not the operation. Turning "hide message details" on must publish its
    /// tombstone first, and a caller that writes the file directly performs the mutation while
    /// skipping the ordering that makes it safe. Production goes through
    /// <see cref="SettingsChangeCoordinator"/>; this remains only so tests can seed a stored value.
    /// </para>
    /// <para>
    /// Throws on failure rather than reporting it, because every caller's response is to abandon
    /// the change and leave the previous state in place — and a caller that enabled
    /// "hide message details" has already published its tombstone, so an abandoned change stays
    /// suppressed rather than reverting to showing details.
    /// </para>
    /// </remarks>
    internal void Write(WidgetSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        Directory.CreateDirectory(_paths.RootDirectory);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(settings, SerializerOptions);

        File.WriteAllBytes(_paths.SettingsTempFilePath, payload);

        if (File.Exists(_paths.SettingsFilePath))
        {
            File.Replace(_paths.SettingsTempFilePath, _paths.SettingsFilePath, destinationBackupFileName: null);
        }
        else
        {
            File.Move(_paths.SettingsTempFilePath, _paths.SettingsFilePath);
        }
    }
}
