using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class WelcomeRenderTests
{
    [Fact]
    public Task Welcome_RenderProbe_RecordsWarmFrameCostWhenRequested() => WorkspaceTabsTests.Sta(async () =>
    {
        string? output = Environment.GetEnvironmentVariable("KNE_WELCOME_PROBE");
        if (output == null) return;
        Directory.CreateDirectory(output);
        foreach (var (name, view) in new (string, WelcomeAnimation)[] { ("logo", new WatercolorLogo()), ("bubbles", new WelcomeBubbles()) })
        {
            var size = name == "logo" ? new Size(960, 125) : new Size(1460, 900);
            view.Measure(size); view.Arrange(new Rect(size)); view.UpdateLayout();
            async Task<RenderTargetBitmap> Frame()
            {
                await view.Dispatcher.InvokeAsync(() => view.UpdateLayout(), DispatcherPriority.Render);
                var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32); image.Render(view); return image;
            }
            void Save(RenderTargetBitmap image, string suffix)
            { var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(output, name + suffix + ".png")); encoder.Save(file); }
            Save(await Frame(), "-start");
            for (int i = 0; i < 5; i++) { view.Step(1d / 30); _ = await Frame(); }
            long allocated = GC.GetTotalAllocatedBytes(false); var clock = Stopwatch.StartNew();
            for (int i = 0; i < 40; i++) { view.Step(1d / 30); _ = await Frame(); }
            clock.Stop(); allocated = GC.GetTotalAllocatedBytes(false) - allocated;
            Save(await Frame(), "-end");
            await File.WriteAllTextAsync(Path.Combine(output, name + "-metrics.json"), JsonSerializer.Serialize(new { frames = 40, milliseconds = clock.Elapsed.TotalMilliseconds, allocated, millisecondsPerFrame = clock.Elapsed.TotalMilliseconds / 40 }, new JsonSerializerOptions { WriteIndented = true }));
            string baseline = Path.Combine(Path.GetDirectoryName(output)!, "before");
            if (Path.GetFileName(output) == "after" && Directory.Exists(baseline))
                foreach (string suffix in new[] { "-start", "-end" })
                {
                    byte[] Pixels(string path)
                    {
                        using var input = File.OpenRead(path);
                        var decoder = new PngBitmapDecoder(input, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
                        var frame = new FormatConvertedBitmap(decoder.Frames[0], PixelFormats.Pbgra32, null, 0);
                        var bytes = new byte[frame.PixelWidth * frame.PixelHeight * 4]; frame.CopyPixels(bytes, frame.PixelWidth * 4, 0); return bytes;
                    }
                    byte[] before = Pixels(Path.Combine(baseline, name + suffix + ".png")), after = Pixels(Path.Combine(output, name + suffix + ".png"));
                    Assert.Equal(before.Length, after.Length);
                    long difference = 0, ink = 0; int max = 0;
                    for (int i = 0; i < before.Length; i++) { int delta = Math.Abs(before[i] - after[i]); difference += delta; ink += before[i]; max = Math.Max(max, delta); }
                    double relativeDifference = difference / (double)Math.Max(1, ink);
                    await File.WriteAllTextAsync(Path.Combine(output, name + suffix + "-comparison.json"), JsonSerializer.Serialize(new { meanChannelDifference = difference / (double)before.Length, relativeDifference, maximumChannelDifference = max }));
                    // Supersampling changes edge coverage. Compare premultiplied channels too:
                    // near-transparent bubbles have little ink, so their relative error alone is misleading.
                    Assert.InRange(difference / (double)before.Length, 0, 2); // < 0.8% of the channel range.
                    Assert.InRange(relativeDifference, 0, .1);
                }
        }
    });

    [Fact]
    public Task Welcome_CachesSurviveAnimationAndRebuildForPaletteObjectsAndLargerLogo() => WorkspaceTabsTests.Sta(async () =>
    {
        var size = new Size(960, 125); var logo = new WatercolorLogo();
        var bubbles = new WelcomeBubbles(); var bubbleSize = new Size(500, 300);
        async Task<byte[]> Render(WelcomeAnimation view, Size dimensions)
        {
            view.Measure(dimensions); view.Arrange(new Rect(dimensions));
            await view.Dispatcher.InvokeAsync(() => view.UpdateLayout(), DispatcherPriority.Render);
            return WelcomeOptionsTests.Pixels(view, dimensions);
        }
        byte[] initialLogo = await Render(logo, size), initialBubbles = await Render(bubbles, bubbleSize);
        Assert.Equal(1, logo.CacheBuilds); Assert.Equal(1, bubbles.CacheBuilds);
        for (int i = 0; i < 15; i++)
        {
            logo.Step(1d / 30); bubbles.Step(1d / 30);
            _ = await Render(logo, size); _ = await Render(bubbles, bubbleSize);
        }
        Assert.Equal(1, logo.CacheBuilds); Assert.Equal(1, bubbles.CacheBuilds);
        byte[] movingLogo = await Render(logo, size), movingBubbles = await Render(bubbles, bubbleSize);
        Assert.False(initialLogo.SequenceEqual(movingLogo)); Assert.False(initialBubbles.SequenceEqual(movingBubbles));
        logo.StartColor = Colors.Cyan; bubbles.Ink = Brushes.Gold;
        byte[] recoloredLogo = await Render(logo, size), recoloredBubbles = await Render(bubbles, bubbleSize);
        Assert.Equal(2, logo.CacheBuilds); Assert.Equal(2, bubbles.CacheBuilds);
        Assert.False(movingLogo.SequenceEqual(recoloredLogo)); Assert.False(movingBubbles.SequenceEqual(recoloredBubbles));
        bubbles.ObjectKind = "stars";
        byte[] stars = await Render(bubbles, bubbleSize);
        Assert.False(recoloredBubbles.SequenceEqual(stars)); Assert.Equal(3, bubbles.CacheBuilds);
        _ = await Render(logo, new Size(1920, 250)); Assert.Equal(3, logo.CacheBuilds);
        _ = await Render(logo, new Size(1920, 250)); Assert.Equal(3, logo.CacheBuilds);
    });
}
