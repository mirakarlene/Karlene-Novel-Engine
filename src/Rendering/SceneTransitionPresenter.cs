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
    internal static BitmapSource? Capture(FrameworkElement target, double rasterScale = 1)
    {
        if (target.ActualWidth < 1 || target.ActualHeight < 1) return null;
        double scale = Math.Min(rasterScale, Math.Min(4096 / target.ActualWidth, 2160 / target.ActualHeight));
        int width = Math.Max(1, (int)(target.ActualWidth * scale)), height = Math.Max(1, (int)(target.ActualHeight * scale));
        var visual = new DrawingVisual();
        using (var draw = visual.RenderOpen()) draw.DrawRectangle(new VisualBrush(target) { AutoLayoutContent = false }, null, new Rect(0, 0, width, height));
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); bitmap.Freeze(); return bitmap;
    }
    internal static async Task PlayMenuAsync(Grid root, BitmapSource previous, MenuTransitionSettings settings, CancellationToken token,
        double rasterScale = 1)
    {
        if (!settings.IsEnabled) return;
        settings.Validate();
        token.ThrowIfCancellationRequested();
        root.UpdateLayout();
        if (Capture(root, rasterScale) is not { } current) return;
        await PlayFramesAsync(root, previous, settings, token, current);
    }

    private static async Task PlayFramesAsync(Grid root, BitmapSource previous, MenuTransitionSettings settings,
        CancellationToken token, BitmapSource current)
    {
        var layer = new Grid { Background = Brushes.Black, ClipToBounds = true };
        var back = new Image { Source = previous, Stretch = Stretch.Fill };
        var front = new Image { Source = current, Stretch = Stretch.Fill };
        var move = new TranslateTransform();
        var previousMove = new TranslateTransform();
        var zoom = new ScaleTransform(1, 1);
        var previousZoom = new ScaleTransform(1, 1);
        var clip = new RectangleGeometry();
        var tiles = new GeometryGroup();
        if (settings.Kind is "slideaway" or "easeout" || settings is { Kind: "iris", Direction: "out" })
        {
            back.Source = current;
            front.Source = previous;
        }
        if (settings.Kind is "wipe" or "iris") front.Clip = clip;
        if (settings.Kind is "slide" or "slideaway" or "easein" or "easeout" or "push" or "hpunch" or "vpunch") front.RenderTransform = move;
        if (settings.Kind == "push") back.RenderTransform = previousMove;
        if (settings.Kind is "zoomin" or "zoomout" or "zoominout")
        {
            front.RenderTransform = zoom;
            back.RenderTransform = previousZoom;
            front.RenderTransformOrigin = back.RenderTransformOrigin = new(.5, .5);
            if (settings.Kind == "zoomout")
            {
                back.Source = current;
                front.Source = previous;
            }
        }
        if (settings.Kind is "blinds" or "squares")
        {
            front.Clip = tiles;
            int count = settings.Kind == "blinds" ? 12 : 96;
            for (int index = 0; index < count; index++) tiles.Children.Add(new RectangleGeometry());
        }
        if (settings.Kind == "pixellate") RenderOptions.SetBitmapScalingMode(front, BitmapScalingMode.NearestNeighbor);
        layer.Children.Add(back);
        layer.Children.Add(front);
        Panel.SetZIndex(layer, int.MaxValue);
        root.Children.Add(layer);
        layer.PreviewMouseDown += (_, args) => args.Handled = true;
        try
        {
            var watch = Stopwatch.StartNew();
            int lastPixelStage = -1;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                double progress = Math.Min(1, watch.Elapsed.TotalSeconds / settings.Duration);
                double width = root.ActualWidth, height = root.ActualHeight;
                switch (settings.Kind)
                {
                    case "fade":
                        back.Source = null;
                        front.Source = progress < 0.5 ? previous : current;
                        front.Opacity = Math.Abs(2 * progress - 1);
                        break;
                    case "dissolve":
                        front.Opacity = progress;
                        break;
                    case "wipe":
                        clip.Rect = settings.Direction switch
                        {
                            "left" => new(width * (1 - progress), 0, width * progress, height),
                            "up" => new(0, height * (1 - progress), width, height * progress),
                            "down" => new(0, 0, width, height * progress),
                            _ => new(0, 0, width * progress, height)
                        };
                        break;
                    case "slide": case "slideaway": case "easein": case "easeout": case "push":
                        double travel = settings.Kind is "easein" or "easeout" ? (1 - Math.Cos(Math.PI * progress)) / 2 : progress;
                        double offset = settings.Kind is "slideaway" or "easeout" ? travel : travel - 1;
                        move.X = settings.Direction == "left" ? -width * offset : settings.Direction == "right" ? width * offset : 0;
                        move.Y = settings.Direction == "up" ? -height * offset : settings.Direction == "down" ? height * offset : 0;
                        if (settings.Kind == "push")
                        {
                            previousMove.X = settings.Direction == "left" ? -width * progress : settings.Direction == "right" ? width * progress : 0;
                            previousMove.Y = settings.Direction == "up" ? -height * progress : settings.Direction == "down" ? height * progress : 0;
                        }
                        break;
                    case "zoomin":
                        zoom.ScaleX = zoom.ScaleY = progress;
                        break;
                    case "zoomout":
                        zoom.ScaleX = zoom.ScaleY = 1 - progress;
                        break;
                    case "zoominout":
                        zoom.ScaleX = zoom.ScaleY = progress;
                        previousZoom.ScaleX = previousZoom.ScaleY = 1 - progress;
                        break;
                    case "blinds": case "squares":
                        int rows = settings.Kind == "blinds" ? 1 : 8;
                        double tileWidth = width / 12, tileHeight = height / rows;
                        for (int index = 0; index < tiles.Children.Count; index++)
                        {
                            double insetX = rows == 1 ? 0 : tileWidth * (1 - progress) / 2;
                            double insetY = rows == 1 ? 0 : tileHeight * (1 - progress) / 2;
                            ((RectangleGeometry)tiles.Children[index]).Rect = new(index % 12 * tileWidth + insetX,
                                index / 12 * tileHeight + insetY, tileWidth * progress, tileHeight - 2 * insetY);
                        }
                        break;
                    case "hpunch": case "vpunch":
                        double shake = Math.Sin(progress * Math.Tau * 2) * (1 - progress) * Math.Min(width, height) / 40;
                        if (settings.Kind == "hpunch") move.X = shake;
                        else move.Y = shake;
                        break;
                    case "iris":
                        double aperture = settings.Direction == "in" ? progress : 1 - progress;
                        clip.Rect = new(width * (1 - aperture) / 2, height * (1 - aperture) / 2, width * aperture, height * aperture);
                        break;
                    case "pixellate":
                        int stage = Math.Min(12, (int)(progress * 13));
                        if (stage != lastPixelStage)
                        {
                            BitmapSource source = stage <= 6 ? previous : current;
                            int block = 1 << (stage <= 6 ? stage : 12 - stage);
                            double scale = Math.Max(1d / block, 1d / Math.Min(source.PixelWidth, source.PixelHeight));
                            front.Source = block == 1 ? source : new TransformedBitmap(source, new ScaleTransform(scale, scale));
                            lastPixelStage = stage;
                        }
                        break;
                }
                if (progress >= 1) break;
                await Task.Delay(16, token);
            }
        }
        finally
        {
            root.Children.Remove(layer);
            front.Source = back.Source = null;
        }
    }

    internal static async Task PlayAsync(Window host, ProjectInfo project, Grid root, BitmapSource previous,
        SceneTransitionSettings exit, SceneTransitionSettings entry, CancellationToken token, double rasterScale = 1)
    {
        if (exit.Kind == "none" && entry.Kind == "none" || host.WindowState == WindowState.Minimized || !host.IsVisible) return;
        token.ThrowIfCancellationRequested(); exit.Validate(project.Path); entry.Validate(project.Path);
        root.UpdateLayout(); var current = Capture(root, rasterScale); if (current == null) return;
        var layer = new Grid { Background = Brushes.Black, ClipToBounds = true, Focusable = true };
        var back = new Image { Stretch = Stretch.Fill }; var front = new Image { Stretch = Stretch.Fill };
        layer.Children.Add(back); layer.Children.Add(front); Panel.SetZIndex(layer, int.MaxValue); root.Children.Add(layer);
        // Block pointer interaction while the snapshot covers controls; execution owns keyboard actions.
        layer.PreviewMouseDown += (_, args) => args.Handled = true;
        ShaderGpuSession? gpu = null;
        BitmapSource? black = null;
        BitmapSource BlackFrame() => black ??= BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 0, 0, 0, 255 }, 4);
        try
        {
            async Task Stage(SceneTransitionSettings settings, bool entering)
            {
                if (settings.Kind == "none") return;
                if (SceneTransitionSettings.Presets.TryGetValue(settings.Kind, out var preset))
                {
                    BitmapSource from = entering && exit.Kind != "none"
                        ? exit.Kind == "dissolve" ? current : BlackFrame()
                        : previous;
                    BitmapSource to = entering || entry.Kind == "none" ? current : BlackFrame();
                    if (!entering && preset.Effect is "hpunch" or "vpunch") to = previous;
                    await PlayFramesAsync(root, from, new MenuTransitionSettings
                    {
                        Kind = preset.Effect, Direction = preset.Direction, Duration = settings.Duration
                    }, token, to);
                    return;
                }
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
