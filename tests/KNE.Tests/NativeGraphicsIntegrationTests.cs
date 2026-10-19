using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Xunit;

namespace KNE.Tests;

public sealed class NativeGraphicsIntegrationTests
{
    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    [InlineData("auto")]
    public Task NativeWelcome_LoadedLogoAndParticlesCompleteOnWorkersKeepDispatcherResponsiveAndReleaseOnUnload(string api) => WorkspaceTabsTests.Sta(async () =>
    {
        var logo = new WatercolorLogo { Height = 125 };
        var bubbles = new WelcomeBubbles();
        var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(125) }); content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(logo); content.Children.Add(bubbles); Grid.SetRow(bubbles, 1);
        var window = Host(content, api, 640, 430);
        var stats = GraphicsEnvironment.For(window);
        var elapsed = Stopwatch.StartNew(); var intervals = new List<double>(); double previous = 0;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => { double now = elapsed.Elapsed.TotalMilliseconds; intervals.Add(now - previous); previous = now; };
        heartbeat.Start();
        Thread[] workers = [];
        try
        {
            window.Show(); double showMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            double firstInputTurnMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            int expectedDraws = WatercolorLogo.BrandText.Count(character => !char.IsWhiteSpace(character)) + 30;
            await Until(async () =>
            {
                await ForceRender(content);
                return logo.CacheBuilds > 0 && stats.DrawCalls == expectedDraws;
            }, () => $"Backend={stats.Backend}; error={stats.Error}; draws={stats.DrawCalls}; logoCache={logo.CacheBuilds}");
            double readyMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            // Auto measures each differently sized layer; a window may legitimately compose both native backends.
            if (api == "auto")
            {
                string[] backends = stats.Backend.Split(" / ", StringSplitOptions.RemoveEmptyEntries);
                Assert.InRange(backends.Length, 1, 2);
                Assert.All(backends, backend => Assert.Contains(backend, new[] { "vulkan", "opengl" }));
            }
            else Assert.Equal(api, stats.Backend);
            Assert.Null(stats.Error); Assert.Equal(expectedDraws, stats.DrawCalls);
            Assert.True(stats.AllocatedBytes > 0); Assert.True(double.IsFinite(stats.RenderMilliseconds) && stats.RenderMilliseconds >= 0);
            Assert.Equal(30, bubbles.Field.Bubbles.Count);
            var layers = new[] { Layer(logo), Layer(bubbles) };
            workers = layers.Select(layer => Field<Thread>(layer, "thread")).ToArray();
            Assert.All(workers, worker => { Assert.True(worker.IsAlive); Assert.NotEqual(Environment.CurrentManagedThreadId, worker.ManagedThreadId); });
            foreach (var layer in layers)
            {
                var bitmap = Field<WriteableBitmap>(layer, "bitmap");
                var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
                Assert.Contains(Enumerable.Range(0, bitmap.PixelWidth * bitmap.PixelHeight), index => pixels[index * 4 + 3] > 0);
            }
            heartbeat.Stop();
            // Offline rendering is measured separately; it must not warm the live logo's cold preparation.
            var offlineLogo = new WatercolorLogo(); var size = new Size(640, 125);
            var probe = Stopwatch.StartNew(); _ = WelcomeOptionsTests.Pixels(offlineLogo, size); double offlineColdMilliseconds = probe.Elapsed.TotalMilliseconds;
            probe.Restart();
            for (int frame = 0; frame < 5; frame++) { offlineLogo.Step(1d / 60); _ = WelcomeOptionsTests.Pixels(offlineLogo, size); }
            double offlineWarmMillisecondsPerFrame = probe.Elapsed.TotalMilliseconds / 5;
            string? path = Environment.GetEnvironmentVariable("KNE_GPU_PROBE");
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(Path.Combine(path, "welcome-" + api + ".json"), JsonSerializer.Serialize(new
                {
                    backend = stats.Backend, showMilliseconds, firstInputTurnMilliseconds, readyMilliseconds,
                    dispatcherHeartbeatCount = intervals.Count, maximumDispatcherGapMilliseconds = intervals.DefaultIfEmpty(0).Max(),
                    offlineColdMilliseconds, offlineWarmMillisecondsPerFrame, nativeRenderMilliseconds = stats.RenderMilliseconds,
                    drawCalls = stats.DrawCalls, trackedGpuBytes = stats.AllocatedBytes,
                    scope = "Offscreen WPF host with real native logo/particle workers. Timings are observations, not application FPS or performance assertions."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            // Removing the visual tree must release resources even while the host window remains alive.
            window.Content = null;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Until(() => Task.FromResult(workers.All(worker => !worker.IsAlive)), () => "GPU worker remained alive after Unloaded.");
            Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes); Assert.Equal("", stats.Backend);
            Assert.False(logo.IsAnimating); Assert.False(bubbles.IsAnimating);
        }
        finally
        {
            heartbeat.Stop(); window.Close();
            if (workers.Length > 0) await Task.Run(() => { foreach (var worker in workers) worker.Join(TimeSpan.FromSeconds(5)); });
        }
    });

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public Task NativeSceneBackground_LoadedGeometryUsesSelectedBackendAndSingleDrawAcrossQualityChanges(string api) => WorkspaceTabsTests.Sta(async () =>
    {
        var background = new AnimatedBackground();
        background.Configure(new GameTheme { Pattern = true, PatternStyle = "waves", PatternColor = "#FF8844CC", AnimationEnabled = true }, true);
        var window = Host(background, api, 400, 240); var stats = GraphicsEnvironment.For(window);
        Thread? worker = null;
        try
        {
            window.Show();
            await Until(async () => { await ForceRender(background); return stats.DrawCalls == 1; }, () => $"Backend={stats.Backend}; error={stats.Error}; draws={stats.DrawCalls}");
            Assert.Equal(api, stats.Backend); Assert.Null(stats.Error); Assert.Equal(1, stats.DrawCalls);
            var layer = Layer(background); worker = Field<Thread>(layer, "thread");
            var firstBitmap = Field<WriteableBitmap>(layer, "bitmap");
            var before = new byte[firstBitmap.PixelWidth * firstBitmap.PixelHeight * 4]; firstBitmap.CopyPixels(before, firstBitmap.PixelWidth * 4, 0);
            Assert.Contains(Enumerable.Range(0, firstBitmap.PixelWidth * firstBitmap.PixelHeight), index => before[index * 4 + 3] > 0);
            foreach (string quality in new[] { "medium", "low", "high" })
            {
                background.SetQuality(quality); background.Phase = quality == "low" ? 1.2 : 2.1;
                await Until(async () =>
                {
                    await ForceRender(background);
                    var image = Field<WriteableBitmap>(layer, "bitmap");
                    var current = new byte[before.Length]; image.CopyPixels(current, image.PixelWidth * 4, 0);
                    if (current.SequenceEqual(before)) return false;
                    before = current; return true;
                }, () => "Scene background did not produce a new native geometry frame: " + quality);
                Assert.Equal(1, stats.DrawCalls); Assert.Equal(api, stats.Backend); Assert.Null(stats.Error);
            }
            window.Content = null; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Until(() => Task.FromResult(!worker.IsAlive), () => "Scene GPU worker remained alive after Unloaded.");
            Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes);
        }
        finally { window.Close(); if (worker != null) await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))); }
    });

    private static Window Host(FrameworkElement content, string api, int width, int height)
    {
        var window = new Window { Content = content, Width = width, Height = height, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        GraphicsEnvironment.SetSettings(window, new GraphicsSettings { Api = api, VSync = false, MaxFps = 30, MsaaSamples = 2 });
        return window;
    }

    private static GraphicsLayer Layer(object owner) => Field<GraphicsLayer>(owner, "graphics");
    private static T Field<T>(object instance, string name) => Assert.IsType<T>(instance.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(instance));

    private static async Task ForceRender(FrameworkElement content)
    {
        content.InvalidateVisual(); content.UpdateLayout();
        await content.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        int width = Math.Max(1, (int)content.ActualWidth), height = Math.Max(1, (int)content.ActualHeight);
        var image = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); image.Render(content);
    }

    private static async Task Until(Func<Task<bool>> ready, Func<string> diagnostic)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20))
        {
            if (await ready()) return;
            await Task.Delay(20);
        }
        Assert.Fail("Native graphics integration timed out: " + diagnostic());
    }
}
