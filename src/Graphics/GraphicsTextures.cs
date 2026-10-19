using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Converts a frozen WPF texture once; native frames reuse the immutable premultiplied pixels.</summary>
internal static class GraphicsTextures
{
    private static readonly ConditionalWeakTable<BitmapSource, GraphicsTexture> cache = new();
    private static long nextId;
    internal static GraphicsTexture Get(BitmapSource source) => cache.GetValue(source, image =>
    {
        BitmapSource bitmap = image.Format == PixelFormats.Pbgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Pbgra32, null, 0);
        var bytes = new byte[checked(bitmap.PixelWidth * bitmap.PixelHeight * 4)]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return new(Interlocked.Increment(ref nextId), bitmap.PixelWidth, bitmap.PixelHeight, bytes);
    });
}
