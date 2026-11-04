using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

internal static class ProjectIcon
{
    internal static ImageSource? Load(string root, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        string path = PathSafety.ResolveRelative(root, relative);
        if (new FileInfo(path).Length > 4 * 1024 * 1024) throw new InvalidDataException("Иконка больше 4 МБ.");
        using var input = File.OpenRead(path); var bitmap = BitmapDecoder.Create(input, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0]; bitmap.Freeze(); return bitmap;
    }
    internal static void WriteExeIcon(string root, string relative, string target)
    {
        var image = (BitmapSource)(Load(root, relative) ?? throw new InvalidDataException("Иконка не найдена."));
        var drawing = new DrawingVisual(); using (var context = drawing.RenderOpen()) context.DrawImage(image, new System.Windows.Rect(0, 0, 256, 256));
        var frame = new RenderTargetBitmap(256, 256, 96, 96, PixelFormats.Pbgra32); frame.Render(drawing);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(frame)); using var png = new MemoryStream(); encoder.Save(png);
        using var file = File.Create(target); using var writer = new BinaryWriter(file);
        writer.Write((ushort)0); writer.Write((ushort)1); writer.Write((ushort)1); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((byte)0); writer.Write((ushort)1); writer.Write((ushort)32); writer.Write((int)png.Length); writer.Write(22); writer.Write(png.ToArray());
    }
}
