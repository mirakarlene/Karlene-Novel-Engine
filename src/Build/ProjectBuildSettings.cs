using System.IO;
using System.Text.Json;
using System.Text;

namespace KNE;

/// <summary>Project-owned Windows x64 test, distribution and executable metadata preferences.</summary>
public sealed record ProjectBuildSettings
{
    public string TestMode { get; set; } = "vm";
    public string TestPath { get; set; } = "";
    public string ReleaseMode { get; set; } = "self_contained";
    public string ReleasePath { get; set; } = "";
    public string Author { get; set; } = "";
    public string Team { get; set; } = "";
    public string Version { get; set; } = "1.0.0";
    public string SaveLocation { get; set; } = "appdata";
    public string GameId { get; set; } = "";
    public static IReadOnlyList<GraphicsOption<string>> TestModes { get; } = [new("vm", "VM (Worker Process)"), new("standalone", "Standalone Test"), new("remote_debug", "Remote Debug")];
    public static IReadOnlyList<GraphicsOption<string>> ReleaseModes { get; } = [new("framework_dependent", "Framework-Dependent"), new("self_contained", "Self-Contained"), new("single_file", "Single-File (сжатие, без trimming)"), new("msix", "MSIX / App Package"), new("portable", "Portable (ZIP)")];
    public string EffectiveSaveLocation => ReleaseMode == "portable" ? "local" : ReleaseMode == "msix" ? "appdata" : SaveLocation;

    public void Validate()
    {
        if (!TestModes.Any(item => item.Value == TestMode) || !ReleaseModes.Any(item => item.Value == ReleaseMode)) throw new InvalidDataException("Неизвестный режим сборки.");
        if (SaveLocation is not ("local" or "appdata")) throw new InvalidDataException("Неизвестная папка сохранений билда.");
        if (Author == null || Team == null || Author.Length > 128 || Team.Length > 128 || Author.Any(char.IsControl) || Team.Any(char.IsControl)) throw new InvalidDataException("Автор и команда: до 128 символов без управляющих знаков.");
        if (!System.Version.TryParse(Version, out var version) || version.Major > 65535 || version.Minor > 65535 || version.Build > 65535 || version.Revision > 65535 || Version.Contains('-')) throw new InvalidDataException("Версия: от двух до четырёх чисел 0…65535, например 1.0.0.");
        if (GameId != "" && !Guid.TryParseExact(GameId, "N", out _)) throw new InvalidDataException("Некорректный ID игры.");
        foreach (string path in new[] { TestPath, ReleasePath })
            if (path == null || path.Length > 2000 || path.Any(char.IsControl) || path.Length > 0 && !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Для билда укажите полный путь к папке.");
        if (TestMode != "vm" && string.IsNullOrWhiteSpace(TestPath)) throw new InvalidDataException("Укажите папку тестового билда.");
    }

    internal string FourPartVersion
    {
        get { var v = System.Version.Parse(Version); return $"{v.Major}.{v.Minor}.{Math.Max(0, v.Build)}.{Math.Max(0, v.Revision)}"; }
    }

    public static string DefaultOutput(string projectPath, string? storagePath = null)
    {
        string parent = Path.GetDirectoryName(PathSafety.NormalizeDirectory(string.IsNullOrWhiteSpace(storagePath) ? projectPath : storagePath))!;
        return Path.Combine(parent, "output", Path.GetFileName(PathSafety.NormalizeDirectory(projectPath)));
    }
}

/// <summary>Build-profile persistence with canonical project.json metadata and read-only legacy fallback.</summary>
public static class ProjectBuildSettingsStore
{
    public static ProjectBuildSettings Load(string root)
    {
        string file = PathSafety.ResolveRelative(root, "engine/build.json");
        if (File.Exists(file) && new FileInfo(file).Length > 32768) throw new InvalidDataException("Параметры сборки больше 32 КБ.");
        var value = ProjectBuildMetadata.Resolve(root, ReadBuild(File.Exists(file) ? File.ReadAllBytes(file) : null));
        value.Validate(); return value;
    }

    private static ProjectBuildSettings ReadBuild(byte[]? bytes)
    {
        if (bytes == null) return new();
        if (bytes.Length > 32768) throw new InvalidDataException("Параметры сборки больше 32 КБ.");
        try
        {
            using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return JsonSerializer.Deserialize<ProjectBuildSettings>(reader.ReadToEnd(), EngineSettings.JsonOptions) ?? throw new InvalidDataException("Параметры сборки не должны быть null.");
        }
        catch (JsonException error) { throw new InvalidDataException("Ошибка engine/build.json: " + error.Message, error); }
    }

    public static Task SaveAsync(string root, ProjectBuildSettings value, CancellationToken token = default) => SaveCoreAsync(root, value, null, null, token);

    internal static Task<ProjectBuildWrite> SaveCheckedAsync(string root, ProjectBuildSettings value, ProjectExeMetadata expected, string? title = null, CancellationToken token = default, Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject>? update = null) => SaveCoreAsync(root, value, expected, title, token, update);

    private static async Task<ProjectBuildWrite> SaveCoreAsync(string root, ProjectBuildSettings value, ProjectExeMetadata? expected, string? title, CancellationToken token, Func<System.Text.Json.Nodes.JsonObject, System.Text.Json.Nodes.JsonObject>? update = null)
    {
        value.Validate();
        if (value.GameId == "") value = value with { GameId = Guid.NewGuid().ToString("N") };
        if (title != null) title = ProjectMaintenance.ValidateName(title);
        var project = ProjectBuildMetadata.Read(root);
        string file = PathSafety.ResolveRelative(root, "engine/build.json");
        byte[]? previousBuild = File.Exists(file) ? await File.ReadAllBytesAsync(file, token) : null;
        if (expected != null && expected != ProjectExeMetadata.From(project.Apply(ReadBuild(previousBuild))))
            throw new IOException("Метаданные проекта изменены вне окна параметров. Откройте параметры заново, чтобы загрузить изменения.");
        if (update != null) project = project with { Configuration = update(project.Configuration) };
        byte[] nextBuild = JsonSerializer.SerializeToUtf8Bytes(value, EngineSettings.JsonOptions), nextProject = project.Updated(value, title);
        if (nextProject.Length > 1024 * 1024) throw new InvalidDataException("project.json больше 1 МБ.");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        string projectTemp = project.Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        bool buildCommitted = false;
        try
        {
            await File.WriteAllBytesAsync(temp, nextBuild, token); await File.WriteAllBytesAsync(projectTemp, nextProject, token); token.ThrowIfCancellationRequested();
            ProjectBuildMetadata.CheckCurrent(file, previousBuild); ProjectBuildMetadata.CheckCurrent(project.Path, project.Original);
            File.Move(temp, file, true); buildCommitted = true;
            ProjectBuildMetadata.CheckCurrent(project.Path, project.Original); File.Move(projectTemp, project.Path, true);
            return new(value, nextBuild, nextProject, project.Original, previousBuild);
        }
        catch (Exception error)
        {
            if (buildCommitted)
            {
                try { ProjectBuildMetadata.CheckCurrent(file, nextBuild); await ProjectBuildMetadata.ReplaceAsync(file, previousBuild); }
                catch (Exception rollback) { throw new AggregateException("Сохранение метаданных не завершено; не удалось отменить изменение профиля сборки.", error, rollback); }
            }
            throw;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); if (File.Exists(projectTemp)) File.Delete(projectTemp); }
    }
}
