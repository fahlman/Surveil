using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Surveil.App.ViewModels;

/// <summary>One selectable value inside a facet, e.g. "Hikvision" under Manufacturer, with a live
/// count of how many (otherwise-matching) cameras carry it.</summary>
public sealed partial class FacetOption : ObservableObject
{
    public string Value { get; }

    [ObservableProperty][NotifyPropertyChangedFor(nameof(Label))] private int count;
    [ObservableProperty] private bool isSelected;

    /// <summary>Raised when the checkbox toggles, so the owner can re-apply the filter.</summary>
    public Action? SelectionChanged { get; set; }

    public FacetOption(string value) => Value = value;

    public string Label => $"{Value}  ({Count})";

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}

/// <summary>A facet (Manufacturer, Model, Codec, …) and its discovered values. Options are rebuilt
/// from the identified cameras; a value with a zero count under the current filter is dropped.</summary>
public sealed partial class FacetGroup : ObservableObject
{
    public string Name { get; }
    public ObservableCollection<FacetOption> Options { get; } = new();

    public FacetGroup(string name) => Name = name;

    public int SelectedCount => Options.Count(option => option.IsSelected);
    public bool HasSelection => SelectedCount > 0;

    /// <summary>Button label: the facet name, plus how many values are selected.</summary>
    public string HeaderLabel => SelectedCount > 0 ? $"{Name}  ·  {SelectedCount}" : Name;

    public IReadOnlyList<string> SelectedValues =>
        Options.Where(option => option.IsSelected).Select(option => option.Value).ToList();

    /// <summary>Refresh the header/selection-derived properties after an option toggles.</summary>
    public void RefreshHeader()
    {
        OnPropertyChanged(nameof(HeaderLabel));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedCount));
    }
}
