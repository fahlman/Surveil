using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>A site node: parent of its CIDR ranges. Expands/collapses via a chevron, name locks
/// after creation, and its (tri-state) checkbox selects/deselects all of its CIDRs.</summary>
public sealed partial class SiteItem : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private string notes;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool isExpanded = true;

    /// <summary>False when a filter is active and no range under this site has a matching camera.</summary>
    [ObservableProperty] private bool isVisible = true;

    /// <summary>Null = some (but not all) CIDRs selected (indeterminate); true = all; false = none.</summary>
    [ObservableProperty] private bool? isSelected = false;

    private bool syncing;

    /// <summary>The CIDR ranges nested under this site.</summary>
    public ObservableCollection<NetworkRangeItem> Children { get; } = new();

    /// <summary>The collection this site lives in (for self-removal / add-sibling).</summary>
    public ObservableCollection<SiteItem>? Owner { get; set; }

    public SiteItem(Site site, ObservableCollection<SiteItem>? owner = null)
    {
        name = site.Name;
        notes = site.Notes;
        isEditing = false;
        Owner = owner;
        foreach (var range in site.Ranges) Children.Add(new NetworkRangeItem(range, this));
    }

    /// <summary>A brand-new site starts editable, expanded, and with one empty CIDR to fill in.</summary>
    public SiteItem(string name, ObservableCollection<SiteItem>? owner = null)
    {
        this.name = name;
        notes = "";
        isEditing = true;
        Owner = owner;
        Children.Add(new NetworkRangeItem(this));
    }

    public Site ToSite() => new()
    {
        Name = Name,
        Notes = Notes,
        Ranges = Children.Select(child => child.ToRange()).ToList(),
    };

    // Checking the site selects/deselects every CIDR; a child changing rolls the state back up.
    partial void OnIsSelectedChanged(bool? value)
    {
        if (syncing || value is not bool all) return;
        syncing = true;
        foreach (var child in Children) child.IsSelected = all;
        syncing = false;
    }

    /// <summary>Recompute this site's checkbox from its children (all / none / some).</summary>
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
        // Never leave the map with no sites — recreate an empty one.
        if (Owner.Count == 0) Owner.Add(new SiteItem("Site 1", Owner));
    }

    /// <summary>Add a new site right after this one (the site row's + button).</summary>
    [RelayCommand]
    private void AddSite()
    {
        if (Owner is null) return;
        var index = Owner.IndexOf(this);
        Owner.Insert(index + 1, new SiteItem($"Site {Owner.Count + 1}", Owner));
    }
}
