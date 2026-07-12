using Microsoft.UI.Xaml.Controls;
using Surveil.App.ViewModels;

namespace Surveil.App.Views;

public sealed partial class ScanPage : Page
{
    public ScanPage()
    {
        InitializeComponent();
        DataContext = new ScanViewModel();
    }
}
