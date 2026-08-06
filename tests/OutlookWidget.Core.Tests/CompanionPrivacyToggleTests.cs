using System.Text.RegularExpressions;
using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>
/// The companion's privacy toggle, checked from source.
/// </summary>
/// <remarks>
/// <para>
/// The test project cannot reference <c>OutlookWidget.App</c>, for the reason it cannot reference the
/// provider, so these are source-level checks. They exist because both defects they cover were
/// disclosure bugs in a button: one whose label described the opposite of its effect, and one that
/// repaired a damaged settings file by enabling disclosure.
/// </para>
/// <para>
/// Both were the same underlying mistake the core has now made four times — treating the
/// substituted fail-closed value as a record of what the user chose. It is worth a test at each
/// layer that can commit it.
/// </para>
/// </remarks>
public sealed class CompanionPrivacyToggleTests
{
    private static string ProgramSource() =>
        File.ReadAllText(Path.Combine(RepositorySources.AppSourceDirectory, "Program.cs"));

    private static string WindowSource() =>
        File.ReadAllText(Path.Combine(RepositorySources.AppSourceDirectory, "CompanionWindow.cs"));

    [Fact]
    public void An_unreadable_setting_makes_the_toggle_offer_to_hide()
    {
        // Read() substitutes HideMessageDetails = true so a renderer that ignores the status still
        // withholds. Negating that substitution asks to SHOW details — repairing a damaged file by
        // enabling disclosure on the strength of a value nobody stored. The decision must branch on
        // the status instead.
        string source = ProgramSource();

        Assert.Contains(
            "stored.Status == SettingsReadStatus.Unreadable",
            source,
            StringComparison.Ordinal);

        // And the toggle must never derive its value by negating the read result, which is the
        // shape the defect had.
        Assert.False(
            Regex.IsMatch(source, @"!\s*\w*settings?\w*\.Read\(\)\.Settings\.HideMessageDetails"),
            "The desired value must come from the status-aware decision, not from negating the "
                + "substituted fail-closed value.");
    }

    [Fact]
    public void The_caption_and_the_action_come_from_one_decision()
    {
        // A caption and an effect computed separately can disagree, and did: the button was created
        // with a fixed caption and only relabelled after an operation finished, so reopening the
        // companion with the setting already on showed "Hide message details" over a click that
        // would reveal them.
        string source = ProgramSource();

        Assert.Contains("NextPrivacyToggleAction(state).Caption", source, StringComparison.Ordinal);
        Assert.Contains(
            "NextPrivacyToggleAction(state).DesiredHideValue",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_toggle_is_created_with_the_caption_the_store_implies()
    {
        // The other half of the same defect: asking the callback only after the first operation
        // leaves the initial label wrong for a whole session.
        string source = WindowSource();

        Assert.Contains("_privacyToggleCaption?.Invoke()", source, StringComparison.Ordinal);

        int callbackUse = source.IndexOf("string privacyCaption =", StringComparison.Ordinal);
        int buttonCreated = source.IndexOf("_privacyToggleButton = CreateWindowExW", StringComparison.Ordinal);

        Assert.True(callbackUse > 0, "The initial caption should be read from the callback.");
        Assert.True(
            callbackUse < buttonCreated,
            "The caption must be established before the button is created, not after the first "
                + "operation completes.");
    }

    [Fact]
    public void The_companion_changes_the_setting_only_through_the_coordinator()
    {
        // WidgetSettingsStore.Write is internal precisely so this cannot happen, but the companion
        // is a separate assembly and the check is cheap. Writing the file directly would perform the
        // mutation while skipping the suppress-first ordering that makes enabling it safe.
        foreach (string file in RepositorySources.AppSourceFiles())
        {
            string code = File.ReadAllText(file);

            Assert.False(
                Regex.IsMatch(code, @"\.Write\(new WidgetSettings"),
                $"{Path.GetFileName(file)} writes settings directly instead of using "
                    + "SettingsChangeCoordinator.");
        }
    }
}
