using System.Numerics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Leases the window's existing GPU context instead of allocating another device for shader effects.</summary>
internal sealed class ShaderGpuSession : IAsyncDisposable
{
    private bool disposed;
    private Task<BitmapSource>? pending;
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
        if (pending is { IsCompleted: false }) throw new InvalidOperationException("Shader queue is busy.");
        pending = RenderCoreAsync(source, active, settings with { }, seconds, origin, screenSize, token);
        return pending;
    }
    private async Task<BitmapSource> RenderCoreAsync(BitmapSource source, CompiledShaderGraph[] programs, GraphicsSettings settings, float seconds, Vector2 origin, Vector2 screenSize, CancellationToken token)
    {
        using var pixels = await Worker.RenderShadersAsync(source, programs, settings, seconds, origin, screenSize, token).ConfigureAwait(false);
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
