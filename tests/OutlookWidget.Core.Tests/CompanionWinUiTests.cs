using OutlookWidget.Core.Tests.TestInfrastructure;

namespace OutlookWidget.Core.Tests;

/// <summary>Source-level boundaries for the Phase 2 WinUI companion.</summary>
public sealed class CompanionWinUiTests
{
    private static string AppSource(string fileName) =>
        File.ReadAllText(Path.Combine(RepositorySources.AppSourceDirectory, fileName));

    [Fact]
    public void The_companion_is_a_WinUI_app_with_a_custom_single_instance_entry_point()
    {
        string project = AppSource("OutlookWidget.App.csproj");
        string program = AppSource("Program.cs");

        Assert.Contains("<UseWinUI>true</UseWinUI>", project, StringComparison.Ordinal);
        Assert.Contains("DISABLE_XAML_GENERATED_MAIN", project, StringComparison.Ordinal);
        Assert.Contains("AppInstance.FindOrRegisterForKey", program, StringComparison.Ordinal);
        Assert.Contains("RedirectActivationToAsync", program, StringComparison.Ordinal);
        Assert.Contains("Application.Start", program, StringComparison.Ordinal);
    }

    [Fact]
    public void WAM_receives_the_real_WinUI_window_handle()
    {
        string program = AppSource("Program.cs");
        string window = AppSource("MainWindow.xaml.cs");

        Assert.Contains("_parentWindow = () => window.Handle;", program, StringComparison.Ordinal);
        Assert.Contains(".CreateAsync(options, paths, _parentWindow)", program, StringComparison.Ordinal);
        Assert.Contains("WindowNative.GetWindowHandle(this)", window, StringComparison.Ordinal);
        Assert.DoesNotContain("AcquireTokenInteractive", window, StringComparison.Ordinal);
    }

    [Fact]
    public void The_user_facing_actions_are_present_and_serialized_together()
    {
        string markup = AppSource("MainWindow.xaml");
        string window = AppSource("MainWindow.xaml.cs");

        foreach (string action in new[]
                 {
                     "Sign in",
                     "Switch account",
                     "Sign out",
                     "Clear interrupted operations",
                     "Show diagnostics",
                     "Test New Outlook",
                 })
        {
            Assert.Contains(action, markup, StringComparison.Ordinal);
        }

        Assert.Contains("Interlocked.Exchange(ref _operationRunning, 1)", window, StringComparison.Ordinal);
        Assert.Contains("SetOperationsEnabled(false)", window, StringComparison.Ordinal);
        Assert.Contains("SetOperationsEnabled(true)", window, StringComparison.Ordinal);
    }

    [Fact]
    public void The_widget_diagnostic_footer_has_moved_off_the_card()
    {
        string card = File.ReadAllText(
            Path.Combine(RepositorySources.ProviderSourceDirectory, "Cards", "InboxCard.cs"));

        Assert.DoesNotContain("diagnosticInstance", card, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnosticState", card, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("diagnosticWidgetId", card, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("WidgetSize.Large => 5", card, StringComparison.Ordinal);
    }
}
