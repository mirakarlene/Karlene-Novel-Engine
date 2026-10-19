using System.Windows;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Packs immutable artwork once; motion draws cached regions through WPF's retained compositor.</summary>
internal sealed class GraphicsAtlas
{
    private readonly IReadOnlyList<WelcomeArtCache> source;
    private readonly Int32Rect[] regions;
    private readonly GraphicsFrame frame;
    private BitmapSource? lastBitmap;
    private WelcomeArtCache[]? result;
    internal GraphicsAtlas(IReadOnlyList<WelcomeArtCache> source)
    {
        this.source = source.ToArray(); regions = new Int32Rect[source.Count]; var sprites = new List<GraphicsSprite>(source.Count);
        int x = 0, y = 0, row = 0, width = 1;
        for (int i = 0; i < source.Count; i++)
        {
            var art = source[i]; if (art.Bounds.IsEmpty) continue;
            int w = art.Image.PixelWidth, h = art.Image.PixelHeight;
            if (x > 0 && x + w > 2048) { y += row; x = 0; row = 0; }
            regions[i] = new(x, y, w, h); sprites.Add(new(GraphicsTextures.Get(art.Image), new Rect(x, y, w, h)));
            x += w; row = Math.Max(row, h); width = Math.Max(width, x);
        }
        frame = new(width, Math.Max(1, y + row), sprites);
    }
    internal GraphicsFrame Frame => frame;
    internal IReadOnlyList<WelcomeArtCache> Artwork(BitmapSource bitmap)
    {
        if (ReferenceEquals(bitmap, lastBitmap)) return result!;
        lastBitmap = bitmap; result = new WelcomeArtCache[source.Count];
        for (int i = 0; i < source.Count; i++)
        {
            if (regions[i].IsEmpty || source[i].Bounds.IsEmpty) { result[i] = source[i]; continue; }
            var image = new CroppedBitmap(bitmap, regions[i]); image.Freeze(); result[i] = new(image, source[i].Bounds);
        }
        return result;
    }
}
