using CommunityToolkit.Mvvm.ComponentModel;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>Observable wrapper over a Core <see cref="NetworkRange"/> that also tracks whether the
/// row is being edited. Rows are locked (read-only) after creation and unlocked via the edit button.</summary>
public sealed partial class NetworkRangeItem : ObservableObject
{
    [ObservableProperty] private string name;
    [ObservableProperty] private string cidr;
    [ObservableProperty] private bool isEditing;

    public NetworkRangeItem(NetworkRange range, bool editing = false)
    {
        name = range.Name;
        cidr = range.Cidr;
        isEditing = editing;
    }

    /// <summary>A brand-new range starts in edit mode so it can be filled in.</summary>
    public NetworkRangeItem()
    {
        name = "";
        cidr = "";
        isEditing = true;
    }

    public NetworkRange ToRange() => new() { Name = Name, Cidr = Cidr };
}
