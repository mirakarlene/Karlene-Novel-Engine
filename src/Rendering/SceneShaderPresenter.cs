using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KNE;

internal sealed record ShaderProgramFile(string Path, CompiledShaderGraph Program);

/// <summary>Applies shader-only files to retained game elements, preserving input on the original controls.</summary>
internal sealed class SceneShaderPresenter : IAsyncDisposable
{
    private sealed record Surface(FrameworkElement Target, Image Output, Brush? OriginalMask);
    private readonly ProjectInfo project;
    private readonly FrameworkElement host;
    private readonly Func<CompiledShaderGraph, FrameworkElement?> resolve;
    private readonly Action<Exception> fail;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<FrameworkElement, Surface> surfaces = [];
    private readonly CancellationTokenSource lifetime = new();
    private ShaderProgramFile[] programs = [];
    private ShaderGpuSession? gpu;
    private bool disposed, disabled, busy;
    private int generation;
    private Task? initialization;
    private Task? cleanup;
    internal bool Disabled => disabled;
    internal int SurfaceCount => surfaces.Count;
    internal SceneShaderPresenter(ProjectInfo project, FrameworkElement host, Func<CompiledShaderGraph, FrameworkElement?> resolve, Action<Exception> fail)
    {
        this.project = project; this.host = host; this.resolve = resolve; this.fail = fail;
        timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        timer.Tick += Tick;
        host.Loaded += Loaded; host.Unloaded += Unloaded;
    }
    internal static ShaderProgramFile[] LoadPrograms(ProjectInfo project)
    {
        var documents = new Dictionary<string, VisualScript>(StringComparer.OrdinalIgnoreCase);
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in project.Files.Where(file => ScriptDocument.IsShaderPath(file.FilePath)))
        {
            if (!EngineLaunch.PluginsEnabled && EngineLaunch.IsPluginPath(Path.GetRelativePath(project.Path, file.FilePath))) continue;
            if (documents.Count >= 64 || new FileInfo(file.FilePath).Length > 2 * 1024 * 1024) throw new InvalidDataException("Не более 64 файлов шейдеров, каждый до 2 МБ.");
            var graph = VisualScript.Parse(File.ReadAllText(PathSafety.EnsureInside(project.Path, file.FilePath)));
            if (!graph.IsShader) throw new InvalidDataException(file.FilePath + ": ожидается граф .kneshader.");
            documents.Add(file.FilePath, graph);
            foreach (var container in graph.Nodes.Where(node => node.Kind == ScriptNodeKind.ShaderContainer))
                if (container.Path.Length > 0) referenced.Add(PathSafety.ResolveRelative(project.Path, container.Path));
        }
        // Compile every file once for validation, including an otherwise rootless container cycle.
        var compiled = documents.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair => { try { return new ShaderProgramFile(pair.Key, ShaderGraphCompiler.Compile(pair.Value, project.Path, pair.Key)); } catch (Exception error) when (error is not OutOfMemoryException) { throw new InvalidDataException(pair.Key + ": " + error.Message, error); } }).ToArray();
        var roots = compiled.Where(item => !referenced.Contains(item.Path)).ToArray();
        if (roots.Length > 16) throw new InvalidDataException("Не более 16 применяемых корневых шейдеров.");
        return roots;
    }
    internal Task InitializeAsync() => initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        try
        {
            var loaded = await Task.Run(() => LoadPrograms(project), lifetime.Token);
            if (disposed) return;
            programs = loaded;
            if (host.IsLoaded && programs.Length > 0) timer.Start();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!disposed) fail(error); }
    }
    internal void SetScene(string? scene)
    {
        bool value = project.Engine.ScenePerformance.For(scene).HideShaders;
        if (value == disabled) return;
        disabled = value; generation++;
        if (disabled) { Restore(); if (gpu is { } old) { gpu = null; _ = old.DisposeAsync(); } }
    }
    private void Loaded(object sender, RoutedEventArgs args) { if (programs.Length > 0 && !disposed) timer.Start(); }
    private void Unloaded(object sender, RoutedEventArgs args) { timer.Stop(); Restore(); }
    private async void Tick(object? sender, EventArgs args)
    {
        if (disposed || disabled || busy || !host.IsLoaded || !host.IsVisible || Window.GetWindow(host)?.WindowState == WindowState.Minimized) return;
        busy = true; int version = generation;
        try
        {
            int fps = project.Engine.Performance.Graphics.MaxFps;
            timer.Interval = TimeSpan.FromMilliseconds(1000d / (fps == 0 ? 30 : Math.Min(30, fps)));
            var groups = programs.Select(file => (file.Program, Target: resolve(file.Program))).Where(item => item.Target?.Parent is Panel)
                .GroupBy(item => item.Target!).ToArray();
            foreach (var stale in surfaces.Keys.Where(target => !groups.Any(group => group.Key == target)).ToArray()) Restore(stale);
            foreach (var group in groups)
            {
                var target = group.Key;
                if (!target.IsVisible || target.ActualWidth < 1 || target.ActualHeight < 1) { Restore(target); continue; }
                if (!surfaces.TryGetValue(target, out var surface))
                {
                    var output = new Image { IsHitTestVisible = false, Stretch = Stretch.Fill };
                    surface = new(target, output, target.OpacityMask); surfaces.Add(target, surface);
                    var parent = (Panel)target.Parent; parent.Children.Insert(parent.Children.IndexOf(target) + 1, output);
                }
                var image = surface.Output;
                image.Width = target.Width; image.Height = target.Height; image.MinWidth = target.MinWidth; image.MinHeight = target.MinHeight;
                image.HorizontalAlignment = target.HorizontalAlignment; image.VerticalAlignment = target.VerticalAlignment;
                image.Margin = target.Margin; image.RenderTransform = target.RenderTransform; image.RenderTransformOrigin = target.RenderTransformOrigin;
                Panel.SetZIndex(image, Panel.GetZIndex(target)); Grid.SetRow(image, Grid.GetRow(target)); Grid.SetColumn(image, Grid.GetColumn(target));
                var settings = project.Engine.Performance.Graphics;
                double quality = settings.TextureQuality switch { "half" => .5, "quarter" => .25, _ => 1 };
                double scale = Math.Min(quality, Math.Min(2048 / target.ActualWidth, 2048 / target.ActualHeight));
                // Bound the captured and returned snapshots together; native resources have their own budget check.
                scale = Math.Min(scale, Math.Sqrt(settings.CacheBudgetBytes / (target.ActualWidth * target.ActualHeight * 12)));
                int width = Math.Max(1, (int)(target.ActualWidth * scale)), height = Math.Max(1, (int)(target.ActualHeight * scale));
                BitmapSource capture;
                target.SetCurrentValue(UIElement.OpacityMaskProperty, surface.OriginalMask);
                try { var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(target); bitmap.Freeze(); capture = bitmap; }
                finally { target.SetCurrentValue(UIElement.OpacityMaskProperty, image.Source == null ? surface.OriginalMask : Brushes.Transparent); }
                var point = target.PointToScreen(new Point());
                var screenEnd = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
                gpu ??= new ShaderGpuSession();
                var result = await gpu.RenderAsync(capture, group.Select(item => item.Program).ToArray(), settings, (float)clock.Elapsed.TotalSeconds, new Vector2((float)point.X, (float)point.Y), lifetime.Token, new Vector2((float)(screenEnd.X - point.X), (float)(screenEnd.Y - point.Y)));
                if (disposed || disabled || version != generation) return;
                image.Source = result; target.SetCurrentValue(UIElement.OpacityMaskProperty, Brushes.Transparent);
            }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) when (!disposed) { timer.Stop(); Restore(); fail(error); }
        finally { busy = false; }
    }
    private void Restore(FrameworkElement target)
    {
        if (!surfaces.Remove(target, out var surface)) return;
        target.SetCurrentValue(UIElement.OpacityMaskProperty, surface.OriginalMask);
        if (surface.Output.Parent is Panel parent) parent.Children.Remove(surface.Output);
        surface.Output.Source = null;
    }
    private void Restore() { foreach (var target in surfaces.Keys.ToArray()) Restore(target); }
    public ValueTask DisposeAsync() => new(cleanup ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if (disposed) return; disposed = true; generation++; lifetime.Cancel(); timer.Stop();
        host.Loaded -= Loaded; host.Unloaded -= Unloaded; Restore();
        if (initialization != null) await initialization;
        if (gpu != null) await gpu.DisposeAsync();
        lifetime.Dispose();
    }
}
