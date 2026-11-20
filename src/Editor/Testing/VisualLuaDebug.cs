using System.IO;
using System.Text.Json;

namespace KNE;

internal sealed record VisualLuaDebugReport(string Status, IReadOnlyList<string> Output);

internal static class VisualLuaDebug
{
    internal static async Task<VisualLuaDebugReport> RunAsync(string projectRoot, string relative, string source, CancellationToken token = default)
    {
        string temp = PathSafety.NormalizeDirectory(Path.GetTempPath());
        string root = PathSafety.ResolveRelative(temp, "kne-vlua-debug-" + Guid.NewGuid().ToString("N"));
        var output = new Queue<string>();
        Directory.CreateDirectory(root);
        try
        {
            var project = await ProjectLoader.LoadProjectAsync(projectRoot, token);
            await GameBuildExporter.CopyProjectAsync(project, root, token);
            string securityPath = PathSafety.ResolveRelative(root, "engine/security.json");
            var security = ProjectSecuritySettings.Load(root);
            security.AllowPlugins = security.AllowNetwork = security.CrashReports = false;
            security.FileSystem = "project";
            await File.WriteAllTextAsync(securityPath, JsonSerializer.Serialize(security, EngineSettings.JsonOptions), token);
            var performance = PerformanceSettingsStore.LoadProject(root);
            performance.PersistentLocation = "local";
            performance.UnavailableSaves = "read_only";
            await PerformanceSettingsStore.SaveProjectAsync(root, performance, token);

            var copy = await ProjectLoader.LoadProjectAsync(root, token);
            var draft = new EditorPreviewSource(relative, source, StepCommands: false, LuaDebug: true);
            await using var session = new ProcessGameSession(copy, line =>
                {
                    if (output.Count == 500) output.Dequeue();
                    output.Enqueue(line);
                }, executionLimit: TimeSpan.FromSeconds(20), editorSource: draft, pluginsEnabled: false);
            await session.StartAsync(token);
            return new(session.Story.Kind is "empty" or "completed" or "end" ? "Скрипт выполнен."
                : "Скрипт дошёл до ожидания игрового ввода или таймера (" + session.Story.Kind + ").", output.ToArray());
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            return new("Ошибка отладки: " + error.Message, output.ToArray());
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(PathSafety.EnsureInside(temp, root), recursive: true); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { EngineDiagnostics.Trace("Не удалена временная копия Visual Lua", error.Message); }
        }
    }
}
