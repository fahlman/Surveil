using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.Services;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
        // The password lives in memory only (never in settings.json); mirror it to/from the session.
        Loaded += (_, _) => PasswordInput.Password = AppSession.Current.Password;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e) =>
        AppSession.Current.Password = PasswordInput.Password;
}
