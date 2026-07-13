using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>A CIDR range node under a site. Locks after creation, carries a selection checkbox
/// for scanning, and holds the cameras found in it by the most recent scan.</summary>
public sealed partial class NetworkRangeItem : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private string cidr;
    [ObservableProperty] private bool isEditing;
    [ObservableProperty] private bool isSelected;

    /// <summary>False when a filter is active and no camera in this range matches — the range hides.</summary>
    [ObservableProperty] private bool isVisible = true;

    /// <summary>The site this range belongs to (for add-sibling, remove, and selection roll-up).</summary>
    public SiteItem? Parent { get; set; }

    /// <summary>Cameras found in this CIDR by the most recent scan (each with a selection checkbox).</summary>
    public ObservableCollection<CameraItem> Cameras { get; } = new();

    public NetworkRangeItem(NetworkRange range, SiteItem? parent = null, bool editing = false)
    {
        name = range.Name;
        cidr = range.Cidr;
        isEditing = editing;
        Parent = parent;
    }

    /// <summary>A brand-new range starts editable so it can be filled in.</summary>
    public NetworkRangeItem(SiteItem? parent = null)
    {
        name = "";
        cidr = "";
        isEditing = true;
        Parent = parent;
    }

    public NetworkRange ToRange() => new() { Name = Name, Cidr = Cidr };

    partial void OnIsSelectedChanged(bool value) => Parent?.RefreshSelection();

    [RelayCommand] private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand]
    private void Remove()
    {
        var parent = Parent;
        if (parent is null) return;
        parent.Children.Remove(this);
        // A site always keeps at least one range — recreate an empty one.
        if (parent.Children.Count == 0) parent.Children.Add(new NetworkRangeItem(parent));
        parent.RefreshSelection();
    }

    [RelayCommand]
    private void AddSibling()
    {
        if (Parent is null) return;
        var index = Parent.Children.IndexOf(this);
        Parent.Children.Insert(index + 1, new NetworkRangeItem(Parent));
        Parent.RefreshSelection();
    }
}
