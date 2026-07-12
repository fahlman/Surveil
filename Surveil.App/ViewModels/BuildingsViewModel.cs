using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.App.Services;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Editor for the building map (buildings → network ranges) that drives naming and
/// location tagging everywhere else. Persists to the same JSON store the CLI/core uses.</summary>
public sealed partial class BuildingsViewModel : ObservableObject
{
    private readonly AppSession session = AppSession.Current;

    [ObservableProperty] private BuildingItem? selectedBuilding;
    [ObservableProperty] private string statusMessage = "";
    [ObservableProperty] private bool hasError;

    /// <summary>Buildings shown in the list; the source of truth while editing.</summary>
    public ObservableCollection<BuildingItem> Buildings { get; } = new();

    /// <summary>Ranges of the currently selected building, editable in place.</summary>
    public ObservableCollection<NetworkRange> Ranges { get; } = new();

    public string DataDirectory => session.DataDirectory;

    public BuildingsViewModel() => Load();

    public void Load()
    {
        Buildings.Clear();
        foreach (var building in session.Config.Buildings) Buildings.Add(new BuildingItem(building));
        SelectedBuilding = Buildings.FirstOrDefault();
    }

    partial void OnSelectedBuildingChanged(BuildingItem? oldValue, BuildingItem? newValue)
    {
        if (oldValue is not null) CommitRanges(oldValue);
        Ranges.Clear();
        if (newValue is not null)
            foreach (var range in newValue.Ranges) Ranges.Add(range);
    }

    [RelayCommand]
    private void AddBuilding()
    {
        var building = new BuildingItem($"Building {Buildings.Count + 1}");
        Buildings.Add(building);
        SelectedBuilding = building;
    }

    [RelayCommand]
    private void RemoveBuilding()
    {
        if (SelectedBuilding is null) return;
        var index = Buildings.IndexOf(SelectedBuilding);
        Buildings.Remove(SelectedBuilding);
        SelectedBuilding = Buildings.Count == 0 ? null : Buildings[Math.Max(0, index - 1)];
    }

    [RelayCommand]
    private void AddRange()
    {
        if (SelectedBuilding is null) return;
        Ranges.Add(new NetworkRange { Name = "", Cidr = "" });
    }

    [RelayCommand]
    private void RemoveRange(NetworkRange? range)
    {
        if (range is not null) Ranges.Remove(range);
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        try
        {
            CommitCurrent();
            var config = new SurveilConfig { Buildings = Buildings.Select(b => b.ToBuilding()).ToList() };
            await session.Service.SaveConfigAsync(config);
            session.Config = config;
            HasError = false;
            StatusMessage = $"Saved {config.Buildings.Count} buildings to {session.DataDirectory}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
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
        }
    }

    /// <summary>Export the current map to a path chosen by the page.</summary>
    public async Task ExportAsync(string path)
    {
        try
        {
            CommitCurrent();
            session.Config = new SurveilConfig { Buildings = Buildings.Select(b => b.ToBuilding()).ToList() };
            await session.Service.SaveConfigAsync(session.Config);
            await session.Service.ExportConfigAsync(path);
            HasError = false;
            StatusMessage = $"Exported to {path}.";
        }
        catch (Exception ex)
        {
            HasError = true;
            StatusMessage = ex.Message;
        }
    }

    private void CommitCurrent()
    {
        if (SelectedBuilding is not null) CommitRanges(SelectedBuilding);
    }

    private void CommitRanges(BuildingItem building) => building.Ranges = Ranges.ToList();
}
