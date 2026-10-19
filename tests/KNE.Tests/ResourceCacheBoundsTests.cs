using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ResourceCacheBoundsTests
{
    private const long Mebibyte = 1024 * 1024;

    [Fact]
    public void Cache_ExactBudgetIsAcceptedAndNextImageEvictsLeastRecentlyUsedRatherThanRecentlyTouchedEntry()
    {
        using var fixture = new TempProject(); var cache = Cache(16);
        string[] paths = Enumerable.Range(0, 4).Select(index => Image(fixture, "lru-" + index + ".png", 1024, 1024, (byte)index)).ToArray();
        var first = cache.Load(paths[0]); var second = cache.Load(paths[1]);
        _ = cache.Load(paths[2]); _ = cache.Load(paths[3]);
        Assert.Equal(16 * Mebibyte, cache.CachedBytes); Assert.Equal(4, cache.CachedCount);
        Assert.Same(first, cache.Load(paths[0]));
        string extra = Image(fixture, "next.png", 1, 1, 200); _ = cache.Load(extra);
        Assert.Equal(12 * Mebibyte + 4, cache.CachedBytes); Assert.Equal(4, cache.CachedCount);
        Assert.Same(first, cache.Load(paths[0])); Assert.NotSame(second, cache.Load(paths[1]));
        Assert.True(cache.CachedBytes <= 16 * Mebibyte);
    }

    [Fact]
    public void Cache_ShrinkingBudgetImmediatelyEvictsOlderImagesAndClearReleasesEveryCachedReference()
    {
        using var fixture = new TempProject(); var cache = Cache(32);
        string[] paths = Enumerable.Range(0, 6).Select(index => Image(fixture, "shrink-" + index + ".png", 1024, 1024, (byte)index)).ToArray();
        foreach (string path in paths) _ = cache.Load(path);
        var last = cache.Load(paths[^1]); Assert.Equal(24 * Mebibyte, cache.CachedBytes); Assert.Equal(6, cache.CachedCount);
        cache.Configure(new() { LimitResourceCache = true, ResourceCacheLimitMb = 16 });
        Assert.Equal(16 * Mebibyte, cache.CachedBytes); Assert.Equal(4, cache.CachedCount); Assert.Same(last, cache.Load(paths[^1]));
        cache.Clear(); Assert.Equal(0, cache.CachedBytes); Assert.Equal(0, cache.CachedCount);
        Assert.NotSame(last, cache.Load(paths[^1])); Assert.Equal(4 * Mebibyte, cache.CachedBytes);
    }

    [Fact]
    public void Cache_ImageLargerThanBudgetIsDecodedForDisplayWithoutDisplacingExistingReusableAssetsOrBeingRetained()
    {
        using var fixture = new TempProject(); var cache = Cache(16);
        string smallPath = Image(fixture, "keep.png", 1, 1, 100), oversizedPath = Image(fixture, "oversized.png", 2049, 2048, 200);
        var small = cache.Load(smallPath); var oversized = cache.Load(oversizedPath);
        Assert.True(oversized.IsFrozen); Assert.Equal(2049, oversized.PixelWidth); Assert.Equal(2048, oversized.PixelHeight);
        Assert.Equal(4, cache.CachedBytes); Assert.Equal(1, cache.CachedCount); Assert.Same(small, cache.Load(smallPath));
        Assert.NotSame(oversized, cache.Load(oversizedPath)); Assert.Equal(4, cache.CachedBytes);
    }

    [Fact]
    public void Cache_DisabledBudgetRetainsMoreThanItsSelectedLimitAndEnablingItTrimsImmediately()
    {
        using var fixture = new TempProject(); var cache = Cache(16); cache.Configure(new() { LimitResourceCache = false, ResourceCacheLimitMb = 16 });
        for (int index = 0; index < 5; index++) _ = cache.Load(Image(fixture, "unlimited-" + index + ".png", 1024, 1024, (byte)index));
        Assert.Equal(20 * Mebibyte, cache.CachedBytes); Assert.Equal(5, cache.CachedCount);
        cache.Configure(new() { LimitResourceCache = true, ResourceCacheLimitMb = 16 });
        Assert.Equal(16 * Mebibyte, cache.CachedBytes); Assert.Equal(4, cache.CachedCount);
        cache.Clear(); Assert.Equal(0, cache.CachedBytes); Assert.Equal(0, cache.CachedCount);
    }

    [Fact]
    public void Cache_RepeatedLargeSceneChangesReachAStableBoundInsteadOfRetainingAllPreviouslyDecodedImages()
    {
        using var fixture = new TempProject(); var cache = Cache(16);
        string[] paths = Enumerable.Range(0, 7).Select(index => Image(fixture, "scene-" + index + ".png", 1024, 1024, (byte)index)).ToArray();
        for (int pass = 0; pass < 4; pass++)
        foreach (string path in paths)
        {
            Assert.True(cache.Load(path).IsFrozen);
            Assert.InRange(cache.CachedBytes, 4 * Mebibyte, 16 * Mebibyte); Assert.InRange(cache.CachedCount, 1, 4);
        }
        Assert.Equal(16 * Mebibyte, cache.CachedBytes); Assert.Equal(4, cache.CachedCount);
    }

    [Fact]
    public void Cache_CancelledLoadDoesNotPublishDecodedResourcesOrDiscardExistingEntries()
    {
        using var fixture = new TempProject(); var cache = Cache(16);
        string kept = Image(fixture, "existing.png", 2, 2, 10), canceled = Image(fixture, "canceled.png", 1024, 1024, 20);
        var existing = cache.Load(kept); using var token = new CancellationTokenSource(); token.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => cache.Load(canceled, token.Token));
        Assert.Equal(16, cache.CachedBytes); Assert.Equal(1, cache.CachedCount); Assert.Same(existing, cache.Load(kept));
        _ = cache.Load(canceled); Assert.Equal(4 * Mebibyte + 16, cache.CachedBytes); Assert.Equal(2, cache.CachedCount);
    }

    private static SceneAssetCache Cache(int megabytes)
    {
        var cache = new SceneAssetCache(); cache.Configure(new() { LimitResourceCache = true, ResourceCacheLimitMb = megabytes }); return cache;
    }

    private static string Image(TempProject fixture, string relative, int width, int height, byte value)
    {
        string path = PathSafety.ResolveRelative(fixture.ProjectPath, relative); var pixels = new byte[width * height * 4];
        for (int offset = 0; offset < pixels.Length; offset += 4) { pixels[offset] = value; pixels[offset + 3] = 255; }
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4); source.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(source)); using var output = File.Create(path); encoder.Save(output); return path;
    }
}
