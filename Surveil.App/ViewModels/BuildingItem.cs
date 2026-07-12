using CommunityToolkit.Mvvm.ComponentModel;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Observable wrapper over a Core <see cref="Building"/> so edits to its name refresh
/// the list live (the Core model stays UI-agnostic — no INotifyPropertyChanged in the domain).
/// Ranges are edited in place as plain <see cref="NetworkRange"/> objects.</summary>
public sealed partial class BuildingItem : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private string notes;

    public List<NetworkRange> Ranges { get; set; }

    public BuildingItem(Building building)
    {
        name = building.Name;
        notes = building.Notes;
        Ranges = new List<NetworkRange>(building.Ranges);
    }

    public BuildingItem(string name)
    {
        this.name = name;
        notes = "";
        Ranges = new List<NetworkRange>();
    }

    public Building ToBuilding() => new() { Name = Name, Notes = Notes, Ranges = Ranges };
}
