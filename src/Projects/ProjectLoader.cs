using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;

namespace KNE;

/// <summary>A loaded project and its resolved entry points.</summary>
public sealed class ProjectInfo
{
    public required string Path { get; init; }
    public required ProjectConfiguration Configuration { get; init; }
    public string Name => Configuration.DisplayName(System.IO.Path.GetFileName(Path));
    public string Title => string.IsNullOrWhiteSpace(Configuration.GameInfo?.Title) ? Name : Configuration.GameInfo.Title;
    public ObservableCollection<ProjectFile> Files { get; } = [];
    public List<string> Warnings { get; } = [];
    public EngineSettings Engine { get; init; } = new();

    public string MainScenePath => ResolveEntry(Configuration.EntryPoints?.MainScene, ["game/scenes/main.krl", "main.krl"], ".krl");
    public string InitScriptPath => ResolveEntry(Configuration.EntryPoints?.InitScript, ["game/scripts/gamelogic.lua", "game/scripts/game_init.lua", "gamelogic.lua"], null);

    private string ResolveEntry(string? configured, string[] defaults, string? extension)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string declaredPath = PathSafety.ResolveRelative(Path, configured);
            if (File.Exists(declaredPath)) return declaredPath;
            // Older KNE templates declared game_init.lua but created gamelogic.lua.
            if (configured.Replace('\\', '/') == "game/scripts/game_init.lua")
            {
                string legacyPath = PathSafety.ResolveRelative(Path, "game/scripts/gamelogic.lua");
                if (File.Exists(legacyPath)) return legacyPath;
            }
            throw new FileNotFoundException("Не найден файл, указанный в entry_points: " + configured, declaredPath);
        }
        foreach (string candidate in defaults)
        {
            string fullPath = PathSafety.ResolveRelative(Path, candidate);
            if (File.Exists(fullPath)) return fullPath;
        }
        if (extension != null)
        {
            var candidates = Files.Where(f => System.IO.Path.GetExtension(f.FilePath).Equals(extension, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length == 1 && File.Exists(candidates[0].FilePath))
                return PathSafety.EnsureInside(Path, candidates[0].FilePath);
        }
        throw new FileNotFoundException("Укажите существующий файл в entry_points: " + (extension == null ? "init_script" : "main_scene"));
    }
}

public static class ProjectLoader
{
    public static async Task<ProjectInfo> LoadProjectAsync(string projectPath, CancellationToken cancellationToken = default)
    {
        string root = PathSafety.NormalizeDirectory(projectPath);
        PathSafety.RejectLinks(root);
        string configPath = PathSafety.ResolveRelative(root, "project.json");
        string json = await File.ReadAllTextAsync(configPath, cancellationToken);
        ProjectConfiguration configuration;
        try
        {
            configuration = JsonSerializer.Deserialize<ProjectConfiguration>(json, ProjectConfiguration.JsonOptions)
                ?? throw new InvalidDataException("project.json не должен содержать null.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Некорректный project.json: " + ex.Message, ex);
        }

        bool hasEngine = File.Exists(PathSafety.ResolveRelative(root, "engine/window.json")) ||
            File.Exists(PathSafety.ResolveRelative(root, "engine/screens.json")) ||
            File.Exists(PathSafety.ResolveRelative(root, "engine/theme.json")) ||
            File.Exists(PathSafety.ResolveRelative(root, "engine/game.json")) ||
            File.Exists(PathSafety.ResolveRelative(root, "engine/state.json"));
        var engine = hasEngine ? EngineSettings.Load(root) : new EngineSettings
        {
            Window = new WindowDefaults { Width = 800, Height = 600 },
            Theme = new GameTheme { BackgroundColor = "#FF2D2D30", Pattern = false },
            Game = new GameDefaults { MainMenu = false },
            Performance = PerformanceSettingsStore.LoadProject(root), ScenePerformance = PerformanceSettingsStore.LoadScenes(root)
        };
        var result = new ProjectInfo { Path = root, Configuration = configuration, Engine = engine };
        var snapshot = await Task.Run(() => EnumerateFiles(root, cancellationToken), cancellationToken);
        foreach (string file in snapshot.Files) result.Files.Add(new ProjectFile { FilePath = file });
        result.Warnings.AddRange(snapshot.Warnings);
        return result;
    }

    internal static (List<string> Files, List<string> Warnings) EnumerateFiles(string root, CancellationToken cancellationToken)
    {
        var files = new List<string>();
        var warnings = new List<string>();
        var pending = new Stack<string>();
        string savesDirectory = PerformanceSettingsStore.LoadProject(root).ResolveSavesPath(root);
        pending.Push(root);
        while (pending.TryPop(out string? directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string entry in Directory.EnumerateFileSystemEntries(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.Equals(savesDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                if (directory.Equals(root, StringComparison.OrdinalIgnoreCase) &&
                    (Path.GetFileName(entry).Equals("saves", StringComparison.OrdinalIgnoreCase) ||
                     Path.GetFileName(entry).Equals(".kne-backups", StringComparison.OrdinalIgnoreCase))) continue;
                FileAttributes attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warnings.Add("Пропущена символическая ссылка: " + entry);
                    continue;
                }
                if ((attributes & FileAttributes.Directory) != 0) pending.Push(entry);
                else files.Add(entry);
            }
        }
        files.Sort(StringComparer.OrdinalIgnoreCase);
        return (files, warnings);
    }
}
