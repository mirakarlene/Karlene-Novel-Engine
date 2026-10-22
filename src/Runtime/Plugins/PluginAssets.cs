using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Bounded HTTP and uncached, disposable-friendly resource loading for trusted extensions.</summary>
public sealed class PluginAssets : IDisposable
{
    private readonly HttpClient http;
    private readonly string projectDirectory;
    private bool disposed;
    public PluginAssets(string projectDirectory) : this(projectDirectory, new HttpClientHandler()) { }
    public PluginAssets(string projectDirectory, HttpMessageHandler handler)
    { this.projectDirectory = PathSafety.NormalizeDirectory(projectDirectory); http = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(30) }; }
    private const int Limit = 32 * 1024 * 1024;
    public string Local(string path, params string[] extensions)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = Path.GetFullPath(path, projectDirectory);
        if (!File.Exists(full)) throw new FileNotFoundException("Ресурс плагина не найден.", full);
        if (extensions.Length > 0 && !extensions.Contains(Path.GetExtension(full), StringComparer.OrdinalIgnoreCase)) throw new ArgumentException("Неподдерживаемый тип ресурса.");
        if (new FileInfo(full).Length > Limit) throw new InvalidDataException("Ресурс плагина больше 32 МБ.");
        return full;
    }
    public BitmapSource LoadImage(string path)
    {
        using var stream = File.OpenRead(Local(path, ".png", ".jpg", ".jpeg", ".bmp", ".webp"));
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation, BitmapCacheOption.None).Frames[0];
        if (frame.PixelWidth > 16384 || frame.PixelHeight > 16384 || (long)frame.PixelWidth * frame.PixelHeight > 64_000_000) throw new InvalidDataException("Изображение плагина слишком велико.");
        int width = frame.PixelWidth, height = frame.PixelHeight; stream.Position = 0;
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        if (width > 4096 || height > 4096) image.DecodePixelWidth = Math.Max(1, (int)(width * (4096d / Math.Max(width, height))));
        image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    public IReadOnlyList<FontFamily> LoadFonts(string path) => Fonts.GetFontFamilies(new Uri(Path.GetDirectoryName(Local(path, ".ttf", ".otf"))! + Path.DirectorySeparatorChar)).ToArray();
    /// <summary>Copies an external resource into the project's content-addressed cache and returns a portable relative path.</summary>
    public string ImportLocal(string path)
    {
        string source = Local(path, ".png", ".jpg", ".jpeg", ".bmp", ".wav", ".mp3", ".ogg", ".ttf", ".otf");
        using var stream = File.OpenRead(source);
        string relative = ".kne/plugin-assets/" + Convert.ToHexString(SHA256.HashData(stream)) + Path.GetExtension(source).ToLowerInvariant();
        string destination = PathSafety.ResolveRelative(projectDirectory, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (!File.Exists(destination)) File.Copy(source, destination, false);
        return relative;
    }
    /// <summary>Sends HTTP(S) requests. The caller owns the request; response bytes are bounded to 32 MiB.</summary>
    public async Task<byte[]> SendAsync(HttpRequestMessage request, CancellationToken token = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.RequestUri is not { IsAbsoluteUri: true } uri || uri.Scheme is not ("https" or "http")) throw new ArgumentException("Требуется абсолютный HTTP(S) URL.");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > Limit) throw new InvalidDataException("Ответ больше 32 МБ.");
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var target = new MemoryStream(); byte[] buffer = new byte[32768]; int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (target.Length + read > Limit) throw new InvalidDataException("Ответ больше 32 МБ.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }
    /// <summary>Downloads to a project cache with a content hash; no executable is launched.</summary>
    public async Task<string> DownloadAsync(Uri url, string extension, CancellationToken token = default)
    {
        if (extension is not (".png" or ".jpg" or ".wav" or ".mp3" or ".ogg" or ".ttf" or ".otf")) throw new ArgumentException("Укажите расширение ресурса изображения, звука или шрифта.");
        using var request = new HttpRequestMessage(HttpMethod.Get, url); byte[] data = await SendAsync(request, token).ConfigureAwait(false);
        string directory = PathSafety.ResolveRelative(projectDirectory, ".kne/plugin-assets"); Directory.CreateDirectory(directory);
        string path = PathSafety.ResolveRelative(projectDirectory, ".kne/plugin-assets/" + Convert.ToHexString(SHA256.HashData(data)) + extension);
        if (!File.Exists(path)) await File.WriteAllBytesAsync(path, data, token).ConfigureAwait(false);
        return path;
    }
    public void Dispose() { if (disposed) return; disposed = true; http.Dispose(); }
}
