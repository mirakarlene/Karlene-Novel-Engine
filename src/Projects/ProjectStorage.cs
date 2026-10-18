using System.IO;

namespace KNE;

/// <summary>A remembered or discovered project, including missing entries.</summary>
public sealed record StoredProject(string Path, bool IsAvailable)
{
    public string Label => System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Path));
}

/// <summary>Discovers only immediate projects in the configured storage directory.</summary>
public static class ProjectStorage
{
    /// <summary>Creates the named storage folder inside the chosen parent without moving existing projects.</summary>
    public static string Create(string parent, string name = "projects")
    {
        PathSafety.ValidateFileName(name);
        parent = PathSafety.NormalizeDirectory(parent); PathSafety.RejectLinks(parent);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Родительская папка не существует.");
        string path = PathSafety.ResolveRelative(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Checks a project path without scanning outside configured storage.</summary>
    public static bool IsAvailable(UserSettings settings, string path)
    {
        try
        {
            path = PathSafety.NormalizeDirectory(path);
            if (!string.IsNullOrWhiteSpace(settings.ProjectsStoragePath) &&
                !string.Equals(System.IO.Path.GetDirectoryName(path), PathSafety.NormalizeDirectory(settings.ProjectsStoragePath), StringComparison.OrdinalIgnoreCase)) return false;
            PathSafety.RejectLinks(path);
            return File.Exists(System.IO.Path.Combine(path, "project.json"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>Combines remembered entries (including unavailable ones) with discovered direct children.</summary>
    public static Task<IReadOnlyList<StoredProject>> DiscoverAsync(UserSettings settings, CancellationToken token = default)
    {
        string storage = settings.ProjectsStoragePath;
        string[] recent = settings.RecentProjects.ToArray();
        return Task.Run<IReadOnlyList<StoredProject>>(() =>
        {
            var paths = new List<string>(recent);
            if (!string.IsNullOrWhiteSpace(storage) && !Path.IsPathFullyQualified(storage)) throw new ArgumentException("Хранилище проектов должно иметь полный путь.");
            if (!string.IsNullOrWhiteSpace(storage) && Directory.Exists(storage))
            {
                PathSafety.RejectLinks(storage);
                foreach (string path in Directory.EnumerateDirectories(storage).Order(StringComparer.OrdinalIgnoreCase))
                {
                    token.ThrowIfCancellationRequested();
                    if (paths.Count >= 2000) break;
                    if (!System.IO.Path.GetFileName(path).StartsWith('.') && IsAvailable(settings, path)) paths.Add(path);
                }
            }
            return paths.Distinct(StringComparer.OrdinalIgnoreCase).Select(path => new StoredProject(path, IsAvailable(settings, path))).ToArray();
        }, token);
    }
}
