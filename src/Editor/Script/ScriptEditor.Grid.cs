using System.Windows;
using System.Windows.Media;

namespace KNE;

public partial class ScriptEditor
{
    internal bool ShowGraphGrid
    {
        get => GraphGrid.Visibility == Visibility.Visible;
        set
        {
            if (value == ShowGraphGrid) return;
            GraphGrid.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            ApplyPluginTexture();
            if (value)
            {
                CanvasScale.Changed += GraphViewportChanged;
                CanvasPan.Changed += GraphViewportChanged;
                GraphViewportChanged(null, EventArgs.Empty);
            }
            else
            {
                CanvasScale.Changed -= GraphViewportChanged;
                CanvasPan.Changed -= GraphViewportChanged;
                GraphGrid.Background = null;
            }
        }
    }

    private static readonly Geometry GridLines = Geometry.Parse("M 0.5,32 V 0.5 H 32");

    private void GraphViewportChanged(object? sender, EventArgs e)
    {
        double spacing = 32 * Math.Max(.1, CanvasScale.ScaleX);
        var pen = new Pen(SystemColors.GrayTextBrush, 32 / spacing);
        var drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing(Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))),
                new GeometryDrawing(null, pen, GridLines)
            }
        };
        GraphGrid.Background = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile,
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 32, 32),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(CanvasPan.X % spacing, CanvasPan.Y % spacing, spacing, spacing),
            Opacity = .15
        };
    }
}
