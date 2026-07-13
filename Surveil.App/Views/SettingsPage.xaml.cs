using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.Services;
using Surveil.App.ViewModels;
using Microsoft.UI.Xaml.Navigation;

namespace Surveil.App.Views;

public sealed partial class SettingsPage : Page
{
    private AppSession? session;

    public SettingsPage()
    {
        InitializeComponent();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is not AppServices services) return;
        session = services.Session;
        DataContext ??= new SettingsViewModel(session);
        PasswordInput.Password = session.Password;
    }

    private void OnPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (session is not null) session.Password = PasswordInput.Password;
    }
}
