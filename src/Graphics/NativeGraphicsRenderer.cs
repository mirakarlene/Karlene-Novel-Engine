using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.SPIRV;
using Veldrid.StartupUtilities;

namespace KNE;

internal sealed record GraphicsTexture(long Id, int Width, int Height, byte[] Pixels);
internal sealed record GraphicsSprite(GraphicsTexture Texture, Rect Bounds, float Opacity = 1);
[StructLayout(LayoutKind.Sequential)]
internal readonly record struct GraphicsVertex(Vector2 Position, Vector2 Uv, Vector4 Color);
internal sealed record GraphicsFrame(int Width, int Height, IReadOnlyList<GraphicsSprite> Sprites, IReadOnlyList<GraphicsVertex>? Geometry = null);
internal sealed class GraphicsPixels(int width, int height, byte[] pixels, string backend, int draws, long bytes, double milliseconds, int samples) : IDisposable
{
    internal int Width { get; } = width;
    internal int Height { get; } = height;
    internal byte[] Pixels { get; } = pixels;
    internal string Backend { get; } = backend;
    internal int DrawCalls { get; } = draws;
    internal long TrackedGpuBytes { get; } = bytes;
    internal double Milliseconds { get; } = milliseconds;
    internal int MsaaSamples { get; } = samples;
    private bool disposed;
    public void Dispose() { if (!disposed) { disposed = true; ArrayPool<byte>.Shared.Return(Pixels); } }
}

/// <summary>Native offscreen OpenGL/Vulkan compositor. All creation, rendering and disposal belong to its worker thread.</summary>
internal sealed class NativeGraphicsRenderer : IDisposable
{
    private readonly GraphicsDevice device;
    private readonly Sdl2Window? window;
    private readonly ResourceLayout layout;
    private readonly Shader[] shaders;
    private readonly CommandList commands;
    private readonly DeviceBuffer vertexBuffer;
    private readonly Dictionary<long, (Texture Texture, ResourceSet Binding, long Bytes)> textures = [];
    private readonly GraphicsTexture white = new(long.MinValue, 1, 1, [255, 255, 255, 255]);
    private Texture? target, resolved, staging;
    private Framebuffer? framebuffer;
    private Pipeline? pipeline;
    private int width, height, samples = -1;
    private bool disposed;
    private long textureBytes;
    private static readonly object glCreation = new();
    internal string Backend => device.BackendType == GraphicsBackend.Vulkan ? "vulkan" : "opengl";
    private const int MaximumVertices = 65536;

    internal static NativeGraphicsRenderer Create(string api, bool vsync)
    {
        if (api == "auto")
        {
            try { return Create("vulkan", vsync); }
            catch (Exception vulkan) when (vulkan is not OutOfMemoryException)
            {
                try { return Create("opengl", vsync); }
                catch (Exception gl) when (gl is not OutOfMemoryException) { throw new AggregateException("Vulkan и OpenGL недоступны.", vulkan, gl); }
            }
        }
        if (api is not ("vulkan" or "opengl")) throw new ArgumentException("Неизвестный графический API.", nameof(api));
        var options = new GraphicsDeviceOptions(false, null, vsync, ResourceBindingModel.Improved, true, true);
        GraphicsDevice? device = null; Sdl2Window? window = null;
        try
        {
            if (api == "vulkan") device = GraphicsDevice.CreateVulkan(options);
            else lock (glCreation)
                VeldridStartup.CreateWindowAndGraphicsDevice(new WindowCreateInfo(0, 0, 1, 1, Veldrid.WindowState.Hidden, "KNE · OpenGL context"), options, GraphicsBackend.OpenGL, out window, out device);
            return new(device, window);
        }
        catch { device?.Dispose(); window?.Close(); throw; }
    }

    private NativeGraphicsRenderer(GraphicsDevice device, Sdl2Window? window)
    {
        this.device = device; this.window = window;
        var factory = device.ResourceFactory;
        layout = factory.CreateResourceLayout(new ResourceLayoutDescription(
            new ResourceLayoutElementDescription("Image", ResourceKind.TextureReadOnly, ShaderStages.Fragment),
            new ResourceLayoutElementDescription("ImageSampler", ResourceKind.Sampler, ShaderStages.Fragment)));
        shaders = factory.CreateFromSpirv(new ShaderDescription(ShaderStages.Vertex, Encoding.UTF8.GetBytes(VertexShader), "main"),
            new ShaderDescription(ShaderStages.Fragment, Encoding.UTF8.GetBytes(FragmentShader), "main"));
        commands = factory.CreateCommandList();
        vertexBuffer = factory.CreateBuffer(new BufferDescription(MaximumVertices * 32u, BufferUsage.VertexBuffer | BufferUsage.Dynamic));
    }

    internal GraphicsPixels Render(GraphicsFrame frame, int msaaSamples)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        Validate(frame, msaaSamples);
        var watch = Stopwatch.StartNew();
        EnsureSurface(frame.Width, frame.Height, msaaSamples);
        var used = frame.Sprites.Select(sprite => sprite.Texture.Id).ToHashSet();
        if (frame.Geometry is { Count: > 0 }) used.Add(white.Id);
        if (textures.Count > 64 || textureBytes > 64 * 1024 * 1024)
            foreach (long key in textures.Keys.Where(key => !used.Contains(key)).ToArray())
            { var old = textures[key]; old.Binding.Dispose(); old.Texture.Dispose(); textureBytes -= old.Bytes; textures.Remove(key); }
        // Prepare resources before command recording, so rejected uploads cannot leave an open command list.
        foreach (var sprite in frame.Sprites) _ = TextureBinding(sprite.Texture);
        if (frame.Geometry is { Count: > 0 }) _ = TextureBinding(white);
        int geometryCount = frame.Geometry?.Count ?? 0, vertexCount = frame.Sprites.Count * 6 + geometryCount;
        var vertices = ArrayPool<GraphicsVertex>.Shared.Rent(Math.Max(1, vertexCount));
        byte[]? pixels = null;
        try
        {
            int index = 0;
            foreach (var sprite in frame.Sprites)
            {
                float left = (float)sprite.Bounds.Left, right = (float)sprite.Bounds.Right, top = (float)sprite.Bounds.Top, bottom = (float)sprite.Bounds.Bottom;
                var color = new Vector4(sprite.Opacity);
                vertices[index++] = Vertex(left, top, 0, 0, color); vertices[index++] = Vertex(right, top, 1, 0, color); vertices[index++] = Vertex(left, bottom, 0, 1, color);
                vertices[index++] = Vertex(right, top, 1, 0, color); vertices[index++] = Vertex(right, bottom, 1, 1, color); vertices[index++] = Vertex(left, bottom, 0, 1, color);
            }
            if (frame.Geometry != null)
                foreach (var vertex in frame.Geometry) vertices[index++] = Vertex(vertex.Position.X, vertex.Position.Y, vertex.Uv.X, vertex.Uv.Y, vertex.Color);
            if (vertexCount > 0) device.UpdateBuffer(vertexBuffer, 0, vertices.AsSpan(0, vertexCount));
            commands.Begin(); commands.SetFramebuffer(framebuffer); commands.ClearColorTarget(0, new RgbaFloat(0, 0, 0, 0));
            commands.SetFullViewports(); commands.SetPipeline(pipeline); commands.SetVertexBuffer(0, vertexBuffer);
            int draws = 0;
            foreach (var sprite in frame.Sprites)
            {
                commands.SetGraphicsResourceSet(0, TextureBinding(sprite.Texture));
                commands.Draw(6, 1, (uint)(draws * 6), 0); draws++;
            }
            if (geometryCount > 0)
            {
                commands.SetGraphicsResourceSet(0, TextureBinding(white)); commands.Draw((uint)geometryCount, 1, (uint)(frame.Sprites.Count * 6), 0); draws++;
            }
            if (msaaSamples > 0) commands.ResolveTexture(target, resolved);
            commands.CopyTexture(resolved!, staging!); commands.End(); device.SubmitCommands(commands); device.WaitForIdle();
            var mapped = device.Map(staging!, MapMode.Read);
            pixels = ArrayPool<byte>.Shared.Rent(checked(width * height * 4));
            try
            {
                for (int row = 0; row < height; row++)
                {
                    int sourceRow = device.IsUvOriginTopLeft ? row : height - row - 1;
                    Marshal.Copy(mapped.Data + checked(sourceRow * (int)mapped.RowPitch), pixels, row * width * 4, width * 4);
                }
            }
            finally { device.Unmap(staging!); }
            long allocated = textureBytes + (long)width * height * 4 * (msaaSamples > 0 ? msaaSamples + 1 : 1);
            watch.Stop();
            return new(width, height, pixels, Backend, draws, allocated, watch.Elapsed.TotalMilliseconds, msaaSamples);
        }
        catch { if (pixels != null) ArrayPool<byte>.Shared.Return(pixels); throw; }
        finally { ArrayPool<GraphicsVertex>.Shared.Return(vertices); }
    }

    private GraphicsVertex Vertex(float x, float y, float u, float v, Vector4 color)
    {
        float py = 1 - y * 2 / height;
        if (device.IsClipSpaceYInverted) py = -py;
        return new(new(x * 2 / width - 1, py), new(u, v), color);
    }
    private ResourceSet TextureBinding(GraphicsTexture source)
    {
        if (textures.TryGetValue(source.Id, out var existing)) return existing.Binding;
        if (textures.Count >= 256 || textureBytes + (long)source.Width * source.Height * 4 > 128 * 1024 * 1024)
            throw new InvalidOperationException("Кеш графического слоя превысил 128 МБ или 256 текстур.");
        var texture = device.ResourceFactory.CreateTexture(TextureDescription.Texture2D((uint)source.Width, (uint)source.Height, 1, 1, PixelFormat.B8_G8_R8_A8_UNorm, TextureUsage.Sampled));
        try
        {
            device.UpdateTexture(texture, source.Pixels, 0, 0, 0, (uint)source.Width, (uint)source.Height, 1, 0, 0);
            var binding = device.ResourceFactory.CreateResourceSet(new ResourceSetDescription(layout, texture, device.LinearSampler));
            long bytes = (long)source.Width * source.Height * 4; textureBytes += bytes; textures.Add(source.Id, (texture, binding, bytes)); return binding;
        }
        catch { texture.Dispose(); throw; }
    }
    private void EnsureSurface(int nextWidth, int nextHeight, int nextSamples)
    {
        if (width == nextWidth && height == nextHeight && samples == nextSamples && pipeline != null && framebuffer != null && staging != null) return;
        device.WaitForIdle(); DisposeSurface(); width = nextWidth; height = nextHeight; samples = nextSamples;
        var count = nextSamples switch { 2 => TextureSampleCount.Count2, 4 => TextureSampleCount.Count4, _ => TextureSampleCount.Count1 };
        if (count > device.GetSampleCountLimit(PixelFormat.B8_G8_R8_A8_UNorm, false)) throw new NotSupportedException("Запрошенный MSAA не поддерживается устройством.");
        var factory = device.ResourceFactory;
        target = factory.CreateTexture(TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.B8_G8_R8_A8_UNorm, TextureUsage.RenderTarget, count));
        resolved = samples == 0 ? target : factory.CreateTexture(TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.B8_G8_R8_A8_UNorm, TextureUsage.RenderTarget));
        staging = factory.CreateTexture(TextureDescription.Texture2D((uint)width, (uint)height, 1, 1, PixelFormat.B8_G8_R8_A8_UNorm, TextureUsage.Staging));
        framebuffer = factory.CreateFramebuffer(new FramebufferDescription(null, target));
        var blend = new BlendStateDescription(new RgbaFloat(0, 0, 0, 0), new BlendAttachmentDescription(true, BlendFactor.One, BlendFactor.InverseSourceAlpha, BlendFunction.Add, BlendFactor.One, BlendFactor.InverseSourceAlpha, BlendFunction.Add));
        pipeline = factory.CreateGraphicsPipeline(new GraphicsPipelineDescription(blend, DepthStencilStateDescription.Disabled,
            new RasterizerStateDescription(FaceCullMode.None, PolygonFillMode.Solid, FrontFace.Clockwise, false, false), PrimitiveTopology.TriangleList,
            new ShaderSetDescription([new VertexLayoutDescription(new VertexElementDescription("Position", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2),
                new VertexElementDescription("Uv", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float2), new VertexElementDescription("Color", VertexElementSemantic.TextureCoordinate, VertexElementFormat.Float4))], shaders), [layout], framebuffer.OutputDescription));
    }
    private static void Validate(GraphicsFrame frame, int samples)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Width < 1 || frame.Height < 1 || frame.Width > 8192 || frame.Height > 8192 || (long)frame.Width * frame.Height > 16 * 1024 * 1024 || samples is not (0 or 2 or 4)) throw new ArgumentOutOfRangeException(nameof(frame));
        if (frame.Sprites == null || frame.Sprites.Count > 500 || (frame.Geometry?.Count ?? 0) % 3 != 0 || frame.Sprites.Count * 6 + (frame.Geometry?.Count ?? 0) > MaximumVertices) throw new ArgumentException("Слишком большой или некорректный кадр.");
        foreach (var sprite in frame.Sprites)
            if (sprite.Texture == null || sprite.Texture.Width < 1 || sprite.Texture.Height < 1 || sprite.Texture.Width > 8192 || sprite.Texture.Height > 8192 || sprite.Texture.Pixels == null || (long)sprite.Texture.Width * sprite.Texture.Height * 4 > sprite.Texture.Pixels.Length ||
                sprite.Bounds.IsEmpty || !double.IsFinite(sprite.Bounds.X) || !double.IsFinite(sprite.Bounds.Y) || !double.IsFinite(sprite.Bounds.Width) || !double.IsFinite(sprite.Bounds.Height) || !float.IsFinite(sprite.Opacity) || sprite.Opacity < 0 || sprite.Opacity > 1)
                throw new ArgumentException("Некорректная текстура или координаты кадра.");
        if (frame.Geometry != null)
            foreach (var vertex in frame.Geometry)
                if (!float.IsFinite(vertex.Position.X) || !float.IsFinite(vertex.Position.Y) || !float.IsFinite(vertex.Uv.X) || !float.IsFinite(vertex.Uv.Y) || !float.IsFinite(vertex.Color.X) || !float.IsFinite(vertex.Color.Y) || !float.IsFinite(vertex.Color.Z) || !float.IsFinite(vertex.Color.W)) throw new ArgumentException("Некорректная геометрия кадра.");
    }
    private void DisposeSurface()
    { pipeline?.Dispose(); framebuffer?.Dispose(); staging?.Dispose(); if (resolved != target) resolved?.Dispose(); target?.Dispose(); pipeline = null; framebuffer = null; staging = resolved = target = null; }
    public void Dispose()
    {
        if (disposed) return; disposed = true;
        try { device.WaitForIdle(); DisposeSurface(); foreach (var item in textures.Values) { item.Binding.Dispose(); item.Texture.Dispose(); } shaders.ToList().ForEach(shader => shader.Dispose()); vertexBuffer.Dispose(); commands.Dispose(); layout.Dispose(); }
        finally { device.Dispose(); window?.Close(); }
    }
    private const string VertexShader = """
        #version 450
        layout(location=0) in vec2 Position;
        layout(location=1) in vec2 Uv;
        layout(location=2) in vec4 Color;
        layout(location=0) out vec2 fsUv;
        layout(location=1) out vec4 fsColor;
        void main(){ gl_Position=vec4(Position,0,1); fsUv=Uv; fsColor=Color; }
        """;
    private const string FragmentShader = """
        #version 450
        layout(set=0,binding=0) uniform texture2D Image;
        layout(set=0,binding=1) uniform sampler ImageSampler;
        layout(location=0) in vec2 fsUv;
        layout(location=1) in vec4 fsColor;
        layout(location=0) out vec4 outputColor;
        void main(){ outputColor=texture(sampler2D(Image,ImageSampler),fsUv)*fsColor; }
        """;
}
