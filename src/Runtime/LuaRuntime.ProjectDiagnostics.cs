using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private void InitializeProjectDiagnostics()
    {
        Register(nameof(ReadProjectDiagnosticsForLua), "kne_read_project_diagnostics");
        lua!.DoString("""
            do
                local read_report, compile = kne_read_project_diagnostics, load
                function project_diagnostics()
                    return assert(compile('return ' .. read_report(), 'KNE project diagnostics', 't', {}))()
                end
            end
            """, "KNE project diagnostics");
    }

    /// <summary>Returns startup checks for the current project as a Lua table literal.</summary>
    public string ReadProjectDiagnosticsForLua() => BuildProjectDiagnostics(project, scripts, executionToken);

    internal static void ReportStockProjectOpening(ProjectInfo project, GameScripts scripts, Action<string> output, CancellationToken token)
    {
        string initialization = Path.GetRelativePath(project.Path, project.InitScriptPath).Replace('\\', '/');
        var script = scripts.Initialization.FirstOrDefault(file => file.Path.Equals(initialization, StringComparison.OrdinalIgnoreCase));
        if (script == null || !ProjectDiagnosticsTemplate.IsStockReport(script.Source)) return;

        string report = BuildProjectDiagnostics(project, scripts, token);
        token.ThrowIfCancellationRequested();
        using var lua = new Lua();
        lua.State.Encoding = Encoding.UTF8;
        var lines = lua.DoString("local lines = {}\nlocal function print(line) lines[#lines+1] = line end\n" +
            "local function project_diagnostics() return " + report + " end\n" +
            ProjectDiagnosticsTemplate.Source + "\nreturn table.concat(lines, '\\n')", "KNE startup diagnostics");
        if (lines.SingleOrDefault() is string text)
            foreach (string line in text.Split('\n')) output(line);
    }

    private static string BuildProjectDiagnostics(ProjectInfo project, GameScripts? scripts, CancellationToken token)
    {
        string savesDirectory = StandalonePlayer.RuntimeManifest?.SavesDirectory(AppContext.BaseDirectory) ??
            project.Engine.Performance.ResolveSavesPath(project.Path);
        var selection = GameStartupDiagnostics.SelectStorage(project, savesDirectory);
        var saves = selection.Effective;
        string persistentDirectory = GameSystemServices.PersistentDirectories(project.Path, savesDirectory)
            .For(project.Engine.Performance.PersistentLocation);
        var persistent = PathSafety.NormalizeDirectory(persistentDirectory).Equals(PathSafety.NormalizeDirectory(savesDirectory), StringComparison.OrdinalIgnoreCase)
            ? saves : GameStartupDiagnostics.CheckStorage(persistentDirectory);
        string installationDirectory = StandalonePlayer.RuntimeManifest == null ? project.Path : AppContext.BaseDirectory;
        return StateLiteral(JsonSerializer.SerializeToElement(new
        {
            version = 2,
            project = project.Title,
            entry = scripts?.Main.Path,
            files = scripts?.Files.Count ?? 0,
            warnings = project.Warnings.Take(25).ToArray(),
            environment = GameStartupDiagnostics.RuntimeEnvironment(),
            process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os_architecture = RuntimeInformation.OSArchitecture.ToString(),
            architecture_translation_possible = RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture,
            installation_directory = Path.TrimEndingDirectorySeparator(installationDirectory),
            integrity = CheckProjectIntegrity(project, scripts, token),
            saves = Storage(saves),
            requested_saves = Storage(selection.Requested),
            persistent = Storage(persistent)
        }));

        static object Storage(GameStorageStatus value) => new
        {
            directory = value.Directory, writable = value.Writable, code = value.Code,
            message = value.Message, available_bytes = value.AvailableBytes
        };
    }

    private static object CheckProjectIntegrity(ProjectInfo project, GameScripts? scripts, CancellationToken token)
    {
        var issues = new HashSet<string>(StringComparer.Ordinal);
        var assets = new HashSet<(string Path, bool Audio)>();
        string context = "Настройки проекта";
        ProjectSystemScan.CheckConfiguredAssets(project, CheckAsset);
        int checkedScripts = 0;
        using var compiler = new Lua();
        compiler.State.Encoding = Encoding.UTF8;
        foreach (var script in scripts?.Files ?? [])
        {
            token.ThrowIfCancellationRequested();
            context = script.Path;
            try
            {
                string source = script.ExecutableSource;
                checkedScripts++;
                using var chunk = compiler.LoadString(source, "@" + script.Path);
                if (script.Graph == null)
                    ProjectSystemScan.CheckLiteralAssets(source, CheckAsset);
                else
                    foreach (var block in script.Graph.Nodes)
                    {
                        if (block.Kind is ScriptNodeKind.Background or ScriptNodeKind.Character or ScriptNodeKind.ShaderContainer)
                            CheckAsset(block.Path, false);
                        else if (block.Kind is ScriptNodeKind.Music or ScriptNodeKind.Sound or ScriptNodeKind.Voice)
                            CheckAsset(block.Path, true);
                        else if (block.Kind == ScriptNodeKind.ShowVideo && SystemBlocks.LiteralVideoPath(block) is { } path)
                            CheckAsset(path, false);
                    }
            }
            catch (Exception error) when (ProjectSystemScan.Reportable(error) || error is NLua.Exceptions.LuaException)
            { issues.Add(context + ": " + error.Message); }
        }
        return new
        {
            scripts = checkedScripts, assets = assets.Count, issue_count = issues.Count,
            issues = issues.Take(50).Select(issue => issue.Length <= 500 ? issue : issue[..500] + "…").ToArray()
        };

        void CheckAsset(string path, bool audio)
        {
            if (string.IsNullOrWhiteSpace(path) || !assets.Add((path, audio))) return;
            try
            {
                using var stream = File.OpenRead(ResourcePaths.Resolve(project.Path, path, audio: audio));
                if (stream.Length == 0) issues.Add(context + " · " + path + ": ресурс пуст.");
            }
            catch (Exception error) when (ProjectSystemScan.Reportable(error))
            { issues.Add(context + " · " + path + ": " + error.Message); }
        }
    }
}
