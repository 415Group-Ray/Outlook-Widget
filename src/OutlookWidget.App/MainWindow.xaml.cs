using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace OutlookWidget.App;

/// <summary>The user-facing WinUI companion window.</summary>
public sealed partial class MainWindow : Window
{
    private readonly CompanionCommands _commands;
    private PrivacyToggleAction _privacyToggleAction;
    private int _operationRunning;

    internal MainWindow(string initialStatus, CompanionCommands commands)
    {
        ArgumentNullException.ThrowIfNull(initialStatus);
        ArgumentNullException.ThrowIfNull(commands);

        InitializeComponent();
        _commands = commands;
        _privacyToggleAction = _commands.NextPrivacyToggleAction();
        PrivacyToggleButton.Content = _privacyToggleAction.Caption;
        StatusTextBox.Text = initialStatus;

        AppWindow.Resize(new SizeInt32(900, 780));
    }

    /// <summary>The real WinUI HWND supplied to MSAL/WAM.</summary>
    internal IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private async void SignInButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(_commands.SignIn, "Signing in…", "Sign-in");

    private async void SwitchAccountButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(_commands.SwitchAccount, "Opening the account picker…", "Account switch");

    private async void SignOutButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(_commands.SignOut, "Signing out…", "Sign-out");

    private async void ClearInterruptedButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(_commands.ClearInterruptedOperations, "Clearing interrupted operations…", "Recovery");

    private async void PrivacyToggleButton_Click(object sender, RoutedEventArgs e)
    {
        bool desiredHide = _privacyToggleAction.DesiredHideValue;
        await RunOperationAsync(
            () => _commands.TogglePrivacy(desiredHide),
            desiredHide ? "Hiding message details…" : "Showing message details…",
            "Privacy setting");
    }

    private async void ShowDiagnosticsButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(_commands.ShowDiagnostics, "Opening diagnostics…", "Diagnostics");

    private async void TestOutlookButton_Click(object sender, RoutedEventArgs e) =>
        await RunOperationAsync(_commands.TestOutlook, "Opening New Outlook…", "New Outlook test");

    private async Task RunOperationAsync(
        Func<Task<string>> operation,
        string progressMessage,
        string failurePrefix)
    {
        if (Interlocked.Exchange(ref _operationRunning, 1) == 1)
        {
            return;
        }

        SetOperationsEnabled(false);
        OperationProgress.IsActive = true;
        OperationInfoBar.Title = progressMessage;
        OperationInfoBar.Message = string.Empty;
        OperationInfoBar.Severity = InfoBarSeverity.Informational;
        OperationInfoBar.IsOpen = true;

        string report;
        bool failed = false;

        try
        {
            // Preserve the measured companion contract: brokered acquisition runs away from the UI
            // dispatcher while its modal surface is parented to this window's real HWND.
            report = await Task.Run(operation);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
        {
            // Type only. Authentication exception messages can contain account or server details.
            report = failurePrefix + " failed unexpectedly: " + exception.GetType().Name;
            failed = true;
        }

        StatusTextBox.Text = report;
        OperationInfoBar.Title = failed ? failurePrefix + " failed" : failurePrefix + " finished";
        OperationInfoBar.Message = FirstLine(report);
        OperationInfoBar.Severity = failed ? InfoBarSeverity.Error : InfoBarSeverity.Success;

        _privacyToggleAction = _commands.NextPrivacyToggleAction();
        PrivacyToggleButton.Content = _privacyToggleAction.Caption;

        OperationProgress.IsActive = false;
        SetOperationsEnabled(true);
        Volatile.Write(ref _operationRunning, 0);
    }

    private void SetOperationsEnabled(bool enabled)
    {
        SignInButton.IsEnabled = enabled;
        SwitchAccountButton.IsEnabled = enabled;
        SignOutButton.IsEnabled = enabled;
        PrivacyToggleButton.IsEnabled = enabled;
        ClearInterruptedButton.IsEnabled = enabled;
        ShowDiagnosticsButton.IsEnabled = enabled;
        TestOutlookButton.IsEnabled = enabled;
    }

    private static string FirstLine(string report)
    {
        int end = report.IndexOfAny(['\r', '\n']);
        return end < 0 ? report : report[..end];
    }
}

/// <summary>The already-reviewed operations surfaced by the WinUI window.</summary>
internal sealed record CompanionCommands(
    Func<Task<string>> SignIn,
    Func<Task<string>> SwitchAccount,
    Func<Task<string>> SignOut,
    Func<Task<string>> ClearInterruptedOperations,
    Func<bool, Task<string>> TogglePrivacy,
    Func<Task<string>> ShowDiagnostics,
    Func<Task<string>> TestOutlook,
    Func<PrivacyToggleAction> NextPrivacyToggleAction);
