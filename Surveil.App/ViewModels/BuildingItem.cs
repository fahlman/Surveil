using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>A building node: the parent of its CIDR ranges. Expands/collapses via a chevron, its
/// name locks after creation, and it owns its edit/delete/add-range commands (bound via x:Bind).
/// The Core <see cref="Building"/> stays UI-agnostic.</summary>
public sealed partial class BuildingItem : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private string notes;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool isExpanded = true;

    /// <summary>The CIDR ranges nested under this building.</summary>
    public ObservableCollection<NetworkRangeItem> Children { get; } = new();

    /// <summary>The collection this building lives in (for self-removal).</summary>
    public ObservableCollection<BuildingItem>? Owner { get; set; }

    public BuildingItem(Building building, ObservableCollection<BuildingItem>? owner = null)
    {
        name = building.Name;
        notes = building.Notes;
        isEditing = false;
        Owner = owner;
        foreach (var range in building.Ranges) Children.Add(new NetworkRangeItem(range, this));
    }

    /// <summary>A brand-new building starts editable so its name can be set.</summary>
    public BuildingItem(string name, ObservableCollection<BuildingItem>? owner = null)
    {
        this.name = name;
        notes = "";
        isEditing = true;
        Owner = owner;
    }

    public Building ToBuilding() => new()
    {
        Name = Name,
        Notes = Notes,
        Ranges = Children.Select(child => child.ToRange()).ToList(),
    };

    [RelayCommand] private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand] private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand] private void Remove() => Owner?.Remove(this);

    /// <summary>Add a new building right after this one (the building row's + button).</summary>
    [RelayCommand]
    private void AddBuilding()
    {
        if (Owner is null) return;
        var index = Owner.IndexOf(this);
        Owner.Insert(index + 1, new BuildingItem($"Building {Owner.Count + 1}", Owner));
    }

    /// <summary>Add a CIDR range under this building (used by the in-building "Add range").</summary>
    [RelayCommand]
    private void AddRange()
    {
        Children.Add(new NetworkRangeItem(this));
        IsExpanded = true;
    }
}
