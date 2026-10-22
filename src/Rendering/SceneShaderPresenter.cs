using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace KNE;

internal sealed record ShaderProgramFile(string Path, CompiledShaderGraph Program);

/// <summary>Loads only invoked shaders; retains snapshots until their scene content changes.</summary>
internal sealed class SceneShaderPresenter : IAsyncDisposable
{
    private sealed class Surface(FrameworkElement target, Image output, Brush? originalMask)
    {
        internal readonly FrameworkElement Target = target;
        internal readonly Image Output = output;
        internal readonly Brush? OriginalMask = originalMask;
        internal BitmapSource? Capture;
        internal long Revision = -1, RenderedRevision = -1;
        internal int Width, Height;
        internal Point Origin, ScreenSize;
        internal int AnimationStamp;
    }
    private readonly ProjectInfo project;
    private readonly FrameworkElement host;
    private readonly Func<CompiledShaderGraph, FrameworkElement?> resolve;
    private readonly Action<Exception> fail;
    private readonly DispatcherTimer timer;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<FrameworkElement, Surface> surfaces = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly RoutedEventHandler contentChanged;
    private ShaderProgramFile[] programs = [];
    private string[] requested = [];
    private ShaderGpuSession? gpu;
    private bool disposed, disabled, busy;
    private int generation;
    private long contentRevision;
    private SceneVisuals? previousVisuals;
    private StoryFrame? previousStory;
    private RuntimeWindowSettings? previousWindow;
    private string[] previousOptions = [];
    private ChoiceOptionState[]? previousChoices;
    private IInputElement? hovered;
    private Task activation = Task.CompletedTask;
    private Task shutdown = Task.CompletedTask;
    private Task? cleanup;
    internal bool Disabled => disabled;
    internal int SurfaceCount => surfaces.Count;
    internal long Captures { get; private set; }
    internal long RenderCount { get; private set; }
    internal SceneShaderPresenter(ProjectInfo project, FrameworkElement host, Func<CompiledShaderGraph, FrameworkElement?> resolve, Action<Exception> fail)
    {
        this.project = project; this.host = host; this.resolve = resolve; this.fail = fail;
        timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher) { Interval = TimeSpan.FromMilliseconds(1000d / 30) };
        timer.Tick += Tick;
        host.Loaded += Loaded; host.Unloaded += Unloaded;
        contentChanged = (_, _) => NotifyContentChanged();
        host.AddHandler(TextBoxBase.TextChangedEvent, contentChanged, true);
        host.PreviewMouseDown += InputChanged; host.MouseMove += HoverChanged;
    }
    internal static ShaderProgramFile[] LoadPrograms(ProjectInfo project, IEnumerable<string>? paths = null)
    {
        var result = new List<ShaderProgramFile>();
        string[] selected = (paths ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (selected.Length > 16) throw new InvalidDataException("Одновременно допускается не более 16 шейдеров.");
        foreach (string relative in selected)
        {
            string full = PathSafety.ResolveRelative(project.Path, relative);
            if (!ScriptDocument.IsShaderPath(full)) throw new InvalidDataException("Ожидается файл .kneshader.");
            if (!EngineLaunch.PluginsEnabled && EngineLaunch.IsPluginPath(relative)) continue;
            if (new FileInfo(full).Length > 2 * 1024 * 1024) throw new InvalidDataException("Шейдер больше 2 МБ.");
            try
            {
                var compiled = ShaderGraphCompiler.Compile(VisualScript.Parse(File.ReadAllText(full)), project.Path, full);
                // An unmodified texture needs no capture, native context, pipeline or framebuffer.
                if (!compiled.IsIdentity) result.Add(new(full, compiled));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { throw new InvalidDataException(full + ": " + error.Message, error); }
        }
        return result.ToArray();
    }
    internal Task InitializeAsync() => Task.CompletedTask;
    internal Task SetActiveAsync(IEnumerable<string> paths)
    {
        if (disposed) return Task.CompletedTask;
        string[] next = paths.ToArray();
        if (requested.SequenceEqual(next, StringComparer.OrdinalIgnoreCase)) return activation;
        requested = next;
        int version = ++generation;
        timer.Stop(); Restore(); programs = [];
        activation = ActivateAsync(next, version);
        return activation;
    }
    private async Task ActivateAsync(string[] paths, int version)
    {
        try
        {
            var loaded = paths.Length == 0 ? [] : await Task.Run(() => LoadPrograms(project, paths), lifetime.Token);
            if (disposed || version != generation) return;
            programs = loaded; NotifyContentChanged();
            if (programs.Length == 0) ReleaseGpu();
            else if (host.IsLoaded && !disabled) timer.Start();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!disposed && version == generation) { ReleaseGpu(); fail(error); } }
    }
    internal void NotifyContentChanged() => contentRevision++;
    internal void SetFrame(RuntimeWindowSettings window, SceneVisuals visuals, StoryFrame story)
    {
        if (programs.Length == 0) return;
        // Worker IPC creates new arrays even for unchanged frames. Compare their content,
        // while versioned UI styles and active shaders are handled by their own revisions.
        var normalizedVisuals = visuals with { AudioChannels = Array.Empty<AudioChannelState>(), UiConfigurations = Array.Empty<GameplayUiConfiguration>(), Shaders = Array.Empty<string>(), ShaderSuppressions = Array.Empty<string>() };
        var normalizedStory = story with { Options = Array.Empty<string>(), ChoiceOptions = null, Triggers = Array.Empty<TriggerSettings>() };
        if (window != previousWindow || normalizedVisuals != previousVisuals || normalizedStory != previousStory ||
            !previousOptions.SequenceEqual(story.Options) || !(previousChoices ?? []).SequenceEqual(story.ChoiceOptions ?? [])) NotifyContentChanged();
        previousWindow = window; previousVisuals = normalizedVisuals; previousStory = normalizedStory;
        previousOptions = story.Options.ToArray(); previousChoices = story.ChoiceOptions?.ToArray();
    }
    private void InputChanged(object sender, System.Windows.Input.MouseEventArgs args) => NotifyContentChanged();
    private void HoverChanged(object sender, System.Windows.Input.MouseEventArgs args)
    {
        var current = System.Windows.Input.Mouse.DirectlyOver;
        if (!ReferenceEquals(current, hovered)) { hovered = current; NotifyContentChanged(); }
    }
    internal void SetScene(string? scene)
    {
        bool value = project.Engine.ScenePerformance.For(scene).HideShaders;
        if (value == disabled) return;
        disabled = value; NotifyContentChanged();
        if (disabled) { timer.Stop(); Restore(); ReleaseGpu(); }
        else if (programs.Length > 0 && host.IsLoaded) timer.Start();
    }
    private void ReleaseGpu()
    {
        if (gpu is not { } old) return;
        gpu = null;
        shutdown = Task.WhenAll(shutdown, old.DisposeAsync().AsTask());
    }
    private void Loaded(object sender, RoutedEventArgs args) { NotifyContentChanged(); if (programs.Length > 0 && !disposed && !disabled) timer.Start(); }
    private void Unloaded(object sender, RoutedEventArgs args) { timer.Stop(); Restore(); ReleaseGpu(); }
    private static bool Live(DependencyObject target, ref HashCode values)
    {
        if (target is UIElement { IsVisible: false }) return false;
        bool live = target is AnimatedBackground background && background.IsAnimating || target is EffectTextPresenter { IsAnimating: true } || target is ChoiceOptionButton { IsAnimating: true };
        // WPF keeps completed HoldEnd clocks attached. Their presence alone must not
        // cause another capture forever: only changing animated values dirty the input.
        if (target is UIElement element)
        {
            if (element.HasAnimatedProperties || element.RenderTransform.HasAnimatedProperties)
            { values.Add(element.Opacity); values.Add(element.RenderSize); values.Add(element.RenderTransform.Value); }
            if (element is FrameworkElement { LayoutTransform.HasAnimatedProperties: true } framework) values.Add(framework.LayoutTransform.Value);
            if (element.Effect is System.Windows.Media.Effects.BlurEffect blur) values.Add(blur.Radius);
            if (element.Effect is System.Windows.Media.Effects.DropShadowEffect shadow)
            { values.Add(shadow.BlurRadius); values.Add(shadow.Color); values.Add(shadow.Direction); values.Add(shadow.Opacity); values.Add(shadow.ShadowDepth); }
            BrushStamp(element.OpacityMask, ref values);
        }
        if (target is Border border) { BrushStamp(border.Background, ref values); BrushStamp(border.BorderBrush, ref values); }
        if (target is Control control) { BrushStamp(control.Background, ref values); BrushStamp(control.Foreground, ref values); BrushStamp(control.BorderBrush, ref values); }
        if (target is Panel panel) BrushStamp(panel.Background, ref values);
        if (target is TextBlock text) BrushStamp(text.Foreground, ref values);
        if (target is System.Windows.Shapes.Shape shape) { BrushStamp(shape.Fill, ref values); BrushStamp(shape.Stroke, ref values); }
        int children = VisualTreeHelper.GetChildrenCount(target);
        for (int i = 0; i < children; i++) live |= Live(VisualTreeHelper.GetChild(target, i), ref values);
        return live;
    }
    private static void BrushStamp(Brush? brush, ref HashCode values)
    {
        if (brush == null || brush.IsFrozen) return;
        if (brush.HasAnimatedProperties || brush.Transform.HasAnimatedProperties || brush.RelativeTransform.HasAnimatedProperties || brush is GradientBrush gradient && gradient.GradientStops.Any(stop => stop.HasAnimatedProperties))
        {
            values.Add(brush.Opacity); values.Add(brush.Transform.Value); values.Add(brush.RelativeTransform.Value);
            if (brush is SolidColorBrush solid) values.Add(solid.Color);
            if (brush is GradientBrush stops) foreach (var stop in stops.GradientStops) { values.Add(stop.Color); values.Add(stop.Offset); }
            if (brush is LinearGradientBrush linear) { values.Add(linear.StartPoint); values.Add(linear.EndPoint); }
            if (brush is RadialGradientBrush radial) { values.Add(radial.Center); values.Add(radial.GradientOrigin); values.Add(radial.RadiusX); values.Add(radial.RadiusY); }
        }
    }
    private async void Tick(object? sender, EventArgs args)
    {
        if (disposed || disabled || busy || programs.Length == 0 || !host.IsLoaded || !host.IsVisible || Window.GetWindow(host)?.WindowState == WindowState.Minimized) return;
        busy = true; int version = generation;
        try
        {
            int fps = project.Engine.Performance.Graphics.MaxFps;
            timer.Interval = TimeSpan.FromMilliseconds(1000d / (fps == 0 ? 30 : Math.Min(30, fps)));
            var groups = programs.Select(file => (file.Program, Target: resolve(file.Program))).Where(item => item.Target?.Parent is Panel).GroupBy(item => item.Target!).ToArray();
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
                Grid.SetRowSpan(image, Grid.GetRowSpan(target)); Grid.SetColumnSpan(image, Grid.GetColumnSpan(target));
                var settings = project.Engine.Performance.Graphics;
                double quality = settings.TextureQuality switch { "half" => .5, "quarter" => .25, _ => 1 };
                double scale = Math.Min(quality, Math.Min(2048 / target.ActualWidth, 2048 / target.ActualHeight));
                // Account for capture, upload, retained GPU surfaces and the returned bitmap together.
                scale = Math.Min(scale, Math.Sqrt(settings.CacheBudgetBytes / (target.ActualWidth * target.ActualHeight * 32)));
                int width = Math.Max(1, (int)(target.ActualWidth * scale)), height = Math.Max(1, (int)(target.ActualHeight * scale));
                var point = target.PointToScreen(new Point());
                var screenEnd = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
                var screenSize = new Point(screenEnd.X - point.X, screenEnd.Y - point.Y);
                var animation = new HashCode(); bool live = Live(target, ref animation); int animationStamp = animation.ToHashCode();
                bool changed = surface.Revision != contentRevision || surface.Width != width || surface.Height != height || live || surface.AnimationStamp != animationStamp;
                if (!changed && surface.RenderedRevision == surface.Revision && surface.Origin == point && surface.ScreenSize == screenSize && !group.Any(item => item.Program.Animated)) continue;
                if (changed || surface.Capture == null)
                {
                    target.SetCurrentValue(UIElement.OpacityMaskProperty, surface.OriginalMask);
                    try
                    {
                        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                        bitmap.Render(target); bitmap.Freeze(); surface.Capture = bitmap; Captures++;
                    }
                    finally { target.SetCurrentValue(UIElement.OpacityMaskProperty, image.Source == null ? surface.OriginalMask : Brushes.Transparent); }
                    surface.Width = width; surface.Height = height; surface.Revision = contentRevision;
                    surface.AnimationStamp = animationStamp;
                }
                gpu ??= new ShaderGpuSession(Window.GetWindow(host));
                var currentGpu = gpu;
                var result = await currentGpu.RenderAsync(surface.Capture!, group.Select(item => item.Program).ToArray(), settings, (float)clock.Elapsed.TotalSeconds, new Vector2((float)point.X, (float)point.Y), lifetime.Token, new Vector2((float)(screenEnd.X - point.X), (float)(screenEnd.Y - point.Y)));
                if (disposed || disabled || version != generation || !ReferenceEquals(gpu, currentGpu)) return;
                RenderCount++; surface.RenderedRevision = surface.Revision;
                surface.Origin = point; surface.ScreenSize = screenSize;
                image.Source = result; target.SetCurrentValue(UIElement.OpacityMaskProperty, Brushes.Transparent);
            }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) when (!disposed) { timer.Stop(); Restore(); ReleaseGpu(); fail(error); }
        finally { busy = false; }
    }
    private void Restore(FrameworkElement target)
    {
        if (!surfaces.Remove(target, out var surface)) return;
        target.SetCurrentValue(UIElement.OpacityMaskProperty, surface.OriginalMask);
        if (surface.Output.Parent is Panel parent) parent.Children.Remove(surface.Output);
        surface.Output.Source = null; surface.Capture = null;
    }
    private void Restore() { foreach (var target in surfaces.Keys.ToArray()) Restore(target); }
    public ValueTask DisposeAsync() => new(cleanup ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if (disposed) return; disposed = true; generation++; lifetime.Cancel(); timer.Stop();
        host.Loaded -= Loaded; host.Unloaded -= Unloaded;
        host.RemoveHandler(TextBoxBase.TextChangedEvent, contentChanged);
        host.PreviewMouseDown -= InputChanged; host.MouseMove -= HoverChanged;
        Restore(); ReleaseGpu();
        await activation; await shutdown;
        lifetime.Dispose();
    }
}
