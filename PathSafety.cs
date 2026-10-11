using System.IO;

namespace KNE;

/// <summary>Validates Windows names and project paths before filesystem access.</summary>
public static class PathSafety
{
    public static string NormalizeDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    public static void ValidateFileName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (name.Length > 255 || name is "." or ".." || name != name.Trim() || name.EndsWith('.') ||
            name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new ArgumentException("Укажите имя без пути, запрещённых символов и точки в конце.", nameof(name));
        string stem = name.Split('.')[0].ToUpperInvariant();
        if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" ||
            (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) &&
             "123456789¹²³".Contains(stem[3])))
            throw new ArgumentException("Это имя зарезервировано Windows.", nameof(name));
    }

    public static string EnsureInside(string root, string path)
    {
        string directory = NormalizeDirectory(root);
        string fullPath = Path.GetFullPath(path);
        string prefix = Path.EndsInDirectorySeparator(directory) ? directory : directory + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Файл должен находиться внутри папки проекта.", nameof(path));
        RejectLinks(fullPath);
        return fullPath;
    }

    public static string ResolveRelative(string root, string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException("В project.json требуется относительный путь.", nameof(relativePath));
        return EnsureInside(root, Path.Combine(root, relativePath));
    }

    public static void RejectLinks(string path)
    {
        for (string? current = Path.GetFullPath(path); current != null; current = Path.GetDirectoryName(current))
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                    throw new IOException("Символические ссылки и точки соединения в путях проекта не поддерживаются: " + current);
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
        }
    }
}
