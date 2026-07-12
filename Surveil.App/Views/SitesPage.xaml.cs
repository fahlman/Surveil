using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.ViewModels;
using Windows.Storage.Pickers;

namespace Surveil.App.Views;

public sealed partial class SitesPage : Page
{
    private SitesViewModel Vm => (SitesViewModel)DataContext;

    public SitesPage()
    {
        InitializeComponent();
        DataContext = new SitesViewModel();
    }

    private async void OnImportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".json");
        InitializeWithWindow(picker);
        var file = await picker.PickSingleFileAsync();
        if (file is not null) await Vm.ImportAsync(file.Path);
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "sites",
        };
        picker.FileTypeChoices.Add("JSON", new List<string> { ".json" });
        InitializeWithWindow(picker);
        var file = await picker.PickSaveFileAsync();
        if (file is not null) await Vm.ExportAsync(file.Path);
    }

    /// <summary>Unpackaged WinUI 3 pickers must be associated with the window's HWND.</summary>
    private static void InitializeWithWindow(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
