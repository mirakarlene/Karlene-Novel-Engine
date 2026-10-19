using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class GraphicsAtlasTests
{
    [Fact]
    public Task Atlas_EmptyArtworkProducesMinimalFrameAndCachesEmptyResult() => WorkspaceTabsTests.Sta(() =>
    {
        var atlas = new GraphicsAtlas([]);
        Assert.Equal(1, atlas.Frame.Width); Assert.Equal(1, atlas.Frame.Height); Assert.Empty(atlas.Frame.Sprites);
        var bitmap = Image(1, 1, 0);
        var result = atlas.Artwork(bitmap);
        Assert.Empty(result); Assert.Same(result, atlas.Artwork(bitmap));
        return Task.CompletedTask;
    });

    [Fact]
    public Task Atlas_RowPackingKeepsBoundsFrozenColorsAndSourceIdentityAcrossCachedAndReplacementBitmaps() => WorkspaceTabsTests.Sta(() =>
    {
        var first = new WelcomeArtCache(Image(1200, 2, 50), new Rect(-3, 5, 600, 1));
        var empty = new WelcomeArtCache(Image(1, 1, 0), Rect.Empty);
        var second = new WelcomeArtCache(Image(1000, 3, 100), new Rect(7, -9, 500, 1.5));
        var third = new WelcomeArtCache(Image(10, 1, 150), new Rect(15, 20, 5, .5));
        var sources = new List<WelcomeArtCache> { first, empty, second, third };
        var atlas = new GraphicsAtlas(sources); sources.Clear();
        Assert.Equal(1200, atlas.Frame.Width); Assert.Equal(5, atlas.Frame.Height);
        Assert.Equal(new[] { new Rect(0, 0, 1200, 2), new Rect(0, 2, 1000, 3), new Rect(1000, 2, 10, 1) }, atlas.Frame.Sprites.Select(sprite => sprite.Bounds));
        Assert.Equal(new byte[] { 50, 100, 150 }, atlas.Frame.Sprites.Select(sprite => sprite.Texture.Pixels[0]));
        var bitmap = PackedImage(atlas.Frame, 0);
        var result = atlas.Artwork(bitmap);
        Assert.Equal(4, result.Count); Assert.Same(empty, result[1]);
        Assert.Equal(new[] { first.Bounds, Rect.Empty, second.Bounds, third.Bounds }, result.Select(art => art.Bounds));
        foreach (int index in new[] { 0, 2, 3 })
        {
            Assert.True(result[index].Image.IsFrozen);
            Assert.Equal(new[] { first, empty, second, third }[index].Image.PixelWidth, result[index].Image.PixelWidth);
            Assert.Equal(new[] { first, empty, second, third }[index].Image.PixelHeight, result[index].Image.PixelHeight);
            byte expected = index == 0 ? (byte)50 : index == 2 ? (byte)100 : (byte)150;
            Assert.All(Blue(result[index].Image), blue => Assert.Equal(expected, blue));
        }
        Assert.Same(result, atlas.Artwork(bitmap));
        var replacement = atlas.Artwork(PackedImage(atlas.Frame, 1));
        Assert.NotSame(result, replacement);
        Assert.All(Blue(replacement[0].Image), blue => Assert.Equal((byte)51, blue));
        Assert.All(Blue(result[0].Image), blue => Assert.Equal((byte)50, blue));
        return Task.CompletedTask;
    });

    private static BitmapSource Image(int width, int height, byte blue)
    {
        var bytes = new byte[width * height * 4];
        for (int pixel = 0; pixel < width * height; pixel++) { bytes[pixel * 4] = blue; bytes[pixel * 4 + 3] = 255; }
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Pbgra32, null, bytes, width * 4); bitmap.Freeze(); return bitmap;
    }

    private static BitmapSource PackedImage(GraphicsFrame frame, byte offset)
    {
        var bytes = new byte[frame.Width * frame.Height * 4];
        foreach (var sprite in frame.Sprites)
            for (int y = (int)sprite.Bounds.Top; y < sprite.Bounds.Bottom; y++)
                for (int x = (int)sprite.Bounds.Left; x < sprite.Bounds.Right; x++)
                { int pixel = (y * frame.Width + x) * 4; bytes[pixel] = (byte)(sprite.Texture.Pixels[0] + offset); bytes[pixel + 3] = 255; }
        var bitmap = BitmapSource.Create(frame.Width, frame.Height, 96, 96, PixelFormats.Pbgra32, null, bytes, frame.Width * 4); bitmap.Freeze(); return bitmap;
    }

    private static IEnumerable<byte> Blue(BitmapSource bitmap)
    {
        var bytes = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(bytes, bitmap.PixelWidth * 4, 0);
        return bytes.Where((_, index) => index % 4 == 0);
    }
}
