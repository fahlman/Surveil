using Microsoft.UI.Xaml;

namespace Surveil.App;

/// <summary>Application entry point. Creates the single main window on launch.</summary>
public partial class App : Application
{
    /// <summary>The single main window, exposed so pages can obtain its HWND for file pickers
    /// (required for unpackaged WinUI 3 apps).</summary>
    public static Window? MainWindow { get; private set; }

    public App() => InitializeComponent();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow();
        MainWindow.Activate();
    }
}
