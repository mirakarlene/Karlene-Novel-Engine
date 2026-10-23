using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32.SafeHandles;

namespace KNE;

/// <summary>Copies Windows file-type icons into frozen WPF images and releases each native HICON.</summary>
internal static class WindowsFileIcons
{
    private static readonly Dictionary<string, ImageSource> cache = new(StringComparer.OrdinalIgnoreCase);
    internal static ImageSource? Get(string extension, bool directory)
    {
        string key = directory ? "<folder>" : extension.ToLowerInvariant();
        lock (cache)
        {
            if (cache.TryGetValue(key, out var existing)) return existing;
            // USEFILEATTRIBUTES obtains a type icon without opening a project file or executing its code.
            SHGetFileInfoW(directory ? "folder" : "file" + extension, directory ? 0x10u : 0x80u,
                out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100u | 0x10u);
            using var icon = new IconHandle(info.Icon);
            if (icon.IsInvalid) return null;
            var image = Imaging.CreateBitmapSourceFromHIcon(icon.DangerousGetHandle(), Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            if (cache.Count < 256) cache[key] = image;
            return image;
        }
    }

    // Runtime marshalling handles the two fixed UTF-16 arrays without enabling unsafe code for the project.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        internal nint Icon;
        internal int IconIndex;
        internal uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] internal string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern nuint SHGetFileInfoW(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("user32.dll", ExactSpelling = true, CallingConvention = CallingConvention.Winapi)]
    private static extern int DestroyIcon(nint icon);

    private sealed class IconHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        internal IconHandle(nint value) : base(true) => SetHandle(value);
        protected override bool ReleaseHandle() => DestroyIcon(handle) != 0;
    }
}
