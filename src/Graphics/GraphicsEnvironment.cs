using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace KNE;

/// <summary>Window-scoped graphics preferences inherited by KNE's graphics layers.</summary>
public static class GraphicsEnvironment
{
    public static readonly DependencyProperty SettingsProperty = DependencyProperty.RegisterAttached("Settings", typeof(GraphicsSettings), typeof(GraphicsEnvironment), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.Inherits, SettingsChanged));
    internal static readonly DependencyProperty ClockProperty = DependencyProperty.RegisterAttached("Clock", typeof(GraphicsFrameClock), typeof(GraphicsEnvironment));
    internal static readonly DependencyProperty LayerProperty = DependencyProperty.RegisterAttached("Layer", typeof(GraphicsLayer), typeof(GraphicsEnvironment));
    private static void SettingsChanged(DependencyObject view, DependencyPropertyChangedEventArgs args)
    { if (view.GetValue(ClockProperty) is GraphicsFrameClock clock) clock.Refresh(); if (view.GetValue(LayerProperty) is GraphicsLayer layer) layer.InvalidateSettings(); if (view is FrameworkElement element) element.InvalidateVisual(); }
    public static GraphicsSettings GetSettings(DependencyObject view) => (GraphicsSettings?)view.GetValue(SettingsProperty) ?? new();
    public static void SetSettings(DependencyObject view, GraphicsSettings settings) => view.SetValue(SettingsProperty, GraphicsSettings.Normalize(settings));
    internal static readonly ConditionalWeakTable<Window, GraphicsTelemetry> Telemetry = new();
    internal static GraphicsTelemetry For(Window window) => Telemetry.GetOrCreateValue(window);
    internal static GraphicsSettings ResolveSettings(FrameworkElement view) => Window.GetWindow(view) is { } window && window.ReadLocalValue(SettingsProperty) != DependencyProperty.UnsetValue ? GetSettings(window) : GetSettings(view);
}

/// <summary>Measured native-layer statistics. Allocated bytes describe KNE-owned resources, not total GPU VRAM.</summary>
internal sealed class GraphicsTelemetry
{
    private readonly Dictionary<long, (string Backend, int Draws, long Bytes, double Ms, string? Error)> layers = [];
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private int frames;
    internal double Fps { get; private set; }
    internal long PreparationCount { get; private set; }
    internal long ReadbackBytes { get; private set; }
    internal string Backend => string.Join(" / ", layers.Values.Select(value => value.Backend).Distinct());
    internal string? Error => layers.Values.Select(value => value.Error).FirstOrDefault(value => value != null);
    internal int? DrawCalls => layers.Count == 0 || layers.Values.All(value => value.Backend == "WPF") ? null : layers.Values.Sum(value => value.Draws);
    internal long AllocatedBytes => layers.Values.Sum(value => value.Bytes);
    internal double RenderMilliseconds => layers.Count == 0 ? 0 : layers.Values.Max(value => value.Ms);
    internal void Record(long id, string backend, int draws, long bytes, double ms, bool primary, string? error = null)
    {
        layers[id] = (backend, draws, bytes, ms, error);
        if (backend != "WPF") PreparationCount++;
    }
    internal void Remove(long id) => layers.Remove(id);
    internal void PreparedBytes(long bytes) => ReadbackBytes += bytes;
    internal void Presented()
    { frames++; if (clock.Elapsed.TotalSeconds >= 1) { Fps = frames / clock.Elapsed.TotalSeconds; frames = 0; clock.Restart(); } }
}

/// <summary>A deterministic frame-cap gate; no timer or sleeping is needed to test its policy.</summary>
internal sealed class FramePacer
{
    private double last = double.NegativeInfinity, accepted = double.NegativeInfinity, next;
    private int rate = -1;
    internal bool ShouldRender(double seconds, int maxFps)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || maxFps is not (0 or 30 or 60 or 144)) throw new ArgumentOutOfRangeException(nameof(seconds));
        if (seconds < last || rate != maxFps) { rate = maxFps; accepted = double.NegativeInfinity; next = seconds; }
        last = seconds;
        if (maxFps == 0) return true;
        if (seconds == accepted || seconds + 1e-9 < next) return false;
        double period = 1d / maxFps;
        // Keep fractional deadline remainder across ordinary delivery jitter. A long stall drops debt.
        next = seconds - next >= period * 2 ? seconds + period : next + period;
        accepted = seconds; return true;
    }
    internal void Reset() { last = accepted = double.NegativeInfinity; rate = -1; next = 0; }
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
    private readonly bool primary;
    internal bool Running => subscribed || timer.IsEnabled;
    internal GraphicsFrameClock(FrameworkElement owner, Action<double> advance, bool primary = false)
    {
        this.owner = owner; this.advance = advance; this.primary = primary;
        owner.SetValue(GraphicsEnvironment.ClockProperty, this);
        timer = new DispatcherTimer(DispatcherPriority.Background, owner.Dispatcher); timer.Tick += Tick;
        owner.Loaded += Loaded; owner.Unloaded += Unloaded; owner.IsVisibleChanged += Changed;
    }
    private void Loaded(object sender, RoutedEventArgs args)
    {
        if (window != null) return;
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
        if (GraphicsEnvironment.ResolveSettings(owner).VSync) { CompositionTarget.Rendering += Tick; subscribed = true; }
        else { int fps = GraphicsEnvironment.ResolveSettings(owner).MaxFps; timer.Interval = TimeSpan.FromMilliseconds(fps == 0 ? 1 : 1000d / fps); timer.Start(); }
    }
    private void Tick(object? sender, EventArgs args)
    {
        var settings = GraphicsEnvironment.ResolveSettings(owner); double now = time.Elapsed.TotalSeconds;
        if (settings.VSync != subscribed) { Refresh(); return; }
        if (!pacer.ShouldRender(now, settings.MaxFps)) return;
        double delta = Math.Clamp(now - previous, 0, .05); previous = now; advance(delta);
        if (primary && window != null && window is not GameWindow) GraphicsEnvironment.For(window).Presented();
    }
    private void Stop() { timer.Stop(); if (subscribed) { CompositionTarget.Rendering -= Tick; subscribed = false; } }
    public void Dispose()
    {
        if (disposed) return; disposed = true; Unloaded(owner, new RoutedEventArgs());
        owner.Loaded -= Loaded; owner.Unloaded -= Unloaded; owner.IsVisibleChanged -= Changed;
    }
}
