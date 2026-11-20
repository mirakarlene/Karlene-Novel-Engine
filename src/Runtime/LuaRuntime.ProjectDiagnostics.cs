using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

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
    public string ReadProjectDiagnosticsForLua()
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
            version = 1,
            project = project.Title,
            entry = scripts?.Main.Path,
            files = scripts?.Files.Count ?? 0,
            warnings = project.Warnings.Take(25).ToArray(),
            environment = GameStartupDiagnostics.RuntimeEnvironment(),
            process_architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            os_architecture = RuntimeInformation.OSArchitecture.ToString(),
            architecture_translation_possible = RuntimeInformation.ProcessArchitecture != RuntimeInformation.OSArchitecture,
            installation_directory = Path.TrimEndingDirectorySeparator(installationDirectory),
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
}
