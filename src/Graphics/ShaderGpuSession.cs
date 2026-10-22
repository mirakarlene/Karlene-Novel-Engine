using System.Collections.Concurrent;
using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>A bounded render queue. Native resources never leave their dedicated worker thread.</summary>
internal sealed class ShaderGpuSession : IAsyncDisposable
{
    private sealed record Job(BitmapSource Source, CompiledShaderGraph[] Programs, GraphicsSettings Settings, float Seconds, Vector2 Origin, Vector2 ScreenSize, TaskCompletionSource<BitmapSource> Completion, CancellationToken Token);
    private readonly BlockingCollection<Job> jobs = new(1);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private int disposed;
    internal ShaderGpuSession()
    {
        thread = new Thread(Work) { IsBackground = true, Name = "KNE shader GPU" }; thread.Start();
    }
    internal Task<BitmapSource> RenderAsync(BitmapSource source, CompiledShaderGraph[] programs, GraphicsSettings settings, float seconds, Vector2 origin = default, CancellationToken token = default, Vector2 screenSize = default)
    {
        ObjectDisposedException.ThrowIf(disposed != 0, this);
        if (!source.IsFrozen) throw new ArgumentException("GPU snapshot must be frozen.");
        var done = new TaskCompletionSource<BitmapSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!jobs.TryAdd(new(source, programs, settings with { }, seconds, origin, screenSize, done, token))) throw new InvalidOperationException("Shader queue is busy.");
        return done.Task;
    }
    private void Work()
    {
        NativeGraphicsRenderer? renderer = null; string? api = null;
        try
        {
            foreach (var job in jobs.GetConsumingEnumerable())
            {
                try
                {
                    job.Token.ThrowIfCancellationRequested();
                    if (disposed != 0) { job.Completion.TrySetCanceled(); continue; }
                    if (api != job.Settings.Api) { renderer?.Dispose(); renderer = NativeGraphicsRenderer.Create(job.Settings.Api, false); api = job.Settings.Api; }
                    renderer!.SetCacheBudget(job.Settings.CacheBudgetBytes);
                    BitmapSource image = job.Source;
                    foreach (var shader in job.Programs)
                    {
                        job.Token.ThrowIfCancellationRequested();
                        var texture = GraphicsTextures.Get(image);
                        using var pixels = renderer.Render(new(image.PixelWidth, image.PixelHeight, [new(texture, new Rect(0, 0, image.PixelWidth, image.PixelHeight))]), job.Settings.MsaaSamples, shader, job.Seconds, job.Origin, job.ScreenSize);
                        image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null, pixels.Pixels, pixels.Width * 4); image.Freeze();
                    }
                    job.Token.ThrowIfCancellationRequested(); job.Completion.TrySetResult(image);
                }
                catch (OperationCanceledException) { job.Completion.TrySetCanceled(job.Token); }
                catch (Exception error) when (error is not OutOfMemoryException) { job.Completion.TrySetException(error); renderer?.Dispose(); renderer = null; api = null; }
            }
        }
        finally { renderer?.Dispose(); while (jobs.TryTake(out var abandoned)) abandoned.Completion.TrySetCanceled(); jobs.Dispose(); stopped.TrySetResult(); }
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) jobs.CompleteAdding();
        await stopped.Task;
    }
}
