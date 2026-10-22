using System.IO;

namespace KNE;

public sealed partial class LuaRuntime
{
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
            return [compiled.ActivationScript, ""];
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

    private void SetShader(string relative, bool enabled)
    {
        string path = PathSafety.ResolveRelative(project.Path, relative);
        if (!ScriptDocument.IsShaderPath(path)) throw new ArgumentException("Контейнер требует файл .kneshader.");
        string canonical = Path.GetRelativePath(project.Path, path).Replace('\\', '/');
        lock (stateLock)
        {
            var other = visuals.Shaders.Where(item => !string.Equals(item, canonical, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (enabled && other.Length >= 16) throw new ArgumentException("Одновременно допускается не более 16 шейдеров.");
            visuals = visuals with { Shaders = enabled ? [.. other, canonical] : other };
        }
    }
}
