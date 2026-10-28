using System.IO;
using System.Text.Json;

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

/// <summary>Atomic optional engine/build.json persistence; older projects keep VM defaults without migration writes.</summary>
public static class ProjectBuildSettingsStore
{
    public static ProjectBuildSettings Load(string root)
    {
        string file = PathSafety.ResolveRelative(root, "engine/build.json");
        if (!File.Exists(file)) return new();
        if (new FileInfo(file).Length > 32768) throw new InvalidDataException("Параметры сборки больше 32 КБ.");
        try { var value = JsonSerializer.Deserialize<ProjectBuildSettings>(File.ReadAllText(file), EngineSettings.JsonOptions) ?? throw new InvalidDataException("Параметры сборки не должны быть null."); value.Validate(); return value; }
        catch (JsonException error) { throw new InvalidDataException("Ошибка engine/build.json: " + error.Message, error); }
    }

    public static async Task SaveAsync(string root, ProjectBuildSettings value, CancellationToken token = default)
    {
        value.Validate();
        if (value.GameId == "") value = value with { GameId = Guid.NewGuid().ToString("N") };
        string file = PathSafety.ResolveRelative(root, "engine/build.json"); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        string temp = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(value, EngineSettings.JsonOptions), token); token.ThrowIfCancellationRequested(); File.Move(temp, file, true); }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
}
