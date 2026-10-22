using System.IO;

namespace KNE;

/// <summary>Normalizes the user's extension list without reading or executing extension code.</summary>
internal static class ApplicationPluginFiles
{
    internal const int MaximumCount = 64;
    internal const long MaximumBytes = 8 * 1024 * 1024;
    internal static string[] Normalize(IEnumerable<string>? paths)
    {
        var result = new List<string>();
        foreach (string? path in paths ?? [])
        {
            if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path)) continue;
            try
            {
                string full = Path.GetFullPath(path);
                if (!Supported(full) || result.Contains(full, StringComparer.OrdinalIgnoreCase)) continue;
                result.Add(full);
                if (result.Count == MaximumCount) break;
            }
            catch (ArgumentException) { }
        }
        return result.ToArray();
    }
    internal static bool Supported(string path) => Path.GetExtension(path).ToLowerInvariant() is ".lua" or ".cs";
    internal static string Validate(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!Path.IsPathFullyQualified(path) || path.Length > 4096 || !Supported(path)) throw new ArgumentException("Выберите файл .lua или .cs по полному пути.");
        string full = Path.GetFullPath(path);
        if (!File.Exists(full)) throw new FileNotFoundException("Файл плагина не найден. Возможно, он перемещён или удалён: " + full, full);
        if (new FileInfo(full).Length > MaximumBytes) throw new InvalidDataException("Плагин больше 8 МБ: " + full);
        return full;
    }
}
