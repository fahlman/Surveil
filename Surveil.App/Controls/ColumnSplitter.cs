using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace Surveil.App.Controls;

/// <summary>A thin draggable strip for resizing a Grid column. Shows a west-east resize cursor and
/// raises manipulation events (the host wires <c>ManipulationDelta</c> to adjust the column width).</summary>
public partial class ColumnSplitter : ContentControl
{
    public ColumnSplitter()
    {
        ManipulationMode = ManipulationModes.TranslateX;
        // Stretch the (transparent) content across the whole strip so the full width is grabbable.
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
    }
}
