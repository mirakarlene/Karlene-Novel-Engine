using System.Windows;
using System.Windows.Media;

namespace KNE;

/// <summary>Creates the scalable ornamental default background from editable theme colors.</summary>
public static class EnginePresentation
{
    /// <summary>Builds a frozen tiled brush, or a solid background when the pattern is disabled.</summary>
    public static Brush CreateBackground(GameTheme theme)
    {
        var background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.BackgroundColor));
        if (!theme.Pattern) { background.Freeze(); return background; }
        var ink = new SolidColorBrush((Color)ColorConverter.ConvertFromString(theme.PatternColor)) { Opacity = 0.35 };
        var drawing = new DrawingGroup();
        using (var context = drawing.Open())
        {
            context.DrawRectangle(background, null, new Rect(0, 0, 240, 240));
            var pen = new Pen(ink, 1.3);
            for (int i = 0; i < 4; i++)
            {
                var geometry = Geometry.Parse($"M 120,20 C {20 + i * 10},50 {20 + i * 10},190 120,220 C {220 - i * 10},190 {220 - i * 10},50 120,20");
                context.DrawGeometry(null, pen, geometry);
            }
            context.DrawEllipse(ink, null, new Point(120, 120), 4, 4);
            context.DrawLine(pen, new Point(0, 120), new Point(240, 120));
            context.DrawLine(pen, new Point(120, 0), new Point(120, 240));
        }
        var brush = new DrawingBrush(drawing)
        {
            TileMode = TileMode.Tile, ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, 240, 240), Stretch = Stretch.Fill
        };
        brush.Freeze();
        return brush;
    }
}
