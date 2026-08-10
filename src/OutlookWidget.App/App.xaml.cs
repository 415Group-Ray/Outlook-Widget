using Microsoft.UI.Xaml;

namespace OutlookWidget.App;

/// <summary>The WinUI companion application.</summary>
public sealed partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] launchArguments = Environment.GetCommandLineArgs().Skip(1).ToArray();
        _window = Program.CreateMainWindow(launchArguments);
        Program.SetActivationHandler(ActivateMainWindow);
        _window.Activate();
    }

    private void ActivateMainWindow()
    {
        _window?.Activate();
    }
}
