using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The standing privacy setting, and how it combines with in-flight suppression.
/// </summary>
/// <remarks>
/// The setting is the second thing that can reduce what a widget displays, and the first that
/// persists. Its read policy is therefore a disclosure decision rather than a preferences one, which
/// is what these tests are about.
/// </remarks>
public sealed class WidgetSettingsTests : IDisposable
{
    private readonly CoordinationFixture _fixture = new();
    private readonly CoordinationPaths _paths;
    private readonly WidgetSettingsStore _store;

    public WidgetSettingsTests()
    {
        _paths = _fixture.Paths;
        _store = new WidgetSettingsStore(_paths);
    }

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void An_absent_file_is_the_defaults_and_not_a_failure()
    {
        // A fresh install has written nothing. Starting a first-run widget in counts-only would be
        // indistinguishable from a defect, and absence is unambiguous in a way corruption is not.
        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Absent, result.Status);
        Assert.False(result.Settings.HideMessageDetails);
    }

    [Fact]
    public void A_written_setting_round_trips()
    {
        _store.Write(new WidgetSettings { HideMessageDetails = true });

        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Success, result.Status);
        Assert.True(result.Settings.HideMessageDetails);
    }

    [Fact]
    public void A_write_replaces_a_previous_value()
    {
        _store.Write(new WidgetSettings { HideMessageDetails = true });
        _store.Write(new WidgetSettings { HideMessageDetails = false });

        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Success, result.Status);
        Assert.False(result.Settings.HideMessageDetails);
    }

    [Fact]
    public void A_corrupt_file_hides_details_rather_than_revealing_them()
    {
        // The asymmetry that matters. Guessing "show details" on the strength of a corrupt file
        // discloses mailbox content; guessing the other way costs some subjects until it is
        // rewritten.
        File.WriteAllText(_paths.SettingsFilePath, "{ this is not json");

        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Unreadable, result.Status);
        Assert.True(result.Settings.HideMessageDetails);
    }

    [Fact]
    public void A_settings_path_that_cannot_be_read_hides_details_rather_than_reading_as_absent()
    {
        // The hole an existence pre-check opened. File.Exists reports false for every failure it
        // meets — ACL damage, a path it cannot traverse, a transient error — so a present but
        // unreadable file was classified Absent, Absent meant the defaults, and the defaults meant
        // full disclosure. One convenience call inverted the entire fail-closed policy.
        //
        // A directory standing where the file belongs reproduces that shape: something is there,
        // and it cannot be read as a settings file.
        Directory.CreateDirectory(_paths.SettingsFilePath);

        try
        {
            SettingsReadResult result = _store.Read();

            Assert.Equal(SettingsReadStatus.Unreadable, result.Status);
            Assert.True(result.Settings.HideMessageDetails);

            // And the effective mode follows, which is the part that actually protects a mailbox.
            var policy = new DisclosurePolicy(new DisclosureTombstoneStore(_paths), _store);
            Assert.Equal(DisclosureMode.CountsOnly, policy.GetEffectiveMode());
        }
        finally
        {
            Directory.Delete(_paths.SettingsFilePath, recursive: true);
        }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{ \"somethingElse\": true }")]
    [InlineData("{ \"HideMessageDetails\": ")]
    public void A_record_without_the_privacy_flag_hides_details(string content)
    {
        // Valid JSON that says nothing about the preference. A bool defaults to false, so before
        // the property was marked required this deserialized cleanly into "show everything" and was
        // reported as a known value — a corrupt file granted full disclosure while looking healthy.
        // The third case is a truncated write, which is how such a file would actually arise.
        File.WriteAllText(_paths.SettingsFilePath, content);

        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Unreadable, result.Status);
        Assert.True(result.Settings.HideMessageDetails);
    }

    [Fact]
    public void An_empty_file_hides_details()
    {
        // Deserializing an empty document yields null rather than throwing, so this reaches the
        // fail-closed branch by a different route than a syntax error.
        File.WriteAllText(_paths.SettingsFilePath, string.Empty);

        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Unreadable, result.Status);
        Assert.True(result.Settings.HideMessageDetails);
    }

    [Fact]
    public void A_reader_can_open_the_file_while_it_is_held_open_for_writing()
    {
        // The provider reads while the companion may be replacing. A sharing violation here would
        // become a disclosure decision, which is the wrong reason to hide someone's mail.
        _store.Write(new WidgetSettings { HideMessageDetails = true });

        using var held = new FileStream(
            _paths.SettingsFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.ReadWrite | FileShare.Delete);

        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Success, result.Status);
    }

    [Fact]
    public void The_setting_alone_reduces_delivery_to_counts_only()
    {
        var tombstones = new DisclosureTombstoneStore(_paths);
        var policy = new DisclosurePolicy(tombstones, _store);

        Assert.Equal(DisclosureMode.Full, policy.GetEffectiveMode());

        _store.Write(new WidgetSettings { HideMessageDetails = true });

        Assert.Equal(DisclosureMode.CountsOnly, policy.GetEffectiveMode());
    }

    [Fact]
    public void An_in_flight_operation_cannot_be_weakened_by_the_setting()
    {
        // A signed-out tombstone outranks a preference that says counts-only, and equally outranks
        // one that says show everything. Neither source may weaken the other, which is why the
        // combination is a maximum rather than a precedence rule.
        var tombstones = new DisclosureTombstoneStore(_paths);
        var policy = new DisclosurePolicy(tombstones, _store);

        _store.Write(new WidgetSettings { HideMessageDetails = false });

        // Left in force deliberately: an operation that has published its marker and not yet
        // committed is exactly the state being tested.
        tombstones.Suppress(DisclosureMode.SignedOut);

        Assert.Equal(DisclosureMode.SignedOut, policy.GetEffectiveMode());
    }

    [Fact]
    public void An_unreadable_setting_hides_details_without_claiming_a_sign_out()
    {
        // Both sources fail closed and they fail to different modes on purpose. Escalating a corrupt
        // preferences file to a signed-out card would tell the user they are signed out when they
        // are not.
        File.WriteAllText(_paths.SettingsFilePath, "{ broken");

        var policy = new DisclosurePolicy(new DisclosureTombstoneStore(_paths), _store);

        Assert.Equal(DisclosureMode.CountsOnly, policy.GetEffectiveMode());
    }

    [Fact]
    public void Settings_survive_a_cleared_cache()
    {
        // The reason this is its own file rather than part of the protected envelope section 3
        // describes. Clear writes a header and no payload, so settings stored there would be erased
        // by a logout — and "hide message details" silently returning to off after signing in again
        // is a privacy regression produced by storage layout.
        _store.Write(new WidgetSettings { HideMessageDetails = true });

        // Erasing the protected envelope outright — a stronger statement than calling Clear, which
        // leaves a header behind. Settings must be unaffected either way.
        Assert.NotEqual(_paths.SettingsFilePath, _paths.StateFilePath);
        File.Delete(_paths.StateFilePath);

        SettingsReadResult result = _store.Read();

        Assert.Equal(SettingsReadStatus.Success, result.Status);
        Assert.True(result.Settings.HideMessageDetails);
    }
}
