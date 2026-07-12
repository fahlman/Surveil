using Microsoft.UI.Xaml.Controls;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();
        DataContext = new SettingsViewModel();
    }
}
