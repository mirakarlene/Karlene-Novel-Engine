using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Leases the window's existing GPU context instead of allocating another device for shader effects.</summary>
internal sealed class ShaderGpuSession : IAsyncDisposable
{
    private bool disposed;
    private Task? pending;
    private Task? cleanup;
    internal SharedGraphicsWorker Worker { get; }
    internal ShaderGpuSession(Window? host = null) => Worker = host == null ? SharedGraphicsWorker.CreateStandalone() : SharedGraphicsWorker.Acquire(host);

    internal Task<BitmapSource> RenderAsync(BitmapSource source, CompiledShaderGraph[] programs, GraphicsSettings settings, float seconds, Vector2 origin = default, CancellationToken token = default, Vector2 screenSize = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!source.IsFrozen) throw new ArgumentException("GPU snapshot must be frozen.");
        token.ThrowIfCancellationRequested();
        if (programs.Length > 16) throw new ArgumentException("Одновременно допускается не более 16 шейдеров.");
        var active = programs.Where(program => !program.IsIdentity).ToArray();
        if (active.Length == 0) return Task.FromResult(source);
        CheckQueue();
        var result = RenderBitmapAsync(() => AsFrame(GraphicsTextures.Get(source)), active, settings with { }, seconds, origin, screenSize, token);
        pending = result;
        return result;
    }
    internal Task<GraphicsPixels> RenderPixelsAsync(GraphicsTexture source, CompiledShaderGraph[] programs, GraphicsSettings settings,
        float seconds, Vector2 origin, CancellationToken token, Vector2 screenSize)
        => RenderPixelsAsync(() => AsFrame(source), false, programs, settings, seconds, origin, screenSize, token);

    internal Task<GraphicsPixels> RenderPixelsAsync(Func<GraphicsFrame> source, bool composite, CompiledShaderGraph[] programs, GraphicsSettings settings,
        float seconds, Vector2 origin, Vector2 screenSize, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        CheckQueue();
        var result = Worker.RenderShadersAsync(source, composite, programs, settings with { }, seconds, origin, screenSize, token);
        pending = result;
        return result;
    }
    private static GraphicsFrame AsFrame(GraphicsTexture source) => new(source.Width, source.Height,
        [new(source, new Rect(0, 0, source.Width, source.Height))]);

    private void CheckQueue()
    {
        if (pending is { IsCompleted: false }) throw new InvalidOperationException("Shader queue is busy.");
    }
    private async Task<BitmapSource> RenderBitmapAsync(Func<GraphicsFrame> source, CompiledShaderGraph[] programs,
        GraphicsSettings settings, float seconds, Vector2 origin, Vector2 screenSize, CancellationToken token)
    {
        using var pixels = await Worker.RenderShadersAsync(source, false, programs, settings, seconds, origin, screenSize, token).ConfigureAwait(false);
        var image = BitmapSource.Create(pixels.Width, pixels.Height, 96, 96, PixelFormats.Pbgra32, null, pixels.Pixels, pixels.Width * 4); image.Freeze();
        return image;
    }
    public ValueTask DisposeAsync() => new(cleanup ??= DisposeCoreAsync());
    private async Task DisposeCoreAsync()
    {
        disposed = true;
        try { if (pending != null) await pending; }
        catch (Exception error) when (error is not OutOfMemoryException) { /* The render caller owns its failure. */ }
        finally { await Worker.ReleaseShaderAsync(); }
    }
}
