using System.IO;

namespace KNE;

public sealed record SceneVisuals(string? Background, string? Character, string Position,
    string? Music, bool LoopMusic, long MusicVersion, string? Sound, long SoundVersion)
{
    public static SceneVisuals Empty { get; } = new(null, null, "center", null, false, 0, null, 0);
    public string Title { get; init; } = "";
    public VisualMotion BackgroundMotion { get; init; } = new();
    public VisualMotion CharacterMotion { get; init; } = new();
    public VisualAnimation BackgroundAnimation { get; init; } = new();
    public VisualAnimation CharacterAnimation { get; init; } = new();
    public SceneCamera Camera { get; init; } = new();
    public SceneShake Shake { get; init; } = new();
    public SceneFlash Flash { get; init; } = new();
    public string? OverlayScreen { get; init; }
    public long ScreenVersion { get; init; }
    public double MasterVolume { get; init; } = 1;
    public AudioChannelState[] AudioChannels { get; init; } = [];
}

public sealed record VisualMotion(double X = 0, double Y = 0, double Duration = 0, long Version = 0);
public sealed record VisualAnimation(string Kind = "none", double Duration = 0, double Amount = 1, long Version = 0);
public sealed record SceneCamera(double Zoom = 1, double X = 0, double Y = 0, double Duration = 0, long Version = 0);
public sealed record SceneShake(double Amount = 0, double Duration = 0, long Version = 0);
public sealed record SceneFlash(string Color = "#FFFFFF", double Duration = 0, long Version = 0);
public sealed record AudioChannelState(string Channel, string? Path = null, bool Loop = false, double Volume = 1, long Version = 0);

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
