using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Surveil.App.ViewModels;
using Windows.Storage.Pickers;

namespace Surveil.App.Views;

public sealed partial class InventoryPage : Page
{
    private InventoryViewModel Vm => (InventoryViewModel)DataContext;

    public InventoryPage()
    {
        InitializeComponent();
        DataContext = new InventoryViewModel();
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = "surveil-inventory",
        };
        picker.FileTypeChoices.Add("CSV", new List<string> { ".csv" });
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
        var file = await picker.PickSaveFileAsync();
        if (file is not null) await Vm.ExportCsvAsync(file.Path);
    }
}
