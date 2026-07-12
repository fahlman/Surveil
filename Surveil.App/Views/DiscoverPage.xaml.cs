using Microsoft.UI.Xaml.Controls;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

public sealed partial class DiscoverPage : Page
{
    public DiscoverPage()
    {
        InitializeComponent();
        DataContext = new DiscoverViewModel();
    }
}
