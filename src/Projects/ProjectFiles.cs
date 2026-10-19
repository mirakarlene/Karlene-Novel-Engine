using System.IO;

namespace KNE;

/// <summary>File operations confined to a loaded project. Existing files are never overwritten.</summary>
public sealed class ProjectFiles(ProjectInfo project)
{
    public string Create(string path)
    {
        string target = PathSafety.EnsureInside(project.Path, path);
        PathSafety.ValidateFileName(Path.GetFileName(target));
        using var stream = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        return target;
    }

    public string Duplicate(ProjectFile file, string path)
    {
        string source = ValidateSource(file);
        string target = PathSafety.EnsureInside(project.Path, path);
        PathSafety.ValidateFileName(Path.GetFileName(target));
        File.Copy(source, target, overwrite: false);
        return target;
    }

    public string Rename(ProjectFile file, string name)
    {
        string source = ValidateSource(file);
        EnsureNotProtected(source);
        PathSafety.ValidateFileName(name);
        string target = PathSafety.EnsureInside(project.Path, Path.Combine(Path.GetDirectoryName(source)!, name));
        if (source.Equals(target, StringComparison.Ordinal)) return source;
        File.Move(source, target, overwrite: false);
        return target;
    }

    /// <summary>Moves an ordinary project file to a new full path without overwriting another file.</summary>
    /// <param name="file">File from the current loaded project snapshot.</param>
    /// <param name="path">Destination file path inside this project, in an existing directory.</param>
    /// <returns>The validated destination path.</returns>
    public string Move(ProjectFile file, string path)
    {
        string source = ValidateSource(file);
        EnsureNotProtected(source);
        string target = PathSafety.EnsureInside(project.Path, path);
        PathSafety.ValidateFileName(Path.GetFileName(target));
        if (source.Equals(target, StringComparison.Ordinal)) return source;
        File.Move(source, target, overwrite: false);
        return target;
    }

    public void Delete(ProjectFile file)
    {
        string source = ValidateSource(file);
        EnsureNotProtected(source);
        File.Delete(source);
    }

    public string ValidateSource(ProjectFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string source = PathSafety.EnsureInside(project.Path, file.FilePath);
        if (!project.Files.Any(f => f.FilePath.Equals(source, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("Файл отсутствует в списке текущего проекта. Обновите список.");
        if (!File.Exists(source)) throw new FileNotFoundException("Файл больше не существует.", source);
        return source;
    }

    private void EnsureNotProtected(string source)
    {
        if (source.Equals(Path.Combine(project.Path, "project.json"), StringComparison.OrdinalIgnoreCase) ||
            source.Equals(ResolveProtectedEntry(() => project.MainScenePath, project.Configuration.EntryPoints?.MainScene), StringComparison.OrdinalIgnoreCase) ||
            source.Equals(ResolveProtectedEntry(() => project.InitScriptPath, project.Configuration.EntryPoints?.InitScript), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Нельзя удалить, переименовать или переместить конфигурацию и точки входа проекта. Для смены сценария сначала измените entry_points в project.json.");
    }

    private string? ResolveProtectedEntry(Func<string> resolve, string? configured)
    {
        try { return resolve(); }
        catch (FileNotFoundException)
        {
            return string.IsNullOrWhiteSpace(configured) ? null : PathSafety.ResolveRelative(project.Path, configured);
        }
    }
}
