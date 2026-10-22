using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Prepares an immutable snapshot per revision. Warm motion does not read back another frame.</summary>
internal sealed class GraphicsLayer : IDisposable
{
    private static long nextId;
    private readonly long id = Interlocked.Increment(ref nextId);
    private readonly FrameworkElement owner;
    private readonly Window host;
    private object? revision;
    private GraphicsSettings? settings;
    private bool pending, failed;
    private int generation;
    internal bool IsDisposed { get; private set; }
    internal SharedGraphicsWorker Worker { get; }
    internal WriteableBitmap? Bitmap { get; private set; }
    internal event Action? Released;
    internal GraphicsLayer(FrameworkElement owner, bool primary = true)
    {
        this.owner = owner; host = Window.GetWindow(owner) ?? throw new InvalidOperationException("Графическому слою нужно окно.");
        Worker = SharedGraphicsWorker.Acquire(host); owner.SetValue(GraphicsEnvironment.LayerProperty, this);
        owner.Unloaded += Unloaded; owner.IsVisibleChanged += VisibilityChanged; host.StateChanged += StateChanged;
    }
    internal bool TryGetBitmap(Func<GraphicsFrame> createFrame, object revision, out BitmapSource? bitmap)
    {
        bitmap = null;
        if (IsDisposed || !owner.IsLoaded || !owner.IsVisible || !host.IsVisible || host.WindowState == WindowState.Minimized) return false;
        var current = GraphicsEnvironment.ResolveSettings(owner);
        if (!Equals(this.revision, revision) || !SamePreparation(settings, current))
        { this.revision = revision; settings = current with { }; Invalidate(); }
        if (!failed && !pending && Bitmap == null)
        {
            int version = generation;
            try
            {
                var frame = createFrame();
                if ((long)frame.Width * frame.Height * 4 > current.CacheBudgetBytes) throw new InvalidOperationException("Подготовленный слой превышает лимит кэша; используется компоновка WPF.");
                pending = Worker.Enqueue(frame, current with { }, (pixels, error) => Complete(version, pixels, error));
            }
            catch (Exception error) when (error is not OutOfMemoryException) { Complete(version, null, error); }
        }
        bitmap = Bitmap; return bitmap != null;
    }
    private static bool SamePreparation(GraphicsSettings? a, GraphicsSettings b) => a != null && a.Api == b.Api && a.MsaaSamples == b.MsaaSamples && a.LimitResourceCache == b.LimitResourceCache && a.ResourceCacheLimitMb == b.ResourceCacheLimitMb;
    internal void InvalidateSettings() { if (!IsDisposed && !SamePreparation(settings, GraphicsEnvironment.ResolveSettings(owner))) { settings = null; Invalidate(); } }
    private void Invalidate() { generation++; Bitmap = null; failed = false; GraphicsEnvironment.For(host).Remove(id); }
    private void Complete(int version, GraphicsPixels? pixels, Exception? error)
    {
        if (owner.Dispatcher.HasShutdownStarted) { pixels?.Dispose(); return; }
        var operation = owner.Dispatcher.BeginInvoke(new Action(() =>
        {
            using (pixels)
            {
                pending = false;
                if (IsDisposed) return;
                if (version != generation) { owner.InvalidateVisual(); return; }
                if (error != null)
                {
                    failed = true; string reason = error.GetBaseException().Message;
                    GraphicsEnvironment.For(host).Record(id, "WPF", 0, 0, 0, false, reason);
                    EngineDiagnostics.Trace("Подготовка графики", "Резерв WPF: " + reason);
                }
                else if (pixels != null)
                {
                    Bitmap = new WriteableBitmap(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null);
                    Bitmap.WritePixels(new Int32Rect(0, 0, pixels.Width, pixels.Height), pixels.Pixels, pixels.Width * 4, 0); Bitmap.Freeze();
                    var telemetry = GraphicsEnvironment.For(host); telemetry.Record(id, pixels.Backend, pixels.DrawCalls, 0, pixels.Milliseconds, false);
                    telemetry.PreparedBytes((long)pixels.Width * pixels.Height * 4);
                }
                owner.InvalidateVisual();
            }
        }));
        operation.Aborted += (_, _) => pixels?.Dispose();
        if (operation.Status == System.Windows.Threading.DispatcherOperationStatus.Aborted) pixels?.Dispose();
    }
    private void Unloaded(object sender, RoutedEventArgs args) => Dispose();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) { if (!owner.IsVisible) Dispose(); }
    private void StateChanged(object? sender, EventArgs args) { if (host.WindowState == WindowState.Minimized) Dispose(); }
    public void Dispose()
    {
        if (IsDisposed) return; IsDisposed = true; generation++; Bitmap = null; revision = null; settings = null;
        owner.Unloaded -= Unloaded; owner.IsVisibleChanged -= VisibilityChanged; host.StateChanged -= StateChanged;
        if (ReferenceEquals(owner.GetValue(GraphicsEnvironment.LayerProperty), this)) owner.ClearValue(GraphicsEnvironment.LayerProperty);
        GraphicsEnvironment.For(host).Remove(id); Worker.Release(); Released?.Invoke(); Released = null;
    }
}

/// <summary>Shares one native context between layers of a window; the last release terminates its worker.</summary>
internal sealed class SharedGraphicsWorker
{
    private sealed record Job(GraphicsSettings? Settings, Func<NativeGraphicsRenderer, GraphicsPixels?> Render, Action<GraphicsPixels?, Exception?> Complete, bool Retain);
    private static readonly ConditionalWeakTable<Window, SharedGraphicsWorker> workers = new();
    private static readonly object workerLock = new();
    private readonly BlockingCollection<Job> jobs = new(8);
    private int references;
    private volatile bool released;
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Thread WorkerThread { get; }
    private SharedGraphicsWorker() { WorkerThread = new Thread(Work) { IsBackground = true, Name = "KNE shared graphics preparation" }; WorkerThread.Start(); }
    internal static SharedGraphicsWorker Acquire(Window window)
    {
        lock (workerLock)
        {
            if (!workers.TryGetValue(window, out var worker) || worker.released) { workers.Remove(window); worker = new(); workers.Add(window, worker); }
            worker.references++; return worker;
        }
    }
    internal static SharedGraphicsWorker CreateStandalone() => new() { references = 1 };
    internal bool Enqueue(GraphicsFrame frame, GraphicsSettings settings, Action<GraphicsPixels?, Exception?> complete) => !released && jobs.TryAdd(new(settings, renderer => renderer.Render(frame, settings.MsaaSamples), complete, false));
    internal Task<GraphicsPixels> RenderShadersAsync(BitmapSource source, CompiledShaderGraph[] programs, GraphicsSettings settings, float seconds, Vector2 origin, Vector2 screenSize, CancellationToken token)
    {
        var completion = new TaskCompletionSource<GraphicsPixels>(TaskCreationOptions.RunContinuationsAsynchronously);
        GraphicsPixels Render(NativeGraphicsRenderer renderer)
        {
            token.ThrowIfCancellationRequested();
            var texture = GraphicsTextures.Get(source);
            return renderer.RenderChain(new(source.PixelWidth, source.PixelHeight, [new(texture, new Rect(0, 0, source.PixelWidth, source.PixelHeight))]), programs, seconds, origin, screenSize);
        }
        void Complete(GraphicsPixels? pixels, Exception? error)
        {
            if (error != null) completion.TrySetException(error);
            else if (token.IsCancellationRequested) { pixels?.Dispose(); completion.TrySetCanceled(token); }
            else completion.TrySetResult(pixels!);
        }
        if (released || !jobs.TryAdd(new(settings, Render, Complete, true))) throw new InvalidOperationException("Очередь рендера занята или закрыта.");
        return completion.Task;
    }
    internal void Release() { lock (workerLock) { if (--references == 0) { released = true; jobs.CompleteAdding(); } } }
    internal Task ReleaseAsync() { Release(); return released ? stopped.Task : Task.CompletedTask; }
    internal async Task ReleaseShaderAsync()
    {
        try
        {
            var trimmed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!released && jobs.TryAdd(new(null, _ => null, (_, error) => { if (error == null) trimmed.TrySetResult(); else trimmed.TrySetException(error); }, true)))
                await trimmed.Task.ConfigureAwait(false);
        }
        finally { await ReleaseAsync().ConfigureAwait(false); }
    }
    private void Work()
    {
        NativeGraphicsRenderer? renderer = null; string? api = null;
        try
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                if (released) { job.Complete(null, new OperationCanceledException("Графическое окно закрыто.")); break; }
                GraphicsPixels? result = null; Exception? failure = null;
                try
                {
                    if (job.Settings == null) { renderer?.ReleasePreparationResources(); job.Complete(null, null); continue; }
                    if (api != job.Settings.Api) { renderer?.Dispose(); renderer = null; renderer = NativeGraphicsRenderer.Create(job.Settings.Api, false); api = job.Settings.Api; }
                    renderer!.SetCacheBudget(job.Settings.CacheBudgetBytes); result = job.Render(renderer);
                    if (!job.Retain) renderer.ReleasePreparationResources();
                }
                catch (OperationCanceledException error) { result?.Dispose(); result = null; failure = error; }
                catch (Exception error) when (error is not OutOfMemoryException) { result?.Dispose(); result = null; failure = error; renderer?.Dispose(); renderer = null; api = null; }
                job.Complete(result, failure);
            }
        }
        finally
        {
            renderer?.Dispose();
            while (jobs.TryTake(out var abandoned)) abandoned.Complete(null, new OperationCanceledException("Графическое окно закрыто."));
            jobs.Dispose(); stopped.TrySetResult();
        }
    }
}
