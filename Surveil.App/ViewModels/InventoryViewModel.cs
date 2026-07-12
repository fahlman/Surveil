using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Read-only view of the saved camera inventory (cameras.json) with CSV export.</summary>
public sealed partial class InventoryViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private string summary = "";

    public ObservableCollection<CameraRecord> Cameras { get; } = new();

    public InventoryViewModel() => _ = LoadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        try
        {
            Cameras.Clear();
            var inventory = await session.Store.LoadInventoryAsync();
            foreach (var camera in inventory.Cameras.OrderBy(c => c.Building).ThenBy(c => c.Ip))
                Cameras.Add(camera);
            Summary = inventory.LastScan > 0
                ? $"{Cameras.Count} cameras · last scan {FormatTime(inventory.LastScan)}"
                : $"{Cameras.Count} cameras";
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

    /// <summary>Write the inventory to a CSV path chosen by the page.</summary>
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
