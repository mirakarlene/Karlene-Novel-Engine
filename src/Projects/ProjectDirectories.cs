using System.IO;

namespace KNE;

internal static class ProjectDirectories
{
    internal static string Create(string projectRoot, string destination)
    {
        string target = CheckDestination(projectRoot, destination);
        Directory.CreateDirectory(target);
        return target;
    }

    internal static string Validate(ProjectInfo project, string relative)
    {
        string full = PathSafety.ResolveRelative(project.Path, relative);
        if (!project.Directories.Contains(full, StringComparer.OrdinalIgnoreCase) || !Directory.Exists(full))
            throw new DirectoryNotFoundException("Обновите список папок проекта.");
        return full;
    }

    internal static void CheckEditable(ProjectInfo project, string source)
    {
        var entries = project.Configuration.EntryPoints;
        var protectedPaths = new List<string> { Path.Combine(project.Path, "engine") };
        if (entries?.MainScene is { Length: > 0 } main) protectedPaths.Add(PathSafety.ResolveRelative(project.Path, main));
        if (entries?.InitScript is { Length: > 0 } init) protectedPaths.Add(PathSafety.ResolveRelative(project.Path, init));
        protectedPaths.AddRange(ProjectLifecycle.Paths(entries).Select(path => PathSafety.ResolveRelative(project.Path, path)));
        try { protectedPaths.Add(project.MainScenePath); } catch (FileNotFoundException) { }
        try { protectedPaths.Add(project.InitScriptPath); } catch (FileNotFoundException) { }
        if (protectedPaths.Any(path => Contains(source, path)))
            throw new InvalidOperationException("Папка содержит настройки или точку входа проекта.");
    }

    internal static void Move(string projectRoot, string source, string destination)
    {
        source = PathSafety.EnsureInside(projectRoot, source);
        string target = CheckDestination(projectRoot, destination);
        if (Contains(source, target)) throw new IOException("Нельзя переместить папку внутрь самой себя.");
        Directory.Move(source, target);
    }

    internal static void Delete(string projectRoot, string source)
    {
        source = PathSafety.EnsureInside(projectRoot, source);
        ValidateTree(source);
        Directory.Delete(source, recursive: true);
    }

    internal static async Task CopyAsync(string projectRoot, string source, string destination, CancellationToken token)
    {
        source = PathSafety.NormalizeDirectory(source);
        PathSafety.RejectLinks(source);
        string target = CheckDestination(projectRoot, destination);
        if (Contains(source, target)) throw new IOException("Нельзя копировать папку внутрь самой себя.");
        string staging = PathSafety.EnsureInside(projectRoot,
            Path.Combine(Path.GetDirectoryName(target)!, ".kne-folder-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(staging);
        try
        {
            foreach (string entry in Entries(source))
            {
                token.ThrowIfCancellationRequested();
                string output = PathSafety.ResolveRelative(staging, Path.GetRelativePath(source, entry));
                if (Directory.Exists(entry)) Directory.CreateDirectory(output);
                else
                {
                    await using var input = File.OpenRead(entry);
                    await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await input.CopyToAsync(file, token).ConfigureAwait(false);
                }
            }
            token.ThrowIfCancellationRequested();
            Directory.Move(staging, target);
        }
        finally
        {
            if (Directory.Exists(staging)) Directory.Delete(PathSafety.EnsureInside(projectRoot, staging), recursive: true);
        }
    }

    internal static bool Contains(string directory, string path) => path.Equals(directory, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.TrimEndingDirectorySeparator(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string CheckDestination(string root, string destination)
    {
        string target = PathSafety.EnsureInside(root, destination);
        PathSafety.ValidateFileName(Path.GetFileName(target));
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("Папка или файл с таким именем уже существует.");
        if (!Directory.Exists(Path.GetDirectoryName(target))) throw new DirectoryNotFoundException("Папка назначения больше не существует.");
        return target;
    }

    private static void ValidateTree(string source)
    {
        foreach (string entry in Entries(source)) { }
    }

    private static IEnumerable<string> Entries(string root)
    {
        foreach (string entry in Directory.EnumerateFileSystemEntries(root))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Папка содержит ссылку: " + entry);
            yield return entry;
            if ((attributes & FileAttributes.Directory) != 0)
                foreach (string child in Entries(entry)) yield return child;
        }
    }
}
