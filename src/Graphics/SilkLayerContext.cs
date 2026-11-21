using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Silk.NET.OpenGL;

namespace KNE;

/// <summary>OpenGL context for offscreen game layers. Creation and use stay on the graphics worker.</summary>
internal sealed partial class SilkLayerContext : IDisposable
{
    private const uint Video = 0x20;
    private nint window;
    private nint context;
    private bool initialized;
    internal static object CreationLock { get; } = new();
    internal GL Gl { get; private set; } = null!;

    internal SilkLayerContext()
    {
        try
        {
            lock (CreationLock)
            {
                Check(Init(Video) == 0);
                initialized = true;
                // SDL's OpenGL attributes are shared across contexts.
                ResetAttributes();
                try
                {
                    Check(SetAttribute(17, 3) == 0); // SDL_GL_CONTEXT_MAJOR_VERSION
                    Check(SetAttribute(18, 3) == 0); // SDL_GL_CONTEXT_MINOR_VERSION
                    Check(SetAttribute(21, 1) == 0); // SDL_GL_CONTEXT_PROFILE_CORE
                    window = CreateWindow("KNE OpenGL layers", 0, 0, 1, 1, 0xA); // OpenGL | Hidden
                    Check(window != 0);
                    context = CreateContext(window);
                    Check(context != 0);
                }
                finally { ResetAttributes(); }
            }
            Activate();
            Gl = GL.GetApi(GetProcAddress);
            _ = Gl.GetStringS(StringName.Version);
        }
        catch { Dispose(); throw; }
    }

    internal bool TryActivate() => MakeCurrent(window, context) == 0;
    internal void Activate() => Check(TryActivate());
    internal nint GetProcAddress(string name) => GetAddress(name);

    private static void Check(bool success)
    {
        if (!success) throw new InvalidOperationException(Marshal.PtrToStringUTF8(GetError()) ?? "Контекст OpenGL недоступен.");
    }

    public void Dispose()
    {
        Gl?.Dispose();
        lock (CreationLock)
        {
            if (context != 0) { DeleteContext(context); context = 0; }
            if (window != 0) { DestroyWindow(window); window = 0; }
            if (initialized) { Quit(Video); initialized = false; }
        }
    }

    private const string Sdl = "SDL2";
    [LibraryImport(Sdl, EntryPoint = "SDL_InitSubSystem"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int Init(uint flags);
    [LibraryImport(Sdl, EntryPoint = "SDL_QuitSubSystem"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void Quit(uint flags);
    [LibraryImport(Sdl, EntryPoint = "SDL_GL_ResetAttributes"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void ResetAttributes();
    [LibraryImport(Sdl, EntryPoint = "SDL_GL_SetAttribute"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int SetAttribute(int attribute, int value);
    [LibraryImport(Sdl, EntryPoint = "SDL_CreateWindow", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint CreateWindow(string title, int x, int y, int width, int height, uint flags);
    [LibraryImport(Sdl, EntryPoint = "SDL_DestroyWindow"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DestroyWindow(nint window);
    [LibraryImport(Sdl, EntryPoint = "SDL_GL_CreateContext"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint CreateContext(nint window);
    [LibraryImport(Sdl, EntryPoint = "SDL_GL_DeleteContext"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial void DeleteContext(nint context);
    [LibraryImport(Sdl, EntryPoint = "SDL_GL_MakeCurrent"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial int MakeCurrent(nint window, nint context);
    [LibraryImport(Sdl, EntryPoint = "SDL_GL_GetProcAddress", StringMarshalling = StringMarshalling.Utf8), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint GetAddress(string name);
    [LibraryImport(Sdl, EntryPoint = "SDL_GetError"), UnmanagedCallConv(CallConvs = [typeof(CallConvCdecl)])]
    private static partial nint GetError();
}
