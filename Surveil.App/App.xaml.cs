using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.Services;

namespace Surveil.App;

/// <summary>Application entry point. Creates the single main window on launch and installs a
/// last-resort handler so an unexpected exception is logged and shown, not silently fatal.</summary>
public partial class App : Application
{
    private bool errorDialogOpen;

    /// <summary>The single main window, exposed so pages can obtain its HWND for file pickers
    /// (required for unpackaged WinUI 3 apps) and so the error handler can reach the UI thread.</summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        MainWindow = new MainWindow(AppServices.Create());
        MainWindow.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Write(e.Exception);
        // Keep the app alive — a line-of-business tool shouldn't die on one bad operation.
        e.Handled = true;
        ShowError("Something went wrong", e.Message);
    }

    /// <summary>Shows an error dialog on the UI thread. Safe to call from any thread; never throws.</summary>
    public static void ShowError(string title, string message)
    {
        if (MainWindow?.Content is not FrameworkElement root) return;
        root.DispatcherQueue.TryEnqueue(async () =>
        {
            if (Current is not App app || app.errorDialogOpen) return;  // one dialog at a time
            app.errorDialogOpen = true;
            try
            {
                var dialog = new ContentDialog
                {
                    Title = title,
                    Content = message,
                    CloseButtonText = "OK",
                    XamlRoot = root.XamlRoot,
                };
                await dialog.ShowAsync();
            }
            catch
            {
                // A failure to show the dialog must not itself crash the app.
            }
            finally
            {
                app.errorDialogOpen = false;
            }
        });
    }
}
