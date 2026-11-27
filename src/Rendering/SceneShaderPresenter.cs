using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using RgbaFloat = Veldrid.RgbaFloat;

namespace KNE;

internal sealed record ShaderProgramFile(string Path, CompiledShaderGraph Program);

/// <summary>Loads only invoked shaders; retains snapshots until their scene content changes.</summary>
internal sealed class SceneShaderPresenter : IAsyncDisposable
{
    private readonly record struct WaveInput(BitmapSource Atlas, Rect Bounds, Color Color, bool CaptureForeground);
    private sealed record TargetGroup(FrameworkElement Target, CompiledShaderGraph[] Effects, bool Animated);
    private sealed class Surface(FrameworkElement target, Image output, Brush? originalMask)
    {
        internal readonly FrameworkElement Target = target;
        internal readonly Image Output = output;
        internal readonly Brush? OriginalMask = originalMask;
        internal RenderTargetBitmap? Capture;
        internal GraphicsTexture? Snapshot;
        internal WaveInput? Waves;
        internal long Revision = -1, RenderedRevision = -1;
        internal int Width, Height;
        internal Point Origin, ScreenSize;
        internal int AnimationStamp;
    }
    private readonly ProjectInfo project;
    private readonly FrameworkElement host;
    private readonly Func<CompiledShaderGraph, FrameworkElement?> resolve;
    private readonly Action<Exception> fail;
    private readonly Panel? waveLayer;
    private readonly Panel? sceneLayer;
    private readonly Func<double>? rasterScale;
    private readonly DispatcherTimer timer;
    private readonly FramePacer pacer = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double lastComposition = double.NegativeInfinity;
    private readonly Dictionary<FrameworkElement, Surface> surfaces = [];
    private readonly CancellationTokenSource lifetime = new();
    private readonly RoutedEventHandler contentChanged;
    private ShaderProgramFile[] programs = [];
    private FrameworkElement?[] resolvedTargets = [];
    private TargetGroup[] targetGroups = [];
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
    private static readonly IReadOnlyDictionary<string, string> EmptyTextMap = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, string> previousScreenTexts = EmptyTextMap;
    private IReadOnlyDictionary<string, string> previousFonts = EmptyTextMap;
    private IInputElement? hovered;
    private Task activation = Task.CompletedTask;
    private Task shutdown = Task.CompletedTask;
    private Task? cleanup;
    internal bool Disabled => disabled;
    internal int SurfaceCount => surfaces.Count;
    internal long Captures { get; private set; }
    internal long RenderCount { get; private set; }
    internal SceneShaderPresenter(ProjectInfo project, FrameworkElement host, Func<CompiledShaderGraph, FrameworkElement?> resolve,
        Action<Exception> fail, Panel? waveLayer = null, Panel? sceneLayer = null, Func<double>? rasterScale = null)
    {
        this.project = project; this.host = host; this.resolve = resolve; this.fail = fail;
        this.waveLayer = waveLayer;
        this.sceneLayer = sceneLayer;
        this.rasterScale = rasterScale;
        timer = new DispatcherTimer(DispatcherPriority.Background, host.Dispatcher);
        timer.Tick += TimerTick;
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
        if (requested.SequenceEqual(paths, StringComparer.OrdinalIgnoreCase)) return activation;
        string[] next = paths.ToArray();
        requested = next;
        int version = ++generation;
        StopRendering(); Restore(); programs = []; resolvedTargets = []; targetGroups = [];
        activation = ActivateAsync(next, version);
        return activation;
    }
    private async Task ActivateAsync(string[] paths, int version)
    {
        try
        {
            var loaded = paths.Length == 0 ? [] : await Task.Run(() => LoadPrograms(project, paths), lifetime.Token);
            if (disposed || version != generation) return;
            programs = loaded; resolvedTargets = new FrameworkElement?[loaded.Length]; NotifyContentChanged();
            if (programs.Length == 0) ReleaseGpu();
            else if (host.IsLoaded && !disabled) StartRendering();
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
        var normalizedVisuals = visuals with { AudioChannels = Array.Empty<AudioChannelState>(), UiConfigurations = Array.Empty<GameplayUiConfiguration>(), Shaders = Array.Empty<string>(), ShaderSuppressions = Array.Empty<string>(), ScreenTexts = EmptyTextMap, Fonts = EmptyTextMap };
        var normalizedStory = story with { Options = Array.Empty<string>(), ChoiceOptions = null, Triggers = Array.Empty<TriggerSettings>() };
        if (window != previousWindow || normalizedVisuals != previousVisuals || normalizedStory != previousStory ||
            !previousOptions.SequenceEqual(story.Options) || !(previousChoices ?? []).SequenceEqual(story.ChoiceOptions ?? []) ||
            !SameTextMap(previousScreenTexts, visuals.ScreenTexts) || !SameTextMap(previousFonts, visuals.Fonts))
        {
            NotifyContentChanged();
            previousWindow = window; previousVisuals = normalizedVisuals; previousStory = normalizedStory;
            previousOptions = story.Options.ToArray(); previousChoices = story.ChoiceOptions?.ToArray();
            previousScreenTexts = new Dictionary<string, string>(visuals.ScreenTexts);
            previousFonts = new Dictionary<string, string>(visuals.Fonts);
        }
    }
    private static bool SameTextMap(IReadOnlyDictionary<string, string> previous, IReadOnlyDictionary<string, string> current) =>
        previous.Count == current.Count && previous.All(pair => current.TryGetValue(pair.Key, out string? value) && value == pair.Value);
    private void InputChanged(object sender, System.Windows.Input.MouseEventArgs args) => NotifyContentChanged();
    private void HoverChanged(object sender, System.Windows.Input.MouseEventArgs args)
    {
        var current = System.Windows.Input.Mouse.DirectlyOver;
        if (!ReferenceEquals(current, hovered)) { hovered = current; NotifyContentChanged(); }
    }
    internal void SetScene(string? scene, string? function = null)
    {
        bool value = project.Engine.ScenePerformance.For(scene, function).HideShaders;
        if (value == disabled) return;
        disabled = value; NotifyContentChanged();
        if (disabled) { StopRendering(); Restore(); ReleaseGpu(); }
        else if (programs.Length > 0 && host.IsLoaded) StartRendering();
    }
    private void ReleaseGpu()
    {
        if (gpu is not { } old) return;
        gpu = null;
        shutdown = Task.WhenAll(shutdown, old.DisposeAsync().AsTask());
    }
    private void StartRendering()
    {
        if (timer.IsEnabled) return;
        pacer.Reset();
        lastComposition = double.NegativeInfinity;
        UpdateInterval();
        CompositionTarget.Rendering += CompositionTick;
        timer.Start();
    }
    private void StopRendering()
    {
        timer.Stop();
        CompositionTarget.Rendering -= CompositionTick;
    }
    private void UpdateInterval()
    {
        int fps = project.Engine.Performance.Graphics.MaxFps;
        var interval = TimeSpan.FromMilliseconds(fps == 0 ? 1 : 1000d / fps);
        if (timer.Interval != interval) timer.Interval = interval;
    }
    private void Loaded(object sender, RoutedEventArgs args) { NotifyContentChanged(); if (programs.Length > 0 && !disposed && !disabled) StartRendering(); }
    private void Unloaded(object sender, RoutedEventArgs args) { StopRendering(); Restore(); ReleaseGpu(); }
    private static bool Live(DependencyObject target, ref HashCode values, Panel? omit)
    {
        if (ReferenceEquals(target, omit) || target is UIElement { IsVisible: false }) return false;
        // The atlas only moves when its phase changes. Duplicate composition callbacks
        // or a paused motion clock must not schedule another identical capture.
        if (target is AnimatedBackground background) values.Add(background.Phase);
        bool live = target is EffectTextPresenter { IsAnimating: true } || target is ChoiceOptionButton { IsAnimating: true };
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
        for (int i = 0; i < children; i++) live |= Live(VisualTreeHelper.GetChild(target, i), ref values, omit);
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
    private void CompositionTick(object? sender, EventArgs args)
    {
        lastComposition = clock.Elapsed.TotalSeconds;
        if (project.Engine.Performance.Graphics.VSync) RenderFrame(lastComposition);
    }
    private void TimerTick(object? sender, EventArgs args)
    {
        UpdateInterval();
        double now = clock.Elapsed.TotalSeconds;
        // WPF can suspend composition callbacks when occluded. Keep animated
        // shader output alive in that case and when VSync has been turned off.
        if (!project.Engine.Performance.Graphics.VSync || now - lastComposition > Math.Max(.04, timer.Interval.TotalSeconds * 2))
            RenderFrame(now);
    }
    private TargetGroup[] ResolveTargetGroups()
    {
        bool changed = false;
        for (int i = 0; i < programs.Length; i++)
        {
            var target = resolve(programs[i].Program);
            if (target?.Parent is not Panel) target = null;
            if (ReferenceEquals(target, resolvedTargets[i])) continue;
            resolvedTargets[i] = target;
            changed = true;
        }
        if (!changed) return targetGroups;
        targetGroups = programs.Select((file, i) => (file.Program, Target: resolvedTargets[i]))
            .Where(item => item.Target != null).GroupBy(item => item.Target!)
            .Select(group => new TargetGroup(group.Key, group.Select(item => item.Program).ToArray(),
                group.Any(item => item.Program.Animated))).ToArray();
        foreach (var stale in surfaces.Keys.Where(target => !targetGroups.Any(group => group.Target == target)).ToArray()) Restore(stale);
        return targetGroups;
    }
    private async void RenderFrame(double now)
    {
        if (disposed || disabled || busy || programs.Length == 0 || !host.IsLoaded || !host.IsVisible || Window.GetWindow(host)?.WindowState == WindowState.Minimized) return;
        if (!pacer.ShouldRender(now, project.Engine.Performance.Graphics.MaxFps)) return;
        busy = true; int version = generation;
        try
        {
            double viewportScale = rasterScale?.Invoke() ?? 1;
            foreach (var group in ResolveTargetGroups())
            {
                var target = group.Target;
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
                double scale = Math.Min(quality * viewportScale, Math.Min(2048 / target.ActualWidth, 2048 / target.ActualHeight));
                // Account for capture, upload, retained GPU surfaces and the returned bitmap together.
                scale = Math.Min(scale, Math.Sqrt(settings.CacheBudgetBytes / (target.ActualWidth * target.ActualHeight * 32)));
                int width = Math.Max(1, (int)(target.ActualWidth * scale)), height = Math.Max(1, (int)(target.ActualHeight * scale));
                var point = target.PointToScreen(new Point());
                var screenEnd = target.PointToScreen(new Point(target.ActualWidth, target.ActualHeight));
                var screenSize = new Point(screenEnd.X - point.X, screenEnd.Y - point.Y);
                var wave = ReadWaves(surface, width, height, settings.CacheBudgetBytes);
                var animation = new HashCode();
                bool needsCapture = wave?.CaptureForeground != false;
                bool live = needsCapture && Live(target, ref animation, wave != null ? waveLayer : null);
                int animationStamp = animation.ToHashCode();
                bool captureChanged = surface.Revision != contentRevision || surface.Width != width || surface.Height != height ||
                    live || surface.AnimationStamp != animationStamp || (surface.Waves == null) != (wave == null);
                bool changed = captureChanged || surface.Waves != wave;
                if (!changed && surface.RenderedRevision == surface.Revision && surface.Origin == point && surface.ScreenSize == screenSize && !group.Animated) continue;
                if (!needsCapture)
                {
                    surface.Capture = null;
                    surface.Snapshot = null;
                }
                else if (captureChanged || surface.Snapshot == null)
                {
                    if (surface.Capture is not { } capture || surface.Width != width || surface.Height != height || capture.DpiX != 96 * scale)
                        surface.Capture = capture = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    int byteCount = checked(width * height * 4);
                    var pixels = surface.Snapshot?.Pixels is { } previous && previous.Length == byteCount
                        ? previous : GC.AllocateUninitializedArray<byte>(byteCount);
                    target.SetCurrentValue(UIElement.OpacityMaskProperty, surface.OriginalMask);
                    var omitted = wave?.CaptureForeground == true ? waveLayer : null;
                    var omittedMask = omitted?.OpacityMask;
                    try
                    {
                        omitted?.SetCurrentValue(UIElement.OpacityMaskProperty, Brushes.Transparent);
                        capture.Clear();
                        capture.Render(target);
                        capture.CopyPixels(pixels, width * 4, 0);
                        surface.Snapshot = GraphicsTextures.Snapshot(width, height, pixels);
                        Captures++;
                    }
                    finally
                    {
                        omitted?.SetCurrentValue(UIElement.OpacityMaskProperty, omittedMask);
                        target.SetCurrentValue(UIElement.OpacityMaskProperty, image.Source == null ? surface.OriginalMask : Brushes.Transparent);
                    }
                }
                surface.Width = width; surface.Height = height; surface.Revision = contentRevision;
                surface.AnimationStamp = animationStamp; surface.Waves = wave;
                gpu ??= new ShaderGpuSession(Window.GetWindow(host));
                var currentGpu = gpu;
                float seconds = (float)clock.Elapsed.TotalSeconds;
                var origin = new Vector2((float)point.X, (float)point.Y);
                var size = new Vector2((float)screenSize.X, (float)screenSize.Y);
                using var result = wave is { } input
                    ? await currentGpu.RenderPixelsAsync(WaveFrame(surface, input), true,
                        group.Effects, settings, seconds, origin, size, lifetime.Token)
                    : await currentGpu.RenderPixelsAsync(surface.Snapshot!, group.Effects, settings, seconds, origin, lifetime.Token, size);
                if (disposed || disabled || version != generation || !ReferenceEquals(gpu, currentGpu)) return;
                if (!PresentPixels(image, result)) continue;
                RenderCount++; surface.RenderedRevision = surface.Revision;
                surface.Origin = point; surface.ScreenSize = screenSize;
                if (target.OpacityMask != Brushes.Transparent)
                    target.SetCurrentValue(UIElement.OpacityMaskProperty, Brushes.Transparent);
            }
        }
        catch (OperationCanceledException) when (disposed) { }
        catch (Exception error) when (!disposed) { StopRendering(); Restore(); ReleaseGpu(); fail(error); }
        finally { busy = false; }
    }

    private static bool PresentPixels(Image output, GraphicsPixels pixels)
    {
        var bitmap = output.Source as WriteableBitmap;
        bool replace = bitmap == null || bitmap.PixelWidth != pixels.Width || bitmap.PixelHeight != pixels.Height;
        if (replace) bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null);
        // Preserve the last completed frame if WPF still holds this surface for composition.
        if (!bitmap!.TryLock(new Duration(TimeSpan.Zero))) return false;
        try { bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Pixels, pixels.Width * 4, 0); }
        finally { bitmap.Unlock(); }
        if (replace) output.Source = bitmap;
        return true;
    }
    private WaveInput? ReadWaves(Surface surface, int width, int height, long budget)
    {
        // The player's simple background has a solid backing below the scrolling
        // atlas. A custom image, mask or other visual still uses the WPF snapshot.
        if (waveLayer == null || waveLayer.Background != null || waveLayer.Clip != null ||
            waveLayer.Opacity != 1 || surface.OriginalMask != null ||
            waveLayer.Children.Count != 4 ||
            waveLayer.Children[0] is not Border { IsVisible: true, Opacity: 1, OpacityMask: null, Background: SolidColorBrush { Opacity: 1 } brush } backing ||
            waveLayer.Children[1] is not AnimatedBackground backdrop ||
            backdrop.RenderSize != waveLayer.RenderSize || backing.RenderSize != waveLayer.RenderSize ||
            waveLayer.Children[2] is not Image defaultImage || (defaultImage.IsVisible && defaultImage.Source != null) ||
            waveLayer.Children[3] is not Image image || (image.IsVisible && image.Source != null) ||
            !backdrop.TryGetWaveAtlas(out var atlas, out Rect bounds))
            return null;
        long bytes = (long)atlas!.PixelWidth * atlas.PixelHeight * 4 + 4;
        if (bytes > budget) return null;
        if (surface.Target == waveLayer) return new(atlas!, bounds, brush.Color, false);
        if (sceneLayer == null || surface.Target != sceneLayer ||
            sceneLayer.Background is not (null or SolidColorBrush { Color.A: 0 }) ||
            sceneLayer.Children.Count == 0 || sceneLayer.Children[0] is not Panel { Background: null, Clip: null } visuals ||
            visuals.Children.Count == 0 ||
            visuals.Children[0] != waveLayer || !visuals.RenderTransform.Value.IsIdentity ||
            visuals.Opacity != 1 || visuals.OpacityMask != null || visuals.Effect != null ||
            waveLayer.RenderSize != sceneLayer.RenderSize || waveLayer.TranslatePoint(new Point(), sceneLayer) != new Point() ||
            bytes + (long)width * height * 4 > budget || programs.Any(file => file.Program.Target == "background"))
            return null;
        return new(atlas!, bounds, brush.Color, true);
    }
    private static Func<GraphicsFrame> WaveFrame(Surface surface, WaveInput input)
    {
        int width = surface.Width, height = surface.Height;
        var bounds = input.Bounds;
        bounds.Scale(width / surface.Target.ActualWidth, height / surface.Target.ActualHeight);
        var foreground = surface.Snapshot;
        var full = new Rect(0, 0, width, height);
        var color = input.Color;
        float Premultiply(byte channel) => ((channel * color.A + 127) / 255) / 255f;
        var clear = new RgbaFloat(Premultiply(color.R), Premultiply(color.G), Premultiply(color.B), color.A / 255f);
        return () => new(width, height, foreground == null
            ? [new(GraphicsTextures.Get(input.Atlas), bounds)]
            : [new(GraphicsTextures.Get(input.Atlas), bounds), new(foreground, full)]) { ClearColor = clear };
    }
    private void Restore(FrameworkElement target)
    {
        if (!surfaces.Remove(target, out var surface)) return;
        target.SetCurrentValue(UIElement.OpacityMaskProperty, surface.OriginalMask);
        if (surface.Output.Parent is Panel parent) parent.Children.Remove(surface.Output);
        surface.Output.Source = null; surface.Capture = null; surface.Snapshot = null;
    }
    private void Restore() { foreach (var target in surfaces.Keys.ToArray()) Restore(target); }
    public ValueTask DisposeAsync() => new(cleanup ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        if (disposed) return; disposed = true; generation++; lifetime.Cancel(); StopRendering();
        timer.Tick -= TimerTick;
        host.Loaded -= Loaded; host.Unloaded -= Unloaded;
        host.RemoveHandler(TextBoxBase.TextChangedEvent, contentChanged);
        host.PreviewMouseDown -= InputChanged; host.MouseMove -= HoverChanged;
        Restore(); ReleaseGpu();
        await activation; await shutdown;
        lifetime.Dispose();
    }
}
