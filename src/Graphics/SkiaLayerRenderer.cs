using System.Diagnostics;
using System.Numerics;
using SkiaSharp;

namespace KNE;

internal interface IGameLayerRenderer : IDisposable
{
    GraphicsPixels Render(GraphicsFrame frame, int samples, long budget);
}

internal sealed class SkiaLayerRenderer : IGameLayerRenderer
{
    private readonly SilkLayerContext? context;
    private readonly GRContext? gpu;

    internal SkiaLayerRenderer(bool openGl)
    {
        if (!openGl) return;
        context = new SilkLayerContext();
        try
        {
            using var api = GRGlInterface.Create(context.GetProcAddress)
                ?? throw new InvalidOperationException("SkiaSharp не смог загрузить интерфейс OpenGL.");
            gpu = GRContext.CreateGl(api) ?? throw new InvalidOperationException("SkiaSharp не смог создать контекст OpenGL.");
        }
        catch { context.Dispose(); throw; }
    }

    public unsafe GraphicsPixels Render(GraphicsFrame frame, int samples, long budget)
    {
        NativeGraphicsRenderer.Validate(frame, samples);
        if (gpu == null && samples != 0) throw new NotSupportedException("Режим CPU SkiaSharp не поддерживает MSAA.");
        var watch = Stopwatch.StartNew();
        context?.Activate();
        var info = new SKImageInfo(frame.Width, frame.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        long allocated = (long)info.BytesSize * (1 + samples);
        if (allocated > budget) throw new InvalidOperationException("Слой SkiaSharp превышает лимит кэша.");
        if (gpu != null && samples > gpu.GetMaxSurfaceSampleCount(info.ColorType))
            throw new NotSupportedException("OpenGL SkiaSharp не поддерживает выбранный уровень MSAA.");
        if (gpu != null && budget != long.MaxValue) gpu.SetResourceCacheLimit(budget);
        using var surface = (gpu == null ? SKSurface.Create(info) : SKSurface.Create(gpu, false, info, samples, GRSurfaceOrigin.TopLeft))
            ?? throw new InvalidOperationException("Не удалось выделить поверхность SkiaSharp.");
        var canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);
        using var paint = new SKPaint();
        var images = new Dictionary<long, SKImage>();
        try
        {
            foreach (var sprite in frame.Sprites)
            {
                var texture = sprite.Texture;
                if (!images.TryGetValue(texture.Id, out var image))
                {
                    var source = new SKImageInfo(texture.Width, texture.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
                    allocated += source.BytesSize;
                    if (allocated > budget) throw new InvalidOperationException("Слой SkiaSharp превышает лимит кэша.");
                    image = SKImage.FromPixelCopy(source, texture.Pixels, source.RowBytes);
                    images.Add(texture.Id, image);
                }
                paint.Color = SKColors.White.WithAlpha(Opacity(sprite.Opacity));
                var bounds = sprite.Bounds;
                canvas.DrawImage(image, new SKRect((float)bounds.Left, (float)bounds.Top, (float)bounds.Right, (float)bounds.Bottom),
                    new SKSamplingOptions(SKFilterMode.Linear), paint);
            }

            int draws = frame.Sprites.Count;
            if (frame.Geometry is { Count: > 0 } geometry)
            {
                var positions = new SKPoint[geometry.Count];
                var colors = new SKColor[geometry.Count];
                for (int i = 0; i < geometry.Count; i++)
                {
                    var vertex = geometry[i];
                    positions[i] = new SKPoint(vertex.Position.X, vertex.Position.Y);
                    var color = LayerVertexColor.Straight(vertex.Color);
                    colors[i] = new SKColor(Opacity(color.X), Opacity(color.Y), Opacity(color.Z), Opacity(color.W));
                }
                using var triangles = SKVertices.CreateCopy(SKVertexMode.Triangles, positions, null, colors);
                paint.Color = SKColors.White;
                // The vertex colors are the destination in Skia's paint/vertex blend.
                canvas.DrawVertices(triangles, SKBlendMode.Dst, paint);
                draws++;
            }

            var pixels = GC.AllocateUninitializedArray<byte>(info.BytesSize);
            fixed (byte* destination = pixels)
                if (!surface.ReadPixels(info, (nint)destination, info.RowBytes, 0, 0))
                    throw new InvalidOperationException("Не удалось прочитать пиксели SkiaSharp.");
            return new(frame.Width, frame.Height, pixels, gpu == null ? "SkiaSharp · CPU" : "Silk.NET + SkiaSharp · OpenGL",
                draws, gpu == null ? 0 : allocated, watch.Elapsed.TotalMilliseconds, samples);
        }
        finally { foreach (var image in images.Values) image.Dispose(); }
    }

    private static byte Opacity(float value) => (byte)MathF.Round(Math.Clamp(value, 0, 1) * 255);

    public void Dispose()
    {
        try
        {
            if (gpu != null && !context!.TryActivate()) gpu.AbandonContext(false);
            gpu?.Dispose();
        }
        finally { context?.Dispose(); }
    }
}

internal static class LayerVertexColor
{
    internal static Vector4 Straight(Vector4 premultiplied) => premultiplied.W <= 0
        ? Vector4.Zero
        : new Vector4(premultiplied.X / premultiplied.W, premultiplied.Y / premultiplied.W, premultiplied.Z / premultiplied.W, premultiplied.W);
}
