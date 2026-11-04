using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Temporary scene snapshots, without altering the game's persistent layout.</summary>
internal static class SceneTransitionPresenter
{
    private sealed class Cover(Grid root, Image image) : IDisposable
    {
        public void Dispose() { root.Children.Remove(image); image.Source = null; }
    }
    internal static IDisposable Hold(Grid root, BitmapSource previous)
    {
        var image = new Image { Source = previous, Stretch = Stretch.Fill, IsHitTestVisible = true };
        Panel.SetZIndex(image, int.MaxValue); root.Children.Add(image); return new Cover(root, image);
    }
    internal static BitmapSource? Capture(FrameworkElement target)
    {
        if (target.ActualWidth < 1 || target.ActualHeight < 1) return null;
        double scale = Math.Min(1, Math.Min(4096 / target.ActualWidth, 2160 / target.ActualHeight));
        int width = Math.Max(1, (int)(target.ActualWidth * scale)), height = Math.Max(1, (int)(target.ActualHeight * scale));
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(new VisualBrush(target) { AutoLayoutContent = false }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    internal static async Task PlayAsync(Window host, ProjectInfo project, Grid root, BitmapSource previous,
        SceneTransitionSettings exit, SceneTransitionSettings entry, CancellationToken token)
    {
        if (exit.Kind == "none" && entry.Kind == "none" || host.WindowState == WindowState.Minimized || !host.IsVisible) return;
        token.ThrowIfCancellationRequested(); exit.Validate(project.Path); entry.Validate(project.Path);
        root.UpdateLayout(); var current = Capture(root); if (current == null) return;
        var layer = new Grid { Background = Brushes.Black, ClipToBounds = true, Focusable = true };
        var back = new Image { Stretch = Stretch.Fill }; var front = new Image { Stretch = Stretch.Fill };
        layer.Children.Add(back); layer.Children.Add(front); Panel.SetZIndex(layer, int.MaxValue); root.Children.Add(layer);
        // Block pointer interaction while the snapshot covers controls; execution owns keyboard actions.
        layer.PreviewMouseDown += (_, args) => args.Handled = true;
        ShaderGpuSession? gpu = null;
        try
        {
            async Task Stage(SceneTransitionSettings settings, bool entering)
            {
                if (settings.Kind == "none") return;
                var programs = settings.Kind == "custom" ? SceneShaderPresenter.LoadPrograms(project, [settings.Shader!]).Select(p => p.Program).ToArray() : [];
                back.Source = null;
                if (settings.Kind == "dissolve")
                {
                    if (!entering || exit.Kind == "dissolve") back.Source = current;
                    else if (exit.Kind == "none") back.Source = previous;
                }
                front.Source = entering ? current : previous;
                front.Opacity = entering ? 0 : 1;
                var watch = Stopwatch.StartNew();
                do
                {
                    token.ThrowIfCancellationRequested();
                    double progress = Math.Clamp(watch.Elapsed.TotalSeconds / settings.Duration, 0, 1);
                    if (programs.Length > 0)
                    {
                        gpu ??= new ShaderGpuSession(host);
                        front.Source = await gpu.RenderAsync(entering ? current : previous, programs, project.Engine.Performance.Graphics, (float)progress, token: token);
                    }
                    front.Opacity = entering ? progress : 1 - progress;
                    if (progress >= 1) break;
                    await Task.Delay(16, token);
                } while (true);
            }
            await Stage(exit, false); await Stage(entry, true);
        }
        finally
        {
            root.Children.Remove(layer); front.Source = back.Source = null;
            if (gpu != null) await gpu.DisposeAsync();
        }
    }
}
