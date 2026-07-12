using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>A CIDR range node under a building. Locks (read-only) after creation; the edit button
/// unlocks it. Owns its edit/delete/add-sibling commands so templates bind them with x:Bind.</summary>
public sealed partial class NetworkRangeItem : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private string cidr;
    [ObservableProperty] private bool isEditing;

    /// <summary>The building this range belongs to (for add-sibling and remove).</summary>
    public BuildingItem? Parent { get; set; }

    public NetworkRangeItem(NetworkRange range, BuildingItem? parent = null, bool editing = false)
    {
        name = range.Name;
        cidr = range.Cidr;
        isEditing = editing;
        Parent = parent;
    }

    /// <summary>A brand-new range starts editable so it can be filled in.</summary>
    public NetworkRangeItem(BuildingItem? parent = null)
    {
        name = "";
        cidr = "";
        isEditing = true;
        Parent = parent;
    }

    public NetworkRange ToRange() => new() { Name = Name, Cidr = Cidr };

    [RelayCommand] private void ToggleEdit() => IsEditing = !IsEditing;

    [RelayCommand] private void Remove() => Parent?.Children.Remove(this);

    [RelayCommand]
    private void AddSibling()
    {
        if (Parent is null) return;
        var index = Parent.Children.IndexOf(this);
        Parent.Children.Insert(index + 1, new NetworkRangeItem(Parent));
    }
}
