using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>A building node: parent of its CIDR ranges. Expands/collapses via a chevron, name locks
/// after creation, and its (tri-state) checkbox selects/deselects all of its CIDRs.</summary>
public sealed partial class BuildingItem : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private string notes;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool isExpanded = true;

    /// <summary>Null = some (but not all) CIDRs selected (indeterminate); true = all; false = none.</summary>
    [ObservableProperty] private bool? isSelected = false;

    private bool syncing;

    /// <summary>The CIDR ranges nested under this building.</summary>
    public ObservableCollection<NetworkRangeItem> Children { get; } = new();

    /// <summary>The collection this building lives in (for self-removal / add-sibling).</summary>
    public ObservableCollection<BuildingItem>? Owner { get; set; }

    public BuildingItem(Building building, ObservableCollection<BuildingItem>? owner = null)
    {
        name = building.Name;
        notes = building.Notes;
        isEditing = false;
        Owner = owner;
        foreach (var range in building.Ranges) Children.Add(new NetworkRangeItem(range, this));
    }

    /// <summary>A brand-new building starts editable, expanded, and with one empty CIDR to fill in.</summary>
    public BuildingItem(string name, ObservableCollection<BuildingItem>? owner = null)
    {
        this.name = name;
        notes = "";
        isEditing = true;
        Owner = owner;
        Children.Add(new NetworkRangeItem(this));
    }

    public Building ToBuilding() => new()
    {
        Name = Name,
        Notes = Notes,
        Ranges = Children.Select(child => child.ToRange()).ToList(),
    };

    // Checking the building selects/deselects every CIDR; a child changing rolls the state back up.
    partial void OnIsSelectedChanged(bool? value)
    {
        if (syncing || value is not bool all) return;
        syncing = true;
        foreach (var child in Children) child.IsSelected = all;
        syncing = false;
    }

    /// <summary>Recompute this building's checkbox from its children (all / none / some).</summary>
    public void RefreshSelection()
    {
        if (syncing) return;
        syncing = true;
        IsSelected = Children.Count == 0 ? false
            : Children.All(c => c.IsSelected) ? true
            : Children.Any(c => c.IsSelected) ? null
            : false;
        syncing = false;
    }

    [RelayCommand] private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand] private void ToggleExpand() => IsExpanded = !IsExpanded;

    [RelayCommand]
    private void Remove()
    {
        if (Owner is null) return;
        Owner.Remove(this);
        // Never leave the map with no buildings — recreate an empty one.
        if (Owner.Count == 0) Owner.Add(new BuildingItem("Building 1", Owner));
    }

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
        RefreshSelection();
    }
}
