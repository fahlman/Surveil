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
    private void AddBuilding()
    {
        var building = new BuildingItem($"Building {Buildings.Count + 1}", Buildings);
        Buildings.Add(building);
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

    private SurveilConfig ToConfig() =>
        new() { Buildings = Buildings.Select(building => building.ToBuilding()).ToList() };
}
