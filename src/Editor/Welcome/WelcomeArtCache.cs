using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Rasterizes only immutable decorative art at double device resolution; text and editor controls stay vector-based.</summary>
internal sealed record WelcomeArtCache(BitmapSource Image, Rect Bounds)
{
    internal static WelcomeArtCache Create(Drawing art, double dpiScale)
    {
        Rect bounds = art.Bounds;
        if (bounds.IsEmpty) { var empty = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Pbgra32, null, new byte[4], 4); empty.Freeze(); return new(empty, Rect.Empty); }
        bounds.Inflate(1, 1);
        double scale = Math.Clamp(dpiScale * 2, 1, 8);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.PushTransform(new MatrixTransform(scale, 0, 0, scale, -bounds.X * scale, -bounds.Y * scale));
            drawing.DrawDrawing(art); drawing.Pop();
        }
        int width = Math.Max(1, (int)Math.Ceiling(bounds.Width * scale)), height = Math.Max(1, (int)Math.Ceiling(bounds.Height * scale));
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bounds.Width = width / scale; bounds.Height = height / scale;
        image.Render(visual); image.Freeze(); return new(image, bounds);
    }
}
