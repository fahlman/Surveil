using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>The building map as a hierarchical editor: buildings (parents) each hold their CIDR
/// ranges (children). Per-node edit/delete/add live on the nodes themselves; this view model owns
/// the root list plus save / import / export.</summary>
public sealed partial class BuildingsViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;

    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;
    [ObservableProperty] private bool isScanning;
    [ObservableProperty] private double scanProgressValue;
    [ObservableProperty] private double scanProgressMaximum = 1;

    private CancellationTokenSource? scanCts;

    public ObservableCollection<BuildingItem> Buildings { get; } = new();

    public string DataDirectory => session.DataDirectory;

    public BuildingsViewModel() => Load();

    public void Load()
    {
        Buildings.Clear();
        foreach (var building in session.Config.Buildings)
            Buildings.Add(new BuildingItem(building, Buildings));
        // There is always at least one building — a fresh/empty map starts with an empty one.
        if (Buildings.Count == 0) Buildings.Add(new BuildingItem("Building 1", Buildings));
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            var config = ToConfig();
            await session.Service.SaveConfigAsync(config);
            session.Config = config;
            HasError = false;
            StatusMessage = $"Saved {config.Buildings.Count} buildings to {session.DataDirectory}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    /// <summary>Import a config file chosen by the page; replaces the current in-memory map.</summary>
    public async Task ImportAsync(string path)
    {
        try
        {
            var config = await session.Service.ImportConfigAsync(path);
            session.Config = config;
            Load();
            HasError = false;
            StatusMessage = $"Imported {config.Buildings.Count} buildings from {path}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    /// <summary>Export the current map to a path chosen by the page.</summary>
    public async Task ExportAsync(string path)
    {
        try
        {
            session.Config = ToConfig();
            await session.Service.SaveConfigAsync(session.Config);
            await session.Service.ExportConfigAsync(path);
            HasError = false;
            StatusMessage = $"Exported to {path}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
    }

    /// <summary>Scan the checked CIDRs and populate the cameras found in each one under it.</summary>
    [RelayCommand]
    private async Task ScanSelectedAsync()
    {
        if (IsScanning) return;

        var selected = Buildings.SelectMany(b => b.Children)
            .Where(r => r.IsSelected && !string.IsNullOrWhiteSpace(r.Cidr))
            .ToList();
        if (selected.Count == 0)
        {
            HasError = true;
            StatusMessage = "Check one or more CIDRs to scan.";
            return;
        }

        // Expand each selected CIDR to the set of addresses it covers (skip invalid ranges).
        var addressesByRange = new Dictionary<NetworkRangeItem, HashSet<string>>();
        foreach (var range in selected)
        {
            try
            {
                addressesByRange[range] = NetworkRanges.ExpandPrivate(new[] { range.Cidr })
                    .Select(ip => ip.ToString()).ToHashSet();
            }
            catch (Exception ex)
            {
                AppLog.Write(ex);
            }
        }
        if (addressesByRange.Count == 0)
        {
            HasError = true;
            StatusMessage = "None of the checked CIDRs are valid private ranges.";
            return;
        }

        foreach (var range in addressesByRange.Keys) range.Cameras.Clear();
        var targets = addressesByRange.Values.SelectMany(set => set).Distinct().ToArray();

        IsScanning = true;
        HasError = false;
        ScanProgressValue = 0;
        ScanProgressMaximum = Math.Max(1, targets.Length);
        StatusMessage = "Scanning…";
        scanCts = new CancellationTokenSource();
        var settings = session.Settings;

        var progress = new Progress<ScanProgress>(p =>
        {
            ScanProgressMaximum = Math.Max(1, p.Total);
            ScanProgressValue = p.Scanned;
            StatusMessage = $"Scanned {p.Scanned}/{p.Total} — {p.Found} responding";
        });

        try
        {
            var statuses = await session.Service.ScanAsync(targets, settings.DefaultPort,
                settings.DefaultConcurrency, TimeSpan.FromMilliseconds(settings.DefaultTimeoutMs),
                progress, scanCts.Token);

            var found = 0;
            foreach (var camera in statuses.Where(s => s.Status != "offline"))
            {
                var range = addressesByRange.FirstOrDefault(kv => kv.Value.Contains(camera.Ip)).Key;
                if (range is null) continue;
                range.Cameras.Add(camera);
                found++;
            }
            StatusMessage = $"Found {found} camera(s) across {addressesByRange.Count} CIDR(s).";
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Scan cancelled.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
            AppLog.Write(ex);
        }
        finally
        {
            IsScanning = false;
            scanCts?.Dispose();
            scanCts = null;
        }
    }

    [RelayCommand]
    private void CancelScan() => scanCts?.Cancel();

    private SurveilConfig ToConfig() =>
        new() { Buildings = Buildings.Select(building => building.ToBuilding()).ToList() };
}
