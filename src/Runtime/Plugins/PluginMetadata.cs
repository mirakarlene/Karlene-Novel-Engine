using System.IO;
using System.Text;
using System.Text.Json;

namespace KNE;

/// <summary>Reads publisher metadata without executing plugin code.</summary>
internal sealed record PluginMetadata(string Name, string Author, string CreatedAt, string Language, string Readme, string License)
{
    internal static PluginMetadata Read(string source)
    {
        string path = Path.ChangeExtension(source, ".kneplugin.json");
        PluginManifest? manifest = File.Exists(path) ? ReadManifest(path) : null;
        string directory = Path.GetDirectoryName(path)!;
        return new(manifest?.Name ?? Path.GetFileName(source), manifest?.Author ?? "Не указан",
            manifest?.CreatedAt?.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture) ?? "Не указана",
            Path.GetExtension(source).Equals(".lua", StringComparison.OrdinalIgnoreCase) ? "Lua" : "C#",
            ReadText(directory, manifest?.ReadmeFile) ?? manifest?.Readme ?? "Инструкция не предоставлена.",
            ReadText(directory, manifest?.LicenseFile) ?? manifest?.License ?? "Лицензия не указана.");
    }

    internal static PluginManifest ReadManifest(string path)
    {
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Манифест плагина больше 64 КБ: " + path);
        var manifest = JsonSerializer.Deserialize<PluginManifest>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Пустой манифест плагина: " + path);
        if (manifest.ApiVersion != 1) throw new InvalidDataException("Неподдерживаемая версия API плагина: " + path);
        return manifest;
    }

    private static string? ReadText(string directory, string? relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        string path = PathSafety.ResolveRelative(directory, relative);
        if (new FileInfo(path).Length > 256 * 1024) throw new InvalidDataException("Документ плагина больше 256 КБ: " + relative);
        return File.ReadAllText(path, new UTF8Encoding(false, true));
    }
}
