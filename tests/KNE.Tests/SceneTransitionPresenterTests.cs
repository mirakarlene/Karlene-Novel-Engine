using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class SceneTransitionPresenterTests
{
    [Theory]
    [InlineData("fade", false)] [InlineData("fade", true)] [InlineData("dissolve", false)] [InlineData("dissolve", true)]
    public Task SceneTransitionsRenderTheSelectedEntryOrExitAndRemoveEveryTemporaryLayer(string kind, bool entering) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("effect.kneshader", InvertShader());
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["game/scenes/main.krl"] = new() { ["main"] = new() { PostProcessing = false } } } });
        var project = await fixture.LoadAsync(); var root = new Grid { Background = Brushes.Red }; var host = Host(root); SharedGraphicsWorker? worker = kind == "custom" ? SharedGraphicsWorker.Acquire(host) : null; bool released = false;
        try
        {
            host.Show(); Layout(root); var previous = SceneTransitionPresenter.Capture(root)!; root.Background = Brushes.Blue; Layout(root);
            var settings = new SceneTransitionSettings { Kind = kind, Duration = kind == "custom" ? 3 : .4, Shader = "effect.kneshader" };
            var operation = SceneTransitionPresenter.PlayAsync(host, project, root, previous, entering ? new() : settings, entering ? settings : new(), CancellationToken.None);
            var layer = Assert.Single(root.Children.OfType<Grid>()); var front = layer.Children.OfType<Image>().Last();
            await Until(() => front.Opacity is > .15 and < .85 && front.Source != null, operation); Layout(root);
            var middle = SceneTransitionPresenter.Capture(root)!; byte[] pixel = Pixel(middle);
            if (kind == "custom")
            {
                var shaderPixel = Pixel(Assert.IsAssignableFrom<BitmapSource>(front.Source));
                Assert.Equal(entering ? new byte[] { 0, 255, 255, 255 } : new byte[] { 255, 255, 0, 255 }, shaderPixel);
                Assert.InRange(pixel[1], (byte)1, (byte)254);
            }
            else if (kind == "dissolve") { Assert.InRange(pixel[0], (byte)1, (byte)254); Assert.InRange(pixel[2], (byte)1, (byte)254); }
            else if (entering) { Assert.InRange(pixel[0], (byte)1, (byte)254); Assert.Equal(0, pixel[2]); }
            else { Assert.InRange(pixel[2], (byte)1, (byte)254); Assert.Equal(0, pixel[0]); }
            Capture(middle, $"scene-transition-{kind}-{(entering ? "entry" : "exit")}.png");
            await operation; Assert.Empty(root.Children); Assert.Null(front.Source); Assert.Equal(Colors.Blue, ((SolidColorBrush)root.Background).Color);
            Assert.Equal(new byte[] { 255, 0, 0, 255 }, Pixel(SceneTransitionPresenter.Capture(root)!));
            if (worker != null) { Assert.True(worker.WorkerThread.IsAlive); worker.Release(); released = true; Assert.True(await Task.Run(() => worker.WorkerThread.Join(TimeSpan.FromSeconds(5)))); }
        }
        finally { if (worker != null && !released) { worker.Release(); await Task.Run(() => worker.WorkerThread.Join(TimeSpan.FromSeconds(5))); } host.Close(); }
    });

    [GpuTheory]
    [InlineData(false)] [InlineData(true)]
    public Task CustomTransitionsTransformActualGpuPixelsAndReleaseTheirSharedWorkerReference(bool entering)
        => SceneTransitionsRenderTheSelectedEntryOrExitAndRemoveEveryTemporaryLayer("custom", entering);

    [Theory]
    [InlineData("fade")] [InlineData("dissolve")]
    public Task CancellationRemovesTheCoverAndReleasesItsBitmapReferences(string kind) => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); fixture.Write("effect.kneshader", InvertShader()); var project = await fixture.LoadAsync();
        var root = new Grid { Background = Brushes.Red }; var original = new Border(); root.Children.Add(original); var host = Host(root); SharedGraphicsWorker? worker = kind == "custom" ? SharedGraphicsWorker.Acquire(host) : null; bool released = false;
        try
        {
            host.Show(); Layout(root); var previous = SceneTransitionPresenter.Capture(root)!; root.Background = Brushes.Blue; using var canceled = new CancellationTokenSource();
            var operation = SceneTransitionPresenter.PlayAsync(host, project, root, previous, new() { Kind = kind, Duration = 10, Shader = "effect.kneshader" }, new(), canceled.Token);
            var layer = Assert.Single(root.Children.OfType<Grid>()); var images = layer.Children.OfType<Image>().ToArray(); Assert.Equal(2, images.Length); canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Same(original, Assert.Single(root.Children)); Assert.All(images, image => Assert.Null(image.Source));
            Assert.Equal(Colors.Blue, ((SolidColorBrush)root.Background).Color);
            if (worker != null) { Assert.True(worker.WorkerThread.IsAlive); worker.Release(); released = true; Assert.True(await Task.Run(() => worker.WorkerThread.Join(TimeSpan.FromSeconds(5)))); }
        }
        finally { if (worker != null && !released) { worker.Release(); await Task.Run(() => worker.WorkerThread.Join(TimeSpan.FromSeconds(5))); } host.Close(); }
    });

    [GpuTheory]
    [InlineData("custom")]
    public Task CancelingACustomGpuTransitionReleasesItsSharedWorkerReference(string kind) => CancellationRemovesTheCoverAndReleasesItsBitmapReferences(kind);

    [Fact]
    public Task HeldFrameCoversAsyncWorkAndDisposalRestoresTheOriginalControlsWithoutRetainingTheImage() => WorkspaceTabsTests.Sta(() =>
    {
        var root = new Grid { Background = Brushes.Red }; root.Measure(new(100, 60)); root.Arrange(new(0, 0, 100, 60)); var previous = SceneTransitionPresenter.Capture(root)!;
        var original = new Button { Content = "Unchanged" }; root.Children.Add(original);
        var hold = SceneTransitionPresenter.Hold(root, previous); var cover = Assert.Single(root.Children.OfType<Image>());
        Assert.Same(previous, cover.Source); Assert.True(cover.IsHitTestVisible); Assert.Equal(int.MaxValue, Panel.GetZIndex(cover));
        hold.Dispose(); Assert.Same(original, Assert.Single(root.Children)); Assert.Null(cover.Source); Assert.Equal("Unchanged", original.Content);
        hold.Dispose(); Assert.Same(original, Assert.Single(root.Children)); return Task.CompletedTask;
    });

    [Theory]
    [InlineData(100, 60, 100, 60)] [InlineData(8000, 4000, 4096, 2048)] [InlineData(4000, 6000, 1440, 2160)]
    public Task SnapshotsKeepAspectRatioAndBoundThePixelDimensionsWithoutChangingLayout(int width, int height, int expectedWidth, int expectedHeight) => WorkspaceTabsTests.Sta(() =>
    {
        var root = new Grid { Background = Brushes.Red }; Assert.Null(SceneTransitionPresenter.Capture(root)); root.Measure(new(width, height)); root.Arrange(new(0, 0, width, height)); root.UpdateLayout();
        var image = SceneTransitionPresenter.Capture(root)!; Assert.Equal(expectedWidth, image.PixelWidth); Assert.Equal(expectedHeight, image.PixelHeight); Assert.True(image.IsFrozen);
        Assert.Equal(width, root.ActualWidth); Assert.Equal(height, root.ActualHeight); Assert.Equal(new byte[] { 0, 0, 255, 255 }, Pixel(image)); return Task.CompletedTask;
    });

    [Fact]
    public Task DisabledHiddenAndMinimizedTransitionsLeaveTheOriginalContentUntouched() => WorkspaceTabsTests.Sta(async () =>
    {
        using var fixture = new TempProject(); var project = await fixture.LoadAsync(); var root = new Grid { Background = Brushes.Red }; var original = new Border(); root.Children.Add(original); var host = Host(root);
        try
        {
            Layout(root); var previous = SceneTransitionPresenter.Capture(root)!;
            await SceneTransitionPresenter.PlayAsync(host, project, root, previous, new() { Kind = "fade" }, new(), CancellationToken.None); Assert.Same(original, Assert.Single(root.Children));
            host.Show(); Layout(root); await SceneTransitionPresenter.PlayAsync(host, project, root, previous, new(), new(), CancellationToken.None); Assert.Same(original, Assert.Single(root.Children));
            host.WindowState = WindowState.Minimized; await SceneTransitionPresenter.PlayAsync(host, project, root, previous, new() { Kind = "fade" }, new(), CancellationToken.None); Assert.Same(original, Assert.Single(root.Children));
        }
        finally { host.Close(); }
    });

    static Window Host(Grid root) => new() { Content = root, Width = 160, Height = 120, Left = -10000, Top = -10000, ShowInTaskbar = false, ShowActivated = false, WindowStyle = WindowStyle.None };
    static string InvertShader()
    {
        var invert = BlockLibrary.Create(ScriptNodeKind.ShaderInvert); invert.Id = "invert"; var output = BlockLibrary.Create(ScriptNodeKind.ShaderOutput); output.Id = "output"; output.Shader!.Inputs["Color"] = new(invert.Id);
        return new VisualScript { IsShader = true, FunctionName = "effect", Nodes = [invert, output] }.Serialize();
    }
    static void Layout(FrameworkElement root) { root.Measure(new(160, 120)); root.Arrange(new(0, 0, 160, 120)); root.UpdateLayout(); }
    static byte[] Pixel(BitmapSource bitmap)
    {
        var normalized = bitmap.Format == PixelFormats.Pbgra32 ? bitmap : new FormatConvertedBitmap(bitmap, PixelFormats.Pbgra32, null, 0); var pixel = new byte[4]; normalized.CopyPixels(new Int32Rect(bitmap.PixelWidth / 2, bitmap.PixelHeight / 2, 1, 1), pixel, 4, 0); return pixel;
    }
    static async Task Until(Func<bool> predicate, Task operation)
    {
        var time = Stopwatch.StartNew(); while (!predicate() && !operation.IsCompleted && time.Elapsed < TimeSpan.FromSeconds(10)) await Task.Delay(5);
        if (operation.IsFaulted) await operation; Assert.True(predicate(), "Transition did not expose an intermediate rendered frame.");
    }
    static void Capture(BitmapSource image, string name)
    {
        string? directory = Environment.GetEnvironmentVariable("KNE_EDITOR_RENDER"); if (directory == null) return; Directory.CreateDirectory(directory); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var file = File.Create(Path.Combine(directory, name)); encoder.Save(file);
    }
}
