using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.Services;
using Surveil.App.Views;

namespace Surveil.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Title = "Surveil — ONVIF camera configuration";
    }

    private async void Nav_Loaded(object sender, RoutedEventArgs e)
    {
        // Load the site map once up front so every page starts from the same config.
        try { await AppSession.Current.LoadConfigAsync(); }
        catch { /* first run / missing file: keep the empty default config */ }

        Nav.SelectedItem = Nav.MenuItems[0];
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        var page = (item.Tag as string) switch
        {
            "sites" => typeof(SitesPage),
            "settings" => typeof(SettingsPage),
            _ => typeof(SitesPage),
        };

        ContentFrame.Navigate(page);
    }
}
