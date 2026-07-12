using CommunityToolkit.Mvvm.ComponentModel;
using Surveil.Core;

namespace Surveil.App.ViewModels;

/// <summary>A camera row in the tree. Wraps the core <see cref="CameraStatus"/> and adds a
/// provision-selection checkbox: ticking it marks the camera as a provisioning target. This is a
/// separate axis from the range checkboxes (which pick scan targets) — a camera checkbox never
/// affects its range, and vice-versa.</summary>
public sealed partial class CameraItem : ObservableObject
{
    public CameraStatus Camera { get; }

    [ObservableProperty] private bool isSelected;

    /// <summary>Raised when the provision checkbox toggles, so the owner can refresh the target set.</summary>
    public Action? SelectionChanged { get; set; }

    public CameraItem(CameraStatus camera) => Camera = camera;

    public string Ip => Camera.Ip;
    public string StatusText => Camera.Status;

    partial void OnIsSelectedChanged(bool value) => SelectionChanged?.Invoke();
}
