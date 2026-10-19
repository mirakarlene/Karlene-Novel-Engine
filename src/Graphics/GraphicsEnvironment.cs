using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Collections.Concurrent;

namespace KNE;

/// <summary>Window-scoped graphics preferences inherited by KNE's graphics layers.</summary>
public static class GraphicsEnvironment
{
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.RegisterAttached("Settings", typeof(GraphicsSettings), typeof(GraphicsEnvironment), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, SettingsChanged));
    internal static readonly DependencyProperty ClockProperty = DependencyProperty.RegisterAttached("Clock", typeof(GraphicsFrameClock), typeof(GraphicsEnvironment));
    private static void SettingsChanged(DependencyObject view, DependencyPropertyChangedEventArgs args)
    { if (view.GetValue(ClockProperty) is GraphicsFrameClock clock) clock.Refresh(); if (view is FrameworkElement element) element.InvalidateVisual(); }
    public static GraphicsSettings GetSettings(DependencyObject view) => (GraphicsSettings?)view.GetValue(SettingsProperty) ?? new();
    public static void SetSettings(DependencyObject view, GraphicsSettings settings) => view.SetValue(SettingsProperty, GraphicsSettings.Normalize(settings));
    internal static readonly ConditionalWeakTable<Window, GraphicsTelemetry> Telemetry = new();
    internal static GraphicsTelemetry For(Window window) => Telemetry.GetOrCreateValue(window);
}

/// <summary>Measured native-layer statistics. Allocated bytes describe KNE-owned resources, not total GPU VRAM.</summary>
internal sealed class GraphicsTelemetry
{
    private readonly Dictionary<long, (string Backend, int Draws, long Bytes, double Ms, string? Error)> layers = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int frames;
    internal double Fps { get; private set; }
    internal string Backend => string.Join(" / ", layers.Values.Select(value => value.Backend).Distinct());
    internal string? Error => layers.Values.Select(value => value.Error).FirstOrDefault(value => value != null);
    internal int? DrawCalls => layers.Count == 0 || layers.Values.All(value => value.Backend == "WPF") ? null : layers.Values.Sum(value => value.Draws);
    internal long AllocatedBytes => layers.Values.Sum(value => value.Bytes);
    internal double RenderMilliseconds => layers.Count == 0 ? 0 : layers.Values.Max(value => value.Ms);
    internal void Record(long id, string backend, int draws, long bytes, double ms, bool primary, string? error = null)
    {
        layers[id] = (backend, draws, bytes, ms, error);
        if (primary) frames++;
        if (clock.Elapsed.TotalSeconds >= 1) { Fps = frames / clock.Elapsed.TotalSeconds; frames = 0; clock.Restart(); }
    }
    internal void Remove(long id) => layers.Remove(id);
}

/// <summary>A deterministic frame-cap gate; no timer or sleeping is needed to test its policy.</summary>
internal sealed class FramePacer
{
    private double last = double.NegativeInfinity;
    internal bool ShouldRender(double seconds, int maxFps)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || maxFps is not (0 or 30 or 60 or 144)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds < last) last = double.NegativeInfinity;
        if (maxFps > 0 && seconds - last + 1e-9 < 1d / maxFps) return false;
        last = seconds; return true;
    }
    internal void Reset() => last = double.NegativeInfinity;
}

/// <summary>A visible-only frame source. VSync follows WPF composition; unsynchronized mode uses a bounded dispatcher timer.</summary>
internal sealed class GraphicsFrameClock : IDisposable
{
    private readonly FrameworkElement owner;
    private readonly Action<double> advance;
    private readonly FramePacer pacer = new();
    private readonly Stopwatch time = Stopwatch.StartNew();
    private readonly DispatcherTimer timer;
    private Window? window;
    private double previous;
    private bool subscribed, disposed;
    internal bool Running => subscribed || timer.IsEnabled;
    internal GraphicsFrameClock(FrameworkElement owner, Action<double> advance)
    {
        this.owner = owner; this.advance = advance;
        owner.SetValue(GraphicsEnvironment.ClockProperty, this);
        timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher); timer.Tick += Tick;
        owner.Loaded += Loaded; owner.Unloaded += Unloaded; owner.IsVisibleChanged += Changed;
    }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        window = Window.GetWindow(owner); if (window != null) window.StateChanged += WindowChanged;
        SystemParameters.StaticPropertyChanged += PreferencesChanged; Refresh();
    }
    private void Unloaded(object sender, RoutedEventArgs args)
    {
        Stop(); if (window != null) window.StateChanged -= WindowChanged; window = null;
        SystemParameters.StaticPropertyChanged -= PreferencesChanged;
    }
    private void Changed(object sender, DependencyPropertyChangedEventArgs args) => Refresh();
    private void WindowChanged(object? sender, EventArgs args) => Refresh();
    private void PreferencesChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args) { if (args.PropertyName == nameof(SystemParameters.ClientAreaAnimation)) owner.Dispatcher.BeginInvoke(Refresh); }
    internal void Refresh()
    {
        Stop();
        if (disposed || !owner.IsLoaded || !owner.IsVisible || window?.WindowState == WindowState.Minimized || !SystemParameters.ClientAreaAnimation) return;
        previous = time.Elapsed.TotalSeconds; pacer.Reset();
        if (GraphicsEnvironment.GetSettings(owner).VSync) { CompositionTarget.Rendering += Tick; subscribed = true; }
        else { int fps = GraphicsEnvironment.GetSettings(owner).MaxFps; timer.Interval = TimeSpan.FromMilliseconds(fps == 0 ? 1 : 1000d / fps); timer.Start(); }
    }
    private void Tick(object? sender, EventArgs args)
    {
        var settings = GraphicsEnvironment.GetSettings(owner); double now = time.Elapsed.TotalSeconds;
        if (settings.VSync != subscribed) { Refresh(); return; }
        if (!pacer.ShouldRender(now, settings.MaxFps)) return;
        double delta = Math.Clamp(now - previous, 0, .05); previous = now; advance(delta);
    }
    private void Stop() { timer.Stop(); if (subscribed) { CompositionTarget.Rendering -= Tick; subscribed = false; } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Unloaded(owner, new RoutedEventArgs());
        owner.Loaded -= Loaded; owner.Unloaded -= Unloaded; owner.IsVisibleChanged -= Changed;
    }
}

/// <summary>One in-flight native frame per surface, generated off the dispatcher and copied to a reusable WPF bitmap.</summary>
internal sealed class GraphicsLayer : IDisposable
{
    private static long nextId;
    private readonly long id = Interlocked.Increment(ref nextId);
    private readonly FrameworkElement owner;
    private readonly bool primary;
    private readonly Window? host;
    private readonly BlockingCollection<(GraphicsFrame Frame, GraphicsSettings Settings)> jobs = new(1);
    private readonly Thread thread;
    private WriteableBitmap? bitmap;
    private GraphicsSettings? configuration;
    private int pending;
    private volatile bool dead;
    private bool repaintOnly;
    private string? failure;
    internal GraphicsLayer(FrameworkElement owner, bool primary = true)
    {
        this.owner = owner; this.primary = primary; host = Window.GetWindow(owner);
        thread = new Thread(Work) { IsBackground = true, Name = "KNE GPU layer " + id }; thread.Start();
        owner.Unloaded += Unloaded;
    }
    internal bool TryDraw(DrawingContext dc, Func<GraphicsFrame> createFrame, Rect destination)
    {
        if (dead || !owner.IsLoaded || !owner.IsVisible || Window.GetWindow(owner)?.IsVisible != true) return false;
        var settings = GraphicsEnvironment.GetSettings(owner);
        if (configuration != settings) { configuration = settings with { }; failure = null; bitmap = null; }
        if (failure != null) return false;
        if (repaintOnly) repaintOnly = false;
        else if (Interlocked.CompareExchange(ref pending, 1, 0) == 0)
        {
            try { if (!jobs.TryAdd((createFrame(), settings with { }))) Interlocked.Exchange(ref pending, 0); }
            catch { Interlocked.Exchange(ref pending, 0); throw; }
        }
        if (bitmap == null) return false;
        dc.DrawImage(bitmap, destination); return true;
    }
    private void Work()
    {
        NativeGraphicsRenderer? renderer = null; GraphicsSettings? active = null;
        try
        {
            foreach (var request in jobs.GetConsumingEnumerable())
            {
                if (dead) break;
                GraphicsPixels? image = null;
                try
                {
                    if (active != request.Settings) { renderer?.Dispose(); renderer = null; active = request.Settings; renderer = active.Api == "auto" ? SelectRenderer(request.Frame, active) : NativeGraphicsRenderer.Create(active.Api, active.VSync); }
                    image = renderer!.Render(request.Frame, request.Settings.MsaaSamples);
                    var completed = image; image = null;
                    var operation = owner.Dispatcher.BeginInvoke(() =>
                    {
                        using (completed)
                        {
                            Interlocked.Exchange(ref pending, 0);
                            if (dead || configuration != request.Settings) return;
                            if (bitmap == null || bitmap.PixelWidth != completed.Width || bitmap.PixelHeight != completed.Height)
                                bitmap = new WriteableBitmap(completed.Width, completed.Height, 96, 96, PixelFormats.Pbgra32, null);
                            bitmap.WritePixels(new Int32Rect(0, 0, completed.Width, completed.Height), completed.Pixels, completed.Width * 4, 0);
                            if (host is { } window) GraphicsEnvironment.For(window).Record(id, completed.Backend, completed.DrawCalls, completed.TrackedGpuBytes, completed.Milliseconds, primary);
                            repaintOnly = true; owner.InvalidateVisual();
                        }
                    }, DispatcherPriority.Render);
                    operation.Aborted += (_, _) => { completed.Dispose(); Interlocked.Exchange(ref pending, 0); };
                }
                catch (Exception error) when (error is not OutOfMemoryException)
                {
                    image?.Dispose(); renderer?.Dispose(); renderer = null; active = null;
                    string reason = error.GetBaseException().Message;
                    owner.Dispatcher.BeginInvoke(() =>
                    {
                        Interlocked.Exchange(ref pending, 0); if (dead) return;
                        if (configuration != request.Settings) return;
                        failure = reason; bitmap = null;
                        if (host is { } window) GraphicsEnvironment.For(window).Record(id, "WPF", 0, 0, 0, primary, reason);
                        EngineDiagnostics.Trace("Графический слой", "Резерв WPF: " + reason); owner.InvalidateVisual();
                    });
                }
            }
        }
        finally { renderer?.Dispose(); jobs.Dispose(); }
    }
    private void Unloaded(object sender, RoutedEventArgs args) => Dispose();
    private static NativeGraphicsRenderer SelectRenderer(GraphicsFrame frame, GraphicsSettings settings)
    {
        NativeGraphicsRenderer? best = null; double bestTime = double.MaxValue; var failures = new List<Exception>();
        foreach (string api in new[] { "vulkan", "opengl" })
        {
            NativeGraphicsRenderer? candidate = null;
            try
            {
                candidate = NativeGraphicsRenderer.Create(api, settings.VSync);
                using (candidate.Render(frame, settings.MsaaSamples)) { } // Warm pipeline and uploads before comparison.
                var times = new double[3];
                for (int i = 0; i < times.Length; i++) { var watch = Stopwatch.StartNew(); using (candidate.Render(frame, settings.MsaaSamples)) { } times[i] = watch.Elapsed.TotalMilliseconds; }
                Array.Sort(times); double measured = times[1];
                EngineDiagnostics.Trace("Автовыбор графики", $"{api}: медиана {measured:F2} мс, {frame.Width}×{frame.Height}, MSAA {settings.MsaaSamples}");
                if (measured < bestTime) { best?.Dispose(); best = candidate; candidate = null; bestTime = measured; }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { failures.Add(error); }
            finally { candidate?.Dispose(); }
        }
        if (best == null) throw new AggregateException("Vulkan и OpenGL недоступны для этого слоя.", failures);
        EngineDiagnostics.Trace("Выбран графический API", best.Backend); return best;
    }
    public void Dispose()
    {
        if (dead) return; dead = true; owner.Unloaded -= Unloaded;
        if (host is { } window) GraphicsEnvironment.For(window).Remove(id);
        jobs.CompleteAdding();
    }
}
