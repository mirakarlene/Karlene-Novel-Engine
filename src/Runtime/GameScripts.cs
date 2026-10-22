using System.IO;

namespace KNE;

/// <summary>A read-only catalog of project functions and the single game entry point.</summary>
public sealed class GameScripts
{
    public IReadOnlyList<GameScript> Files { get; }
    public IReadOnlyList<GameFunction> Functions { get; }
    public GameFunction Main { get; }
    public IReadOnlyList<GameScript> Initialization { get; }

    private GameScripts(List<GameScript> files, List<GameFunction> functions, GameFunction main)
    {
        Files = files; Functions = functions; Main = main;
        Initialization = files.Where(f => f.Priority != null).OrderBy(f => f.Priority).ThenBy(f => f.Path, StringComparer.Ordinal).ToArray();
    }

    /// <summary>Validates all declarations before any project code runs; main must be globally unique.</summary>
    public static GameScripts Load(ProjectInfo project, EditorPreviewSource? editorSource = null, bool pluginsEnabled = true)
    {
        string init = project.InitScriptPath;
        var extensionSources = pluginsEnabled ? PluginHost.ManifestSourcePaths(project.Path) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var applicationSources = ApplicationPluginFiles.Normalize(project.ApplicationPlugins).ToHashSet(StringComparer.OrdinalIgnoreCase);
        extensionSources.UnionWith(applicationSources);
        var paths = ProjectLoader.EnumerateFiles(project.Path, CancellationToken.None).Files
            .Where(p => ScriptDocument.IsCodePath(p) || ScriptDocument.IsVisualPath(p))
            .Where(p => pluginsEnabled || !EngineLaunch.IsPluginPath(Path.GetRelativePath(project.Path, p)) && !applicationSources.Contains(p))
            .Append(init).Append(project.MainScenePath).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(p => Path.GetRelativePath(project.Path, p), StringComparer.Ordinal);
        var files = new List<GameScript>(); var functions = new List<GameFunction>();
        foreach (string candidate in paths)
        {
            string full = PathSafety.EnsureInside(project.Path, candidate);
            string path = Path.GetRelativePath(project.Path, full).Replace('\\', '/');
            string source = "";
            try
            {
                if (new FileInfo(full).Length > 8 * 1024 * 1024) throw new InvalidDataException("Сценарий больше 8 МБ: " + path);
                bool overridden = editorSource != null && path.Equals(editorSource.RelativePath(project.Path), StringComparison.OrdinalIgnoreCase);
                source = overridden ? editorSource!.Source : File.ReadAllText(full);
                if (ScriptDocument.IsVisualPath(full) && !overridden)
                {
                    var graph = VisualScript.Parse(source);
                    files.Add(new(path, source, null, graph));
                    functions.Add(new(graph.FunctionName, path, 1));
                }
                else
                {
                    if (extensionSources.Contains(full)) { files.Add(new(path, source, null, null)); continue; }
                    var declaration = StartupScripts.Declaration(source);
                    files.Add(new(path, LuaSource.Prepare(declaration.Source), declaration.Priority ?? (full.Equals(init, StringComparison.OrdinalIgnoreCase) ? 0 : null), null));
                    functions.AddRange(LuaSource.Functions(source).Select(f => new GameFunction(f.Name, path, f.Line)));
                }
            }
            catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                int? line = ex.InnerException is System.Text.Json.JsonException { LineNumber: { } jsonLine } ? checked((int)jsonLine + 1) :
                    ScriptDocument.IsCodePath(full) && ex is InvalidDataException ? LuaSource.FirstExecutableLine(source) : null;
                GameErrorReport.Attach(ex, GameErrorReport.Create(project, ex, "Проверка сценариев") with { File = path, Line = line });
                throw;
            }
        }
        var mains = functions.Where(f => f.Name == "main").ToArray();
        if (mains.Length != 1)
            throw new InvalidDataException(mains.Length == 0 ? "Ошибка запуска: в проекте нужна одна функция main(). Ни одного объявления не найдено." :
                "Ошибка запуска: функция main() должна быть одна. Найдено: " + mains.Length + ".\n" +
                string.Join("\n", mains.Select(f => f.Path + ":" + f.Line)));
        return new(files, functions, mains[0]);
    }

    public GameScript FindFile(string path) => Files.FirstOrDefault(f => f.Path.Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase))
        ?? throw new FileNotFoundException("Сценарий не найден в проекте: " + path);

    public string PreviewFunction(string path)
    {
        var names = Functions.Where(f => f.Path.Equals(path.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (names.Any(f => f.Name == "main")) return "main";
        if (names.Length == 1) return names[0].Name;
        throw new InvalidDataException("Для предпросмотра файл должен содержать одну именованную игровую функцию или main().");
    }
}

public sealed record GameFunction(string Name, string Path, int Line);
public sealed record GameScript(string Path, string Source, int? Priority, VisualScript? Graph)
{
    public string ExecutableSource => Graph?.Compile() ?? Source;
}
