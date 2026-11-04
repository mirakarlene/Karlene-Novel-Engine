using System.IO;

namespace KNE;

/// <summary>Project-owned execution and distribution policy, shared by test workers and released players.</summary>
public sealed class ProjectSecuritySettings
{
    public bool EncryptScripts { get; set; }
    public bool ReleaseDebugConsole { get; set; }
    public bool CrashReports { get; set; } = true;
    public bool AllowPlugins { get; set; } = true;
    public bool AllowNetwork { get; set; } = true;
    public string FileSystem { get; set; } = "full";
    internal bool Restricted => !AllowPlugins || !AllowNetwork || FileSystem == "project";
    internal static ProjectSecuritySettings Load(string root)
    {
        string path = PathSafety.ResolveRelative(root, "engine/security.json");
        if (!File.Exists(path)) return new();
        if (new FileInfo(path).Length > 65536) throw new InvalidDataException("Политика безопасности больше 64 КБ.");
        var value = System.Text.Json.JsonSerializer.Deserialize<ProjectSecuritySettings>(File.ReadAllText(path), EngineSettings.JsonOptions) ?? throw new InvalidDataException("Пустая политика безопасности."); value.Validate(); return value;
    }
    public void Validate()
    {
        if (FileSystem is not ("full" or "project")) throw new InvalidDataException("Доступ к файловой системе: project или full.");
    }
    internal void DemandCSharp()
    {
        if (!AllowPlugins || Restricted) throw new InvalidOperationException("C#-плагины запрещены политикой проекта: для них необходимы разрешённые плагины, сеть и полный доступ к файловой системе.");
    }
    internal string ResolveFile(string root, string path)
    {
        string full = Path.GetFullPath(path, root);
        if (!AllowNetwork && (full.StartsWith(@"\\", StringComparison.Ordinal) || new DriveInfo(Path.GetPathRoot(full)!).DriveType == DriveType.Network))
            throw new InvalidOperationException("Сетевые файловые пути запрещены политикой проекта.");
        if (FileSystem == "project") full = PathSafety.EnsureInside(root, full);
        PathSafety.RejectLinks(full); return full;
    }
}
