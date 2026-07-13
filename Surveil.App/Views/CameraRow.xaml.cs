using Microsoft.UI.Xaml.Controls;

namespace Surveil.App.Views;

/// <summary>One camera row: the provision checkbox, IP, login state, and (once identified) the
/// camera's identity plus its services and video capabilities. DataContext is a CameraItem.</summary>
public sealed partial class CameraRow : UserControl
{
    public CameraRow() => InitializeComponent();
}
