using System.IO;

namespace KNE;

public sealed record SceneVisuals(string? Background, string? Character, string Position,
    string? Music, bool LoopMusic, long MusicVersion, string? Sound, long SoundVersion)
{
    public static SceneVisuals Empty { get; } = new(null, null, "center", null, false, 0, null, 0);
}

public static class ResourcePaths
{
    public static string Resolve(string root, string relative, bool audio = false)
    {
        string path = PathSafety.ResolveRelative(root, relative);
        string extension = Path.GetExtension(path).ToLowerInvariant();
        bool supported = audio ? extension is ".wav" or ".mp3" or ".wma" or ".m4a" : extension is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif";
        if (!supported) throw new ArgumentException(audio ? "Нужен WAV, MP3, WMA или M4A." : "Нужно изображение PNG, JPG, BMP или GIF.");
        if (!File.Exists(path)) throw new FileNotFoundException("Ресурс не найден: " + relative, path);
        return path;
    }
}
