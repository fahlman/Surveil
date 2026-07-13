using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Surveil.App.Controls;

/// <summary>A minimal horizontal wrap panel (WinUI has none built in): lays children left-to-right
/// and wraps to a new row when the next child would overflow the available width. Used for the
/// facet bar so the filters stack instead of running off past the Provision panel.</summary>
public sealed class WrapPanel : Panel
{
    public double HorizontalSpacing { get; set; }
    public double VerticalSpacing { get; set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var maxWidth = availableSize.Width;
        double x = 0, rowHeight = 0, totalHeight = 0, widest = 0;

        foreach (var child in Children)
        {
            child.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;

            if (x > 0 && x + w > maxWidth)
            {
                totalHeight += rowHeight + VerticalSpacing;
                widest = Math.Max(widest, x - HorizontalSpacing);
                x = 0;
                rowHeight = 0;
            }

            x += w + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, h);
        }

        totalHeight += rowHeight;
        widest = Math.Max(widest, x - HorizontalSpacing);
        return new Size(double.IsInfinity(maxWidth) ? widest : Math.Min(widest, maxWidth), totalHeight);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0, y = 0, rowHeight = 0;

        foreach (var child in Children)
        {
            var w = child.DesiredSize.Width;
            var h = child.DesiredSize.Height;

            if (x > 0 && x + w > finalSize.Width)
            {
                y += rowHeight + VerticalSpacing;
                x = 0;
                rowHeight = 0;
            }

            child.Arrange(new Rect(x, y, w, h));
            x += w + HorizontalSpacing;
            rowHeight = Math.Max(rowHeight, h);
        }

        return finalSize;
    }
}
