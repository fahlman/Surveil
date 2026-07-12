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

    /// <summary>Ranges of the currently selected building. Each row locks after creation and is
    /// re-editable via its edit button.</summary>
    public ObservableCollection<NetworkRangeItem> Ranges { get; } = new();

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
            foreach (var range in newValue.Ranges) Ranges.Add(new NetworkRangeItem(range, editing: false));
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

    /// <summary>Lock the building name after editing, or unlock it to edit again.</summary>
    [RelayCommand]
    private void ToggleEditBuilding()
    {
        if (SelectedBuilding is not null) SelectedBuilding.IsEditing = !SelectedBuilding.IsEditing;
    }

    /// <summary>Add the first range (used by the empty-state button).</summary>
    [RelayCommand]
    private void AddRange()
    {
        if (SelectedBuilding is null) return;
        Ranges.Add(new NetworkRangeItem());
    }

    /// <summary>Add a new (editable) range immediately after the given one.</summary>
    [RelayCommand]
    private void AddRangeAfter(NetworkRangeItem? range)
    {
        if (SelectedBuilding is null) return;
        var index = range is null ? Ranges.Count - 1 : Ranges.IndexOf(range);
        Ranges.Insert(index + 1, new NetworkRangeItem());
    }

    [RelayCommand]
    private void RemoveRange(NetworkRangeItem? range)
    {
        if (range is not null) Ranges.Remove(range);
    }

    /// <summary>Lock a row after editing, or unlock it to edit again.</summary>
    [RelayCommand]
    private void ToggleEditRange(NetworkRangeItem? range)
    {
        if (range is not null) range.IsEditing = !range.IsEditing;
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
            AppLog.Write(ex);
        }
    }

    private void CommitCurrent()
    {
        if (SelectedBuilding is not null) CommitRanges(SelectedBuilding);
    }

    private void CommitRanges(BuildingItem building) => building.Ranges = Ranges.Select(r => r.ToRange()).ToList();
}
