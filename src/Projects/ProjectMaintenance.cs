using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>Tracks deliberate confirmation clicks without performing filesystem operations.</summary>
internal sealed class ProjectDeletionConfirmation
{
    internal const int RequiredClicks = 72;
    internal int Remaining { get; private set; }
    internal bool IsArmed { get; private set; }
    internal void Begin() { Remaining = RequiredClicks; IsArmed = true; }
    internal void Cancel() { Remaining = 0; IsArmed = false; }
    internal bool RecordClick()
    {
        if (!IsArmed) return false;
        Remaining--;
        if (Remaining != 0) return false;
        IsArmed = false; return true;
    }
}

/// <summary>Changes a project's display name or removes exactly its validated project directory.</summary>
internal static class ProjectMaintenance
{
    internal static string ValidateName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        name = name.Trim();
        if (name.Length is < 1 or > 128 || name.Any(char.IsControl)) throw new ArgumentException("Название проекта: от 1 до 128 символов, без управляющих символов.", nameof(name));
        return name;
    }
    internal static async Task RenameAsync(ProjectInfo project, string name, CancellationToken token = default)
    {
        name = ValidateName(name);
        string path = PathSafety.ResolveRelative(project.Path, "project.json");
        var configuration = await ReadConfigurationAsync(path, token);
        configuration.Metadata ??= new(); configuration.GameInfo ??= new();
        configuration.Metadata.Name = name; configuration.GameInfo.Title = name;
        if (configuration.LegacyName != null) configuration.LegacyName = name;
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(configuration, ProjectConfiguration.JsonOptions), token);
            token.ThrowIfCancellationRequested(); PathSafety.RejectLinks(path); File.Move(temporary, path, true);
            project.Configuration.Metadata = configuration.Metadata; project.Configuration.GameInfo = configuration.GameInfo;
            project.Configuration.LegacyName = configuration.LegacyName;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static async Task DeleteAsync(ProjectInfo project, CancellationToken token = default)
    {
        if (!Path.IsPathFullyQualified(project.Path)) throw new ArgumentException("Для удаления требуется полный путь проекта.");
        string root = PathSafety.NormalizeDirectory(project.Path);
        string parent = Path.GetDirectoryName(root) ?? throw new ArgumentException("Нельзя удалить корень диска.");
        PathSafety.EnsureInside(parent, root); PathSafety.RejectLinks(root);
        string executable = Path.GetFullPath(AppContext.BaseDirectory);
        if (executable.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Нельзя удалить каталог, содержащий работающий KNE.");
        var actual = await ReadConfigurationAsync(PathSafety.ResolveRelative(root, "project.json"), token);
        if (actual.DisplayName(Path.GetFileName(root)) != project.Name) throw new IOException("Название проекта изменилось. Откройте проект заново перед удалением.");
        await Task.Run(() =>
        {
            // Validate every resolved target before the recursive delete, rejecting junctions and symlinks.
            var pending = new Stack<string>(); pending.Push(root);
            while (pending.TryPop(out var directory))
                foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                {
                    token.ThrowIfCancellationRequested(); string target = PathSafety.EnsureInside(root, entry);
                    if (Directory.Exists(target)) pending.Push(target);
                }
            token.ThrowIfCancellationRequested();
            PathSafety.EnsureInside(parent, root); PathSafety.RejectLinks(root);
            Directory.Delete(root, recursive: true);
        }, token);
    }
    private static async Task<ProjectConfiguration> ReadConfigurationAsync(string path, CancellationToken token)
    {
        if (new FileInfo(path).Length > 1024 * 1024) throw new InvalidDataException("project.json больше 1 МБ.");
        try { return JsonSerializer.Deserialize<ProjectConfiguration>(await File.ReadAllTextAsync(path, token), ProjectConfiguration.JsonOptions) ?? throw new InvalidDataException("project.json не должен быть null."); }
        catch (JsonException error) { throw new InvalidDataException("Некорректный project.json.", error); }
    }
}
