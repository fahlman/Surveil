using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;
using Windows.ApplicationModel.DataTransfer;

namespace Surveil.App.ViewModels;

/// <summary>Read-only view of the saved camera inventory (cameras.json) with quick-filter,
/// clipboard copy, and CSV export.</summary>
public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;
    private readonly List<CameraRecord> all = new();

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string summary = "";
    [ObservableProperty] private string filter = "";

    public ObservableCollection<CameraRecord> Cameras { get; } = new();

    public InventoryViewModel() => _ = LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            var inventory = await session.Store.LoadInventoryAsync();
            all.Clear();
            all.AddRange(inventory.Cameras.OrderBy(c => c.Building).ThenBy(c => c.Ip));
            lastScan = inventory.LastScan;
            ApplyFilter();
            HasError = false;
            StatusMessage = "";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    private ulong lastScan;

    partial void OnFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var query = Filter.Trim();
        var matches = query.Length == 0
            ? all
            : all.Where(c => Contains(c.Ip, query) || Contains(c.Building, query) || Contains(c.Area, query));
        Cameras.Clear();
        foreach (var camera in matches) Cameras.Add(camera);

        var scan = lastScan > 0 ? $" · last scan {FormatTime(lastScan)}" : "";
        Summary = query.Length == 0
            ? $"{all.Count} cameras{scan}"
            : $"{Cameras.Count} of {all.Count} cameras{scan}";
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    private void CopyIps()
    {
        if (Cameras.Count == 0) return;
        var package = new DataPackage();
        package.SetText(string.Join(Environment.NewLine, Cameras.Select(c => c.Ip)));
        Clipboard.SetContent(package);
        StatusMessage = $"Copied {Cameras.Count} IP(s) to the clipboard.";
        HasError = false;
    }

    /// <summary>Write the currently shown (filtered) rows to a CSV path chosen by the page.</summary>
    public async Task ExportCsvAsync(string path)
    {
        try
        {
            var builder = new StringBuilder();
            builder.AppendLine(Csv.Line("IP", "Building", "Area", "FirstSeen", "LastSeen"));
            foreach (var camera in Cameras)
                builder.AppendLine(Csv.Line(camera.Ip, camera.Building, camera.Area,
                    FormatTime(camera.FirstSeen), FormatTime(camera.LastSeen)));
            await File.WriteAllTextAsync(path, builder.ToString());
            HasError = false;
            StatusMessage = $"Exported {Cameras.Count} rows to {path}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    private static string FormatTime(ulong unixSeconds) =>
        unixSeconds == 0 ? "" : DateTimeOffset.FromUnixTimeSeconds((long)unixSeconds).LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}
