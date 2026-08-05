using OutlookWidget.Core.Caching;
using OutlookWidget.Core.Refresh;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The ordering a settings change follows, which depends on its direction.
/// </summary>
/// <remarks>
/// Turning "hide message details" on reduces disclosure and is therefore a suppress-first operation
/// like logout and account switch. Turning it off increases disclosure and commits normally. These
/// tests are about that asymmetry and about what is left behind when a step fails.
/// </remarks>
public sealed class SettingsChangeTests : IDisposable
{
    private readonly CoordinationFixture _fixture = new();
    private readonly CoordinationPaths _paths;
    private readonly WidgetSettingsStore _store;
    private readonly DisclosureTombstoneStore _tombstones;
    private int _signals;

    public SettingsChangeTests()
    {
        _paths = _fixture.Paths;
        _store = new WidgetSettingsStore(_paths);
        _tombstones = new DisclosureTombstoneStore(_paths);
    }

    public void Dispose() => _fixture.Dispose();

    private SettingsChangeCoordinator Coordinator() =>
        new(_store, _tombstones, logger: null, signalStateChanged: () =>
        {
            _signals++;
            return true;
        });

    private DisclosureMode EffectiveMode() =>
        new DisclosurePolicy(_tombstones, _store).GetEffectiveMode();

    [Fact]
    public void Hiding_details_stores_the_setting_and_clears_its_tombstone()
    {
        SettingsChangeResult result = Coordinator().Apply(new WidgetSettings { HideMessageDetails = true });

        Assert.Equal(SettingsChangeOutcome.Applied, result.Outcome);
        Assert.False(result.DetailsRemainHidden);

        // Counts-only now comes from the stored setting rather than from a leftover marker.
        Assert.Equal(DisclosureMode.CountsOnly, EffectiveMode());
        Assert.Equal(DisclosureMode.Full, _tombstones.GetEffectiveMode());
        Assert.Equal(1, _signals);
    }

    [Fact]
    public void Revealing_details_again_returns_the_widget_to_full()
    {
        SettingsChangeCoordinator coordinator = Coordinator();

        coordinator.Apply(new WidgetSettings { HideMessageDetails = true });
        SettingsChangeResult result = coordinator.Apply(new WidgetSettings { HideMessageDetails = false });

        Assert.Equal(SettingsChangeOutcome.Applied, result.Outcome);
        Assert.Equal(DisclosureMode.Full, EffectiveMode());
    }

    [Fact]
    public void An_unchanged_setting_writes_nothing_and_suppresses_nothing()
    {
        SettingsChangeResult result = Coordinator().Apply(new WidgetSettings { HideMessageDetails = false });

        Assert.Equal(SettingsChangeOutcome.Unchanged, result.Outcome);
        Assert.Equal(DisclosureMode.Full, EffectiveMode());
        Assert.Equal(0, _signals);
    }

    [Fact]
    public void Hiding_details_when_the_stored_value_is_unreadable_records_the_choice()
    {
        // An unreadable file renders counts-only, which once looked like "already hidden, nothing to
        // do". It is not: the stored preference is unknown, and reporting the request Unchanged
        // dropped it — so a transient failure, or a user asking precisely because the file is
        // damaged, would have let details reappear the moment it became readable again.
        File.WriteAllText(_paths.SettingsFilePath, "{ broken");

        SettingsChangeResult result = Coordinator().Apply(new WidgetSettings { HideMessageDetails = true });

        Assert.Equal(SettingsChangeOutcome.Applied, result.Outcome);

        // Written, so the file is repaired and the preference now survives a readable read.
        SettingsReadResult stored = _store.Read();
        Assert.Equal(SettingsReadStatus.Success, stored.Status);
        Assert.True(stored.Settings.HideMessageDetails);
        Assert.Equal(DisclosureMode.CountsOnly, EffectiveMode());
    }

    [Fact]
    public void Revealing_details_when_the_stored_value_is_unreadable_repairs_the_file()
    {
        // The same rule in the other direction. The user's explicit choice is honoured and the
        // damaged file is replaced, rather than the request being swallowed because the unreadable
        // state happened to render the way a hidden setting would.
        File.WriteAllText(_paths.SettingsFilePath, "{ broken");

        SettingsChangeResult result = Coordinator().Apply(new WidgetSettings { HideMessageDetails = false });

        Assert.Equal(SettingsChangeOutcome.Applied, result.Outcome);

        SettingsReadResult stored = _store.Read();
        Assert.Equal(SettingsReadStatus.Success, stored.Status);
        Assert.False(stored.Settings.HideMessageDetails);
        Assert.Equal(DisclosureMode.Full, EffectiveMode());
    }

    [Fact]
    public void A_failed_write_while_hiding_leaves_the_widget_hidden()
    {
        // The fail-closed direction. The user asked for details to be hidden; the write failed; the
        // tombstone stays, so they are hidden anyway — by the safe path rather than the committed
        // one. The operation reports failure even though the visible result is what was asked for.
        using var block = HoldSettingsPathAsDirectory();

        SettingsChangeResult result = Coordinator().Apply(new WidgetSettings { HideMessageDetails = true });

        Assert.Equal(SettingsChangeOutcome.WriteFailed, result.Outcome);
        Assert.True(result.DetailsRemainHidden);
        Assert.False(result.IsApplied);
        Assert.Equal(DisclosureMode.CountsOnly, EffectiveMode());
    }

    [Fact]
    public void A_failed_write_while_revealing_suppresses_nothing_new()
    {
        // The other direction fails differently and must: nothing was published, so nothing is
        // stuck. The previous setting simply stands.
        Coordinator().Apply(new WidgetSettings { HideMessageDetails = true });

        using var block = HoldSettingsPathAsDirectory();

        SettingsChangeResult result = Coordinator().Apply(new WidgetSettings { HideMessageDetails = false });

        Assert.Equal(SettingsChangeOutcome.WriteFailed, result.Outcome);
        Assert.Equal(DisclosureMode.Full, _tombstones.GetEffectiveMode());
    }

    [Fact]
    public void The_provider_is_signalled_so_it_re_renders_without_a_new_refresh()
    {
        // A settings change alters what may be rendered rather than what is cached, so convergence
        // depends on the provider being told. The cache generation does not move.
        var cache = new ProtectedCache(_paths);
        long before = cache.ReadGeneration();

        Coordinator().Apply(new WidgetSettings { HideMessageDetails = true });

        Assert.Equal(1, _signals);
        Assert.Equal(before, cache.ReadGeneration());
    }

    /// <summary>
    /// Makes the settings write fail by occupying its temporary path with a directory, which
    /// <c>File.WriteAllBytes</c> cannot replace.
    /// </summary>
    private DirectoryRemover HoldSettingsPathAsDirectory()
    {
        Directory.CreateDirectory(_paths.SettingsTempFilePath);
        return new DirectoryRemover(_paths.SettingsTempFilePath);
    }

    private sealed class DirectoryRemover(string path) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                Directory.Delete(path, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Teardown only.
            }
        }
    }
}
