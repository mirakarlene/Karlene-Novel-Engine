using System.Buffers;
using System.Diagnostics;
using System.Numerics;
using ImGuiNET;
using Silk.NET.OpenGL;
using Texture = Silk.NET.OpenGL.TextureTarget;

namespace KNE;

/// <summary>Turns game layers into ImGui draw lists, then renders them offscreen through Silk.NET.</summary>
internal sealed unsafe class ImGuiLayerRenderer : IGameLayerRenderer
{
    private static readonly object imguiLock = new();
    private readonly SilkLayerContext context = new();
    private GL Gl => context.Gl;
    private nint imgui;
    private uint program, vao, vertices, indices, white;
    private uint target, framebuffer, multisample, color;
    private (uint Width, uint Height, int Samples) surfaceSize;
    private int sizeUniform;

    internal ImGuiLayerRenderer()
    {
        try
        {
            program = CreateProgram();
            sizeUniform = Gl.GetUniformLocation(program, "size");
            vao = Gl.GenVertexArray(); Gl.BindVertexArray(vao);
            vertices = Gl.GenBuffer(); Gl.BindBuffer(BufferTargetARB.ArrayBuffer, vertices);
            indices = Gl.GenBuffer(); Gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, indices);
            const uint stride = 20; // ImDrawVert: float2 xy, float2 uv, RGBA8
            Gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, stride, null);
            Gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, (void*)8);
            Gl.VertexAttribPointer(2, 4, VertexAttribPointerType.UnsignedByte, true, stride, (void*)16);
            for (uint i = 0; i < 3; i++) Gl.EnableVertexAttribArray(i);
            white = Upload(new(0, 1, 1, [255, 255, 255, 255]));
            lock (imguiLock)
            {
                nint previous = ImGui.GetCurrentContext();
                imgui = ImGui.CreateContext();
                try
                {
                    ImGui.SetCurrentContext(imgui);
                    var io = ImGui.GetIO();
                    io.NativePtr->IniFilename = null;
                    io.BackendFlags |= ImGuiBackendFlags.RendererHasVtxOffset;
                    io.Fonts.GetTexDataAsRGBA32(out nint _, out _, out _, out _);
                    io.Fonts.SetTexID((nint)white);
                }
                finally { ImGui.SetCurrentContext(previous); }
            }
        }
        catch { Dispose(); throw; }
    }

    public GraphicsPixels Render(GraphicsFrame frame, int samples, long budget)
    {
        NativeGraphicsRenderer.Validate(frame, samples);
        var watch = Stopwatch.StartNew();
        context.Activate();
        uint width = (uint)frame.Width, height = (uint)frame.Height;
        long allocated = (long)width * height * 4 * (1 + samples);
        if (allocated > budget) throw new InvalidOperationException("Поверхность ImGui превышает лимит кэша.");
        var textures = new Dictionary<long, uint>();
        try
        {
            EnsureSurface(width, height, samples);
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, multisample == 0 ? framebuffer : multisample);

            foreach (var sprite in frame.Sprites)
            {
                var texture = sprite.Texture;
                if (textures.ContainsKey(texture.Id)) continue;
                allocated += (long)texture.Width * texture.Height * 4;
                if (allocated > budget) throw new InvalidOperationException("Текстуры ImGui превышают лимит кэша.");
                textures.Add(texture.Id, Upload(texture));
            }

            Gl.Viewport(0, 0, width, height);
            Gl.ClearColor(0, 0, 0, 0);
            Gl.Clear(ClearBufferMask.ColorBufferBit);
            Gl.Enable(EnableCap.Blend);
            Gl.BlendFunc(BlendingFactor.One, BlendingFactor.OneMinusSrcAlpha);
            Gl.UseProgram(program);
            Gl.Uniform2(sizeUniform, (float)width, (float)height);
            int draws;
            lock (imguiLock)
            {
                nint previous = ImGui.GetCurrentContext();
                ImGui.SetCurrentContext(imgui);
                try { draws = RenderDrawLists(frame, textures); }
                finally { ImGui.SetCurrentContext(previous); }
            }

            if (multisample != 0)
            {
                Gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, multisample);
                Gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, framebuffer);
                Gl.BlitFramebuffer(0, 0, frame.Width, frame.Height, 0, 0, frame.Width, frame.Height,
                    ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Nearest);
            }
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
            var pixels = ArrayPool<byte>.Shared.Rent(checked(frame.Width * frame.Height * 4));
            try
            {
                fixed (byte* output = pixels) Gl.ReadPixels(0, 0, width, height, PixelFormat.Bgra, PixelType.UnsignedByte, output);
                var error = Gl.GetError();
                if (error != GLEnum.NoError) throw new InvalidOperationException("Ошибка OpenGL при подготовке слоя ImGui: " + error);
                return new(frame.Width, frame.Height, pixels, "Silk.NET + ImGui.NET · OpenGL", draws, allocated, watch.Elapsed.TotalMilliseconds,
                    samples, static buffer => ArrayPool<byte>.Shared.Return(buffer));
            }
            catch { ArrayPool<byte>.Shared.Return(pixels); throw; }
        }
        finally
        {
            foreach (uint id in textures.Values) Gl.DeleteTexture(id);
        }
    }

    private void EnsureSurface(uint width, uint height, int samples)
    {
        if (surfaceSize == (width, height, samples)) return;
        Gl.DeleteTexture(target);
        Gl.DeleteFramebuffer(framebuffer);
        Gl.DeleteFramebuffer(multisample);
        Gl.DeleteRenderbuffer(color);
        multisample = color = 0;
        surfaceSize = default;
        target = Gl.GenTexture();
        framebuffer = Gl.GenFramebuffer();
        Gl.BindTexture(Texture.Texture2D, target);
        Gl.TexImage2D(Texture.Texture2D, 0, InternalFormat.Rgba8, width, height, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
        Gl.BindFramebuffer(FramebufferTarget.Framebuffer, framebuffer);
        Gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, Texture.Texture2D, target, 0);
        CheckFramebuffer();
        if (samples != 0)
        {
            multisample = Gl.GenFramebuffer();
            color = Gl.GenRenderbuffer();
            Gl.BindFramebuffer(FramebufferTarget.Framebuffer, multisample);
            Gl.BindRenderbuffer(RenderbufferTarget.Renderbuffer, color);
            Gl.RenderbufferStorageMultisample(RenderbufferTarget.Renderbuffer, (uint)samples, InternalFormat.Rgba8, width, height);
            Gl.FramebufferRenderbuffer(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, RenderbufferTarget.Renderbuffer, color);
            CheckFramebuffer();
        }
        surfaceSize = (width, height, samples);
    }

    private int RenderDrawLists(GraphicsFrame frame, Dictionary<long, uint> textures)
    {
        ImGui.GetIO().DisplaySize = new Vector2(frame.Width, frame.Height);
        ImGui.NewFrame();
        var list = ImGui.GetBackgroundDrawList();
        foreach (var sprite in frame.Sprites)
        {
            var bounds = sprite.Bounds;
            list.AddImage((nint)textures[sprite.Texture.Id], new((float)bounds.Left, (float)bounds.Top),
                new((float)bounds.Right, (float)bounds.Bottom), Vector2.Zero, Vector2.One,
                ImGui.ColorConvertFloat4ToU32(new Vector4(1, 1, 1, sprite.Opacity)));
        }

        if (frame.Geometry is { Count: > 0 } geometry)
        {
            list.PushTextureID((nint)white);
            for (int i = 0; i < geometry.Count; i += 3)
            {
                list.PrimReserve(3, 3);
                var first = (ushort)list.NativePtr->_VtxCurrentIdx;
                for (int j = 0; j < 3; j++)
                {
                    var vertex = geometry[i + j];
                    list.PrimWriteIdx((ushort)(first + j));
                    list.PrimWriteVtx(vertex.Position, Vector2.Zero, ImGui.ColorConvertFloat4ToU32(LayerVertexColor.Straight(vertex.Color)));
                }
            }
            list.PopTextureID();
        }
        ImGui.Render();

        var data = ImGui.GetDrawData();
        int draws = 0;
        Gl.BindVertexArray(vao);
        Gl.ActiveTexture(TextureUnit.Texture0);
        for (int i = 0; i < data.CmdListsCount; i++)
        {
            var commands = data.CmdLists[i];
            Gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(commands.VtxBuffer.Size * sizeof(ImDrawVert)),
                (void*)commands.VtxBuffer.Data, BufferUsageARB.StreamDraw);
            Gl.BufferData(BufferTargetARB.ElementArrayBuffer, (nuint)(commands.IdxBuffer.Size * sizeof(ushort)),
                (void*)commands.IdxBuffer.Data, BufferUsageARB.StreamDraw);
            for (int j = 0; j < commands.CmdBuffer.Size; j++)
            {
                var command = commands.CmdBuffer[j];
                Gl.BindTexture(Texture.Texture2D, (uint)command.TextureId);
                Gl.DrawElementsBaseVertex(PrimitiveType.Triangles, command.ElemCount, DrawElementsType.UnsignedShort,
                    (void*)(command.IdxOffset * sizeof(ushort)), (int)command.VtxOffset);
                draws++;
            }
        }
        return draws;
    }

    private uint Upload(GraphicsTexture texture)
    {
        uint id = Gl.GenTexture();
        Gl.BindTexture(Texture.Texture2D, id);
        Gl.TexParameter(Texture.Texture2D, TextureParameterName.TextureMinFilter, (int)GLEnum.Linear);
        Gl.TexParameter(Texture.Texture2D, TextureParameterName.TextureMagFilter, (int)GLEnum.Linear);
        Gl.TexParameter(Texture.Texture2D, TextureParameterName.TextureWrapS, (int)GLEnum.ClampToEdge);
        Gl.TexParameter(Texture.Texture2D, TextureParameterName.TextureWrapT, (int)GLEnum.ClampToEdge);
        fixed (byte* pixels = texture.Pixels)
            Gl.TexImage2D(Texture.Texture2D, 0, InternalFormat.Rgba8, (uint)texture.Width, (uint)texture.Height, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, pixels);
        return id;
    }

    private void CheckFramebuffer()
    {
        if (Gl.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != GLEnum.FramebufferComplete)
            throw new InvalidOperationException("OpenGL не поддерживает запрошенную поверхность или MSAA.");
    }

    private uint CreateProgram()
    {
        const string vertexSource = """
            #version 330 core
            layout (location=0) in vec2 position;
            layout (location=1) in vec2 uv;
            layout (location=2) in vec4 color;
            uniform vec2 size;
            out vec2 texCoord;
            out vec4 tint;
            void main() { gl_Position = vec4(position * 2.0 / size - 1.0, 0, 1); texCoord = uv; tint = color; }
            """;
        const string fragmentSource = """
            #version 330 core
            in vec2 texCoord;
            in vec4 tint;
            uniform sampler2D image;
            out vec4 pixel;
            void main() { pixel = texture(image, texCoord) * vec4(tint.rgb * tint.a, tint.a); }
            """;
        uint result = Gl.CreateProgram(), vertex = 0, fragment = 0;
        try
        {
            vertex = Compile(ShaderType.VertexShader, vertexSource);
            fragment = Compile(ShaderType.FragmentShader, fragmentSource);
            Gl.AttachShader(result, vertex); Gl.AttachShader(result, fragment);
            Gl.LinkProgram(result);
            Gl.GetProgram(result, ProgramPropertyARB.LinkStatus, out int ok);
            if (ok == 0) throw new InvalidOperationException(Gl.GetProgramInfoLog(result));
            return result;
        }
        catch { Gl.DeleteProgram(result); throw; }
        finally
        {
            if (vertex != 0) Gl.DeleteShader(vertex);
            if (fragment != 0) Gl.DeleteShader(fragment);
        }
    }

    private uint Compile(ShaderType stage, string source)
    {
        uint shader = Gl.CreateShader(stage);
        Gl.ShaderSource(shader, source); Gl.CompileShader(shader);
        Gl.GetShader(shader, ShaderParameterName.CompileStatus, out int ok);
        if (ok != 0) return shader;
        string error = Gl.GetShaderInfoLog(shader);
        Gl.DeleteShader(shader);
        throw new InvalidOperationException(error);
    }

    public void Dispose()
    {
        try
        {
            if (imgui != 0)
            {
                lock (imguiLock) ImGui.DestroyContext(imgui);
                imgui = 0;
            }
            // Context destruction frees its remaining GL objects, including partially initialized state.
        }
        finally { context.Dispose(); }
    }
}
