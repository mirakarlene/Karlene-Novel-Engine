using System.IO;

namespace KNE;

public sealed partial class LuaRuntime
{
    private readonly Dictionary<string, (string Target, string Screen)> shaderTargets = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Validates a lazily invoked shader and returns its host plugin commands to the current Lua coroutine.</summary>
    /// <param name="relative">Project-relative shader graph path.</param>
    /// <returns>The setup source and an empty error string, or an empty source and a diagnostic.</returns>
    public string[] ShaderSourceForLua(string relative)
    {
        try
        {
            string path = ShaderPath(relative);
            // Recompile on activation: nested files may have changed as well as the root.
            var graph = VisualScript.Parse(File.ReadAllText(path));
            var compiled = ShaderGraphCompiler.Compile(graph, project.Path, path);
            return [compiled.ActivationScript, "", compiled.Target, compiled.Screen];
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException)
        { return ["", relative + ": " + error.Message]; }
    }

    private string ShaderPath(string relative)
    {
        string path = PathSafety.ResolveRelative(project.Path, relative);
        if (!ScriptDocument.IsShaderPath(path)) throw new ArgumentException("Контейнер требует файл .kneshader.");
        if (!pluginsEnabled && EngineLaunch.IsPluginPath(relative)) throw new ArgumentException("Шейдеры из plugins отключены безопасным режимом.");
        if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidDataException("Шейдер больше 2 МБ.");
        return path;
    }

    private void SetShader(string relative, bool enabled, string? target, string? screen, string flow)
    {
        string path = PathSafety.ResolveRelative(project.Path, relative);
        if (!ScriptDocument.IsShaderPath(path)) throw new ArgumentException("Контейнер требует файл .kneshader.");
        string canonical = Path.GetRelativePath(project.Path, path).Replace('\\', '/');
        lock (stateLock)
        {
            var other = visuals.Shaders.Where(item => !string.Equals(item, canonical, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (enabled && other.Length >= 16) throw new ArgumentException("Одновременно допускается не более 16 шейдеров.");
            if (enabled)
            {
                if (string.IsNullOrEmpty(target))
                {
                    var compiled = ShaderGraphCompiler.Compile(VisualScript.Parse(File.ReadAllText(ShaderPath(relative))), project.Path, path);
                    target = compiled.Target; screen = compiled.Screen;
                }
                shaderTargets[canonical] = (target, screen ?? "");
            }
            else shaderTargets.Remove(canonical);
            visuals = visuals with { Shaders = enabled ? [.. other, canonical] : other, ShaderSuppressions = visuals.ShaderSuppressions.Where(item => item != flow).ToArray() };
        }
    }

    private void RemoveShaders(string? target, string screen, bool suppress, string flow)
    {
        if (target != null && target is not ("scene" or "background" or "sprite" or "ui" or "screen")) throw new ArgumentException("Неизвестный слой шейдеров.");
        if (screen.Length > 128 || target == "screen" && string.IsNullOrWhiteSpace(screen)) throw new ArgumentException("Укажите название экрана длиной до 128 символов.");
        lock (stateLock)
        {
            var removed = visuals.Shaders.Where(path => target == null || shaderTargets.TryGetValue(path, out var layer) && layer.Target == target && (target != "screen" || string.Equals(layer.Screen.Trim(), screen.Trim(), StringComparison.OrdinalIgnoreCase))).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (string path in removed) shaderTargets.Remove(path);
            visuals = visuals with
            {
                Shaders = visuals.Shaders.Where(path => !removed.Contains(path)).ToArray(),
                ShaderSuppressions = suppress && !visuals.ShaderSuppressions.Contains(flow) ? [.. visuals.ShaderSuppressions, flow] : visuals.ShaderSuppressions
            };
        }
    }
}
