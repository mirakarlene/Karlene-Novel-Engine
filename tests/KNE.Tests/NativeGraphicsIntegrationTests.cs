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
        var window = Host(content, api, 640, 430); var stats = GraphicsEnvironment.For(window);
        var elapsed = Stopwatch.StartNew(); var intervals = new List<double>(); double previous = 0;
        var heartbeat = new DispatcherTimer(DispatcherPriority.Input) { Interval = TimeSpan.FromMilliseconds(10) };
        heartbeat.Tick += (_, _) => { double now = elapsed.Elapsed.TotalMilliseconds; intervals.Add(now - previous); previous = now; };
        heartbeat.Start(); Thread? worker = null;
        try
        {
            window.Show(); double showMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Input);
            double firstInputTurnMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            int expectedDraws = WatercolorLogo.BrandText.Count(character => !char.IsWhiteSpace(character)) + 30;
            await Until(async () => { await ForceRender(content); return logo.CacheBuilds > 0 && stats.DrawCalls == expectedDraws; },
                () => $"Backend={stats.Backend}; error={stats.Error}; draws={stats.DrawCalls}; logoCache={logo.CacheBuilds}");
            double readyMilliseconds = elapsed.Elapsed.TotalMilliseconds;
            AssertBackend(stats, api); Assert.Null(stats.Error); Assert.Equal(expectedDraws, stats.DrawCalls);
            Assert.Equal(30, bubbles.Field.Bubbles.Count);
            Assert.Equal(0, stats.AllocatedBytes); // Native preparation targets are released after publishing frozen art.
            var logoLayer = Layer(logo); var bubbleLayer = Layer(bubbles);
            Assert.Same(logoLayer.Worker, bubbleLayer.Worker);
            worker = logoLayer.Worker.WorkerThread;
            Assert.True(worker.IsAlive); Assert.NotEqual(Environment.CurrentManagedThreadId, worker.ManagedThreadId);
            foreach (var layer in new[] { logoLayer, bubbleLayer })
            {
                Assert.NotNull(layer.Bitmap); Assert.True(layer.Bitmap.IsFrozen);
                Assert.Contains(Pixels(layer.Bitmap).Where((_, index) => index % 4 == 3), alpha => alpha > 0);
            }

            long preparations = stats.PreparationCount, readback = stats.ReadbackBytes;
            Assert.InRange(preparations, 2, 2); Assert.True(readback > 0);
            var before = await ForceRender(content); var logoAtlas = logoLayer.Bitmap; var bubbleAtlas = bubbleLayer.Bitmap;
            var warm = Stopwatch.StartNew();
            for (int frame = 0; frame < 24; frame++)
            {
                logo.Step(1d / 30); bubbles.Step(1d / 30);
                _ = await ForceRender(content);
            }
            double warmMillisecondsPerFrame = warm.Elapsed.TotalMilliseconds / 24;
            var after = await ForceRender(content);
            Assert.False(before.SequenceEqual(after), "Cached art must still move on the screen.");
            Assert.Same(logoAtlas, logoLayer.Bitmap); Assert.Same(bubbleAtlas, bubbleLayer.Bitmap);
            Assert.Equal(preparations, stats.PreparationCount); Assert.Equal(readback, stats.ReadbackBytes);
            heartbeat.Stop();
            string? path = Environment.GetEnvironmentVariable("KNE_GPU_PROBE");
            if (!string.IsNullOrEmpty(path))
            {
                Directory.CreateDirectory(path);
                await File.WriteAllTextAsync(Path.Combine(path, "welcome-" + api + ".json"), JsonSerializer.Serialize(new
                {
                    backend = stats.Backend, showMilliseconds, firstInputTurnMilliseconds, readyMilliseconds,
                    dispatcherHeartbeatCount = intervals.Count, maximumDispatcherGapMilliseconds = intervals.DefaultIfEmpty(0).Max(),
                    warmMillisecondsPerFrame, preparationCount = preparations, nativeReadbackBytes = readback,
                    drawCalls = stats.DrawCalls, trackedGpuBytes = stats.AllocatedBytes,
                    scope = "Real native atlas preparation followed by warm WPF motion. Timings are observations, not application FPS assertions."
                }, new JsonSerializerOptions { WriteIndented = true }));
            }
            window.Content = null; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Until(() => Task.FromResult(!worker.IsAlive), () => "Shared GPU worker remained alive after its last layer unloaded.");
            Assert.True(logoLayer.IsDisposed); Assert.True(bubbleLayer.IsDisposed);
            Assert.Null(logoLayer.Bitmap); Assert.Null(bubbleLayer.Bitmap);
            Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes); Assert.Equal("", stats.Backend);
            Assert.False(logo.IsAnimating); Assert.False(bubbles.IsAnimating);
        }
        finally { heartbeat.Stop(); window.Close(); if (worker != null) await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))); }
    });

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public Task NativeSceneBackground_LoadedGeometryUsesSelectedBackendAndSingleDrawAcrossQualityChanges(string api) => WorkspaceTabsTests.Sta(async () =>
    {
        var background = new AnimatedBackground();
        background.Configure(new GameTheme { Pattern = true, PatternStyle = "waves", PatternColor = "#FF8844CC", AnimationEnabled = true }, true);
        var window = Host(background, api, 400, 240); var stats = GraphicsEnvironment.For(window); Thread? worker = null;
        try
        {
            window.Show();
            await Until(async () => { await ForceRender(background); return stats.DrawCalls == 1; }, () => $"Backend={stats.Backend}; error={stats.Error}; draws={stats.DrawCalls}");
            Assert.Equal(api, stats.Backend); Assert.Null(stats.Error);
            var layer = Layer(background); worker = layer.Worker.WorkerThread;
            foreach (string quality in new[] { "medium", "low", "high" })
            {
                long oldCount = stats.PreparationCount; background.SetQuality(quality);
                await Until(async () => { await ForceRender(background); return stats.PreparationCount > oldCount; }, () => "Quality change failed to prepare its atlas: " + quality);
                var atlas = layer.Bitmap; Assert.NotNull(atlas); Assert.True(atlas.IsFrozen);
                long prepared = stats.PreparationCount, readback = stats.ReadbackBytes;
                background.Phase = .3; var first = await ForceRender(background);
                background.Phase = 1.8; var second = await ForceRender(background);
                Assert.False(first.SequenceEqual(second), "Wave motion must translate cached art.");
                Assert.Same(atlas, layer.Bitmap); Assert.Equal(prepared, stats.PreparationCount); Assert.Equal(readback, stats.ReadbackBytes);
                Assert.Equal(1, stats.DrawCalls); Assert.Equal(api, stats.Backend); Assert.Null(stats.Error);
            }
            window.Content = null; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Until(() => Task.FromResult(!worker.IsAlive), () => "Scene GPU worker remained alive after Unloaded.");
            Assert.True(layer.IsDisposed); Assert.Null(layer.Bitmap); Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes);
        }
        finally { window.Close(); if (worker != null) await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))); }
    });

    [GpuTheory]
    [InlineData("vulkan", "opengl")]
    [InlineData("opengl", "vulkan")]
    public Task NativeLayers_ChangingForcedApiInvalidatesEveryAtlasAndUsesRequestedBackend(string first, string second) => WorkspaceTabsTests.Sta(async () =>
    {
        var bubbles = new WelcomeBubbles(); var window = Host(bubbles, first, 320, 220); var stats = GraphicsEnvironment.For(window); Thread? worker = null;
        try
        {
            window.Show();
            await Until(async () => { await ForceRender(bubbles); return stats.Backend == first; }, () => "First backend: " + stats.Backend + "; " + stats.Error);
            var layer = Layer(bubbles); worker = layer.Worker.WorkerThread; var oldAtlas = layer.Bitmap;
            long preparations = stats.PreparationCount;
            GraphicsEnvironment.SetSettings(window, new GraphicsSettings { Api = second, MaxFps = 60, VSync = false, MsaaSamples = 4 });
            await Until(async () => { await ForceRender(bubbles); return stats.Backend == second && stats.PreparationCount > preparations; }, () => "Requested backend was not applied: " + stats.Backend + "; " + stats.Error);
            Assert.Equal(second, stats.Backend); Assert.Null(stats.Error); Assert.NotSame(oldAtlas, layer.Bitmap);
            Assert.Same(worker, layer.Worker.WorkerThread); Assert.True(worker.IsAlive);
            long settled = stats.PreparationCount, readback = stats.ReadbackBytes;
            for (int frame = 0; frame < 8; frame++) { bubbles.Step(.02); await ForceRender(bubbles); }
            Assert.Equal(settled, stats.PreparationCount); Assert.Equal(readback, stats.ReadbackBytes);
        }
        finally { window.Close(); if (worker != null) await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))); }
    });

    [GpuTheory]
    [InlineData("vulkan")]
    [InlineData("opengl")]
    public Task NativeWindow_RemovingThenReattachingContentCreatesUsableWorkerAndDropsOldSnapshots(string api) => WorkspaceTabsTests.Sta(async () =>
    {
        var bubbles = new WelcomeBubbles(); var window = Host(bubbles, api, 300, 200);
        var stats = GraphicsEnvironment.For(window); Thread? firstWorker = null, nextWorker = null;
        try
        {
            window.Show();
            await Until(async () => { await ForceRender(bubbles); return stats.DrawCalls == 30; }, () => "Initial atlas: " + stats.Error);
            var first = Layer(bubbles); firstWorker = first.Worker.WorkerThread;
            window.Content = null; await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Until(() => Task.FromResult(!firstWorker.IsAlive), () => "Detached worker did not exit.");
            Assert.True(first.IsDisposed); Assert.Null(first.Bitmap); Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes);
            window.Content = bubbles;
            await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
            await Until(async () => { await ForceRender(bubbles); return stats.DrawCalls == 30; }, () => "Reattached atlas: " + stats.Error);
            var next = Layer(bubbles); nextWorker = next.Worker.WorkerThread;
            Assert.NotSame(first, next); Assert.NotSame(first.Worker, next.Worker); Assert.NotSame(firstWorker, nextWorker);
            Assert.True(nextWorker.IsAlive); Assert.False(next.IsDisposed); Assert.NotNull(next.Bitmap);
            Assert.Equal(api, stats.Backend); Assert.Null(stats.Error);
        }
        finally
        {
            window.Close();
            await Task.Run(() => { firstWorker?.Join(TimeSpan.FromSeconds(5)); nextWorker?.Join(TimeSpan.FromSeconds(5)); });
        }
    });

    [GpuTheory]
    [InlineData("opengl")]
    [InlineData("vulkan")]
    public Task NativeWelcome_HideAndMinimizeClearAtlasRevisionAndWorker_AndRestorePreparesFreshSnapshots(string api) => WorkspaceTabsTests.Sta(async () =>
    {
        var logo = new WatercolorLogo { Height = 100 }; var bubbles = new WelcomeBubbles();
        var content = new Grid(); content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) }); content.RowDefinitions.Add(new RowDefinition());
        content.Children.Add(logo); content.Children.Add(bubbles); Grid.SetRow(bubbles, 1);
        var window = Host(content, api, 420, 300); var stats = GraphicsEnvironment.For(window);
        var workers = new List<Thread>();
        int expectedDraws = WatercolorLogo.BrandText.Count(character => !char.IsWhiteSpace(character)) + 30;
        object? Private(object owner, string field) => owner.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);
        void Released(GraphicsLayer layer, object owner)
        {
            Assert.True(layer.IsDisposed); Assert.Null(layer.Bitmap);
            Assert.Null(Private(layer, "revision")); Assert.Null(Private(layer, "settings")); Assert.Null(Private(owner, "atlas"));
        }
        async Task Ready() => await Until(async () => { await ForceRender(content); return stats.DrawCalls == expectedDraws; },
            () => $"Welcome restore: backend={stats.Backend}; draws={stats.DrawCalls}; error={stats.Error}");
        try
        {
            window.Show(); await Ready();
            foreach (bool minimize in new[] { false, true })
            {
                var logoLayer = Layer(logo); var bubbleLayer = Layer(bubbles);
                Assert.Same(logoLayer.Worker, bubbleLayer.Worker);
                var worker = logoLayer.Worker.WorkerThread; workers.Add(worker);
                Assert.NotNull(Private(logo, "atlas")); Assert.NotNull(Private(bubbles, "atlas"));
                Assert.NotNull(Private(logoLayer, "revision")); Assert.NotNull(Private(bubbleLayer, "revision"));
                Assert.NotNull(logoLayer.Bitmap); Assert.NotNull(bubbleLayer.Bitmap);

                if (minimize) window.WindowState = WindowState.Minimized; else window.Hide();
                await window.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Loaded);
                Released(logoLayer, logo); Released(bubbleLayer, bubbles);
                Assert.Null(stats.DrawCalls); Assert.Equal(0, stats.AllocatedBytes);
                await Until(() => Task.FromResult(!worker.IsAlive), () => "Hidden/minimized welcome retained its native worker.");

                if (minimize) window.WindowState = WindowState.Normal; else window.Show();
                await Ready();
                var nextLogo = Layer(logo); var nextBubbles = Layer(bubbles);
                Assert.NotSame(logoLayer, nextLogo); Assert.NotSame(bubbleLayer, nextBubbles);
                Assert.Same(nextLogo.Worker, nextBubbles.Worker); Assert.NotSame(worker, nextLogo.Worker.WorkerThread);
                workers.Add(nextLogo.Worker.WorkerThread);
                Assert.True(nextLogo.Worker.WorkerThread.IsAlive); Assert.Equal(api, stats.Backend); Assert.Null(stats.Error);
                Assert.NotNull(nextLogo.Bitmap); Assert.NotNull(nextBubbles.Bitmap);
                Assert.NotNull(Private(logo, "atlas")); Assert.NotNull(Private(bubbles, "atlas"));
            }
        }
        finally { window.Close(); await Task.Run(() => { foreach (var worker in workers.Distinct()) worker.Join(TimeSpan.FromSeconds(5)); }); }
    });

    [GpuTheory]
    [InlineData("opengl", "vulkan")]
    [InlineData("vulkan", "opengl")]
    public async Task GameWindow_PersistedForcedApiAndLiveReloadDriveActualNativeBackendWithoutRestartingProject(string initialApi, string nextApi)
    {
        using var fixture = new TempProject();
        var settings = new ProjectPerformanceSettings
        {
            Graphics = new GraphicsSettings { Api = initialApi, VSync = false, MaxFps = 60, MsaaSamples = 2 },
            Preloading = false
        };
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, settings);
        var project = await fixture.LoadAsync();
        project.Engine.Theme.Pattern = true; project.Engine.Theme.PatternStyle = "waves"; project.Engine.Theme.BackgroundImage = null;
        project.Engine.Game.MainMenu = true;
        await WorkspaceTabsTests.Sta(async () =>
        {
            var window = new GameWindow(project)
            {
                ShowInTaskbar = false, ShowActivated = false, WindowStartupLocation = WindowStartupLocation.Manual,
                Left = -10000, Top = -10000, Width = 640, Height = 480
            };
            var backdrop = WorkspaceTabsTests.Find<AnimatedBackground>(window, "AnimatedBackdrop");
            var stats = GraphicsEnvironment.For(window); Thread? worker = null;
            try
            {
                window.Show();
                await Until(async () => { await ForceRender(backdrop); return stats.Backend == initialApi; },
                    () => "Persisted project API: " + stats.Backend + "; " + stats.Error);
                Assert.Equal(initialApi, GraphicsEnvironment.GetSettings(window).Api);
                Assert.Equal(initialApi, stats.Backend); Assert.Null(stats.Error);
                var layer = Layer(backdrop); worker = layer.Worker.WorkerThread; var oldBitmap = layer.Bitmap;
                long prepared = stats.PreparationCount;

                settings.Graphics = settings.Graphics with { Api = nextApi };
                await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, settings);
                window.ReloadPerformanceSettings();
                await Until(async () => { await ForceRender(backdrop); return stats.Backend == nextApi && stats.PreparationCount > prepared; },
                    () => "Live-reloaded project API: " + stats.Backend + "; " + stats.Error);
                Assert.Equal(nextApi, PerformanceSettingsStore.LoadProject(fixture.ProjectPath).Graphics.Api);
                Assert.Equal(nextApi, GraphicsEnvironment.GetSettings(window).Api);
                Assert.Equal(nextApi, stats.Backend); Assert.Null(stats.Error); Assert.NotSame(oldBitmap, layer.Bitmap);
                Assert.Same(worker, layer.Worker.WorkerThread); Assert.True(window.IsVisible);
                Assert.Equal(Visibility.Collapsed, WorkspaceTabsTests.Find<Border>(window, "StartupErrorOverlay").Visibility);
                long settled = stats.PreparationCount, readback = stats.ReadbackBytes;
                for (int frame = 0; frame < 8; frame++) { backdrop.Step(.02); await ForceRender(backdrop); }
                Assert.Equal(settled, stats.PreparationCount); Assert.Equal(readback, stats.ReadbackBytes);
            }
            finally { window.Close(); if (worker != null) await Task.Run(() => worker.Join(TimeSpan.FromSeconds(5))); }
        });
    }

    private static Window Host(FrameworkElement content, string api, int width, int height)
    {
        var window = new Window { Content = content, Width = width, Height = height, Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false, WindowStyle = WindowStyle.None };
        GraphicsEnvironment.SetSettings(window, new GraphicsSettings { Api = api, VSync = false, MaxFps = 30, MsaaSamples = 2 }); return window;
    }
    private static void AssertBackend(GraphicsTelemetry stats, string api)
    {
        if (api == "auto") Assert.All(stats.Backend.Split(" / ", StringSplitOptions.RemoveEmptyEntries), backend => Assert.Contains(backend, new[] { "vulkan", "opengl" }));
        else Assert.Equal(api, stats.Backend);
    }
    private static GraphicsLayer Layer(object owner) => Assert.IsType<GraphicsLayer>(owner.GetType().GetField("graphics", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner));
    private static byte[] Pixels(BitmapSource bitmap)
    { var pixels = new byte[bitmap.PixelWidth * bitmap.PixelHeight * 4]; bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0); return pixels; }
    private static async Task<byte[]> ForceRender(FrameworkElement content)
    {
        content.InvalidateVisual(); content.UpdateLayout(); await content.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
        var image = new RenderTargetBitmap(Math.Max(1, (int)content.ActualWidth), Math.Max(1, (int)content.ActualHeight), 96, 96, PixelFormats.Pbgra32); image.Render(content); return Pixels(image);
    }
    private static async Task Until(Func<Task<bool>> ready, Func<string> diagnostic)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < TimeSpan.FromSeconds(20)) { if (await ready()) return; await Task.Delay(20); }
        Assert.Fail("Native graphics integration timed out: " + diagnostic());
    }
}
