using System.IO;

namespace KNE;

public sealed partial class LuaRuntime
{
    /// <summary>Reads a bounded UTF-8 file under the configured project policy.</summary>
    public string ReadPolicyFile(string path)
    {
        string full = PolicyPath(path); if (new FileInfo(full).Length > 8 * 1024 * 1024) throw new InvalidDataException("Файл больше 8 МБ."); return File.ReadAllText(full);
    }
    /// <summary>Writes a bounded UTF-8 file without following links outside the permitted directory.</summary>
    public void WritePolicyFile(string path, string text)
    {
        if (text.Length > 4 * 1024 * 1024) throw new InvalidDataException("Текст слишком велик."); string full = PolicyPath(path); Directory.CreateDirectory(Path.GetDirectoryName(full)!); File.WriteAllText(full, text);
    }
    private string PolicyPath(string path)
    {
        return project.Engine.Security.ResolveFile(project.Path, path);
    }
    private void ApplyExecutionPolicy()
    {
        LuaExecutionPolicy.Register(lua!, this, GetType().GetMethod(nameof(ReadPolicyFile))!, "kne_read_file");
        LuaExecutionPolicy.Register(lua!, this, GetType().GetMethod(nameof(WritePolicyFile))!, "kne_write_file");
        lua!.DoString("kne = kne or {}; kne.read_file = kne_read_file; kne.write_file = kne_write_file", "KNE file API");
        LuaExecutionPolicy.Apply(lua, project.Engine.Security);
        if (ScriptProtection.HasEmbeddedKey && !project.Engine.Security.Restricted) InstallEncryptedModules();
    }
    /// <summary>Supplies encrypted project Lua modules while leaving standard module lookup intact in full-access mode.</summary>
    public string[] PlayerModuleSource(string path, bool module)
    {
        if (module) path = path.Replace('.', '/') + ".lua";
        foreach (string relative in module ? new[] { path, Path.GetRelativePath(project.Path, Path.Combine(Path.GetDirectoryName(project.InitScriptPath)!, path)) } : new[] { path })
        {
            string full;
            try { full = PathSafety.ResolveRelative(project.Path, relative); } catch (ArgumentException) { continue; }
            if (File.Exists(full)) return [ScriptProtection.ReadText(full), full.Replace('\\', '/')];
        }
        return ["", ""];
    }
    private void InstallEncryptedModules()
    {
        Register(nameof(PlayerModuleSource), "kne_player_module");
        lua!.DoString("""
            do
                local originalRequire, originalLoadfile = require, loadfile
                function loadfile(path, mode, environment)
                    local value = kne_player_module(path, false)
                    if value[1] == '' then return originalLoadfile(path, mode, environment) end
                    return load(value[0], '@' .. value[1], 't', environment or _G)
                end
                function dofile(path) return assert(loadfile(path))() end
                function require(name)
                    if package.loaded[name] ~= nil then return package.loaded[name] end
                    local value = kne_player_module(name, true)
                    if value[1] == '' then return originalRequire(name) end
                    package.loaded[name] = true
                    local ok, result = pcall(assert(load(value[0], '@' .. value[1], 't', _G)), name)
                    if not ok then package.loaded[name] = nil; error(result, 2) end
                    if result ~= nil then package.loaded[name] = result end
                    return package.loaded[name]
                end
            end
            """, "KNE protected project modules");
    }
}
