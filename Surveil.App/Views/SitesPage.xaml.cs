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

    /// <summary>Discover runs WS-Discovery, then reports what it found and offers to port-scan the
    /// checked ranges for cameras that didn't announce themselves.</summary>
    private async void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        var summary = await Vm.DiscoverAsync();
        if (summary is null) return;  // cancelled or failed — the status bar already explains

        var found = summary.Value.Found;
        var unmapped = summary.Value.Unmapped;
        var checkedRanges = Vm.CheckedRangeCount;

        var message = found > 0
            ? $"Found {found} camera{(found == 1 ? "" : "s")} ({unmapped} unmapped)."
            : "No cameras announced themselves.";
        message += checkedRanges > 0
            ? $"\n\nAlso port-scan the {checkedRanges} checked range{(checkedRanges == 1 ? "" : "s")} for cameras that didn't announce themselves?"
            : "\n\nTick one or more ranges first to also port-scan them for cameras that didn't announce themselves.";

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Discovery complete",
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Scan checked ranges",
            CloseButtonText = "Not now",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = checkedRanges > 0,
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await Vm.ScanSelectedAsync();
    }

    /// <summary>Unpackaged WinUI 3 pickers must be associated with the window's HWND.</summary>
    private static void InitializeWithWindow(object picker)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
    }
}
