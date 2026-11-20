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
        if (editorSource?.LuaDebug == true) InstallDebugModules();
        else if (!project.Engine.Security.Restricted) InstallProjectModules();
    }
    /// <summary>Supplies project Lua modules with compiled or protected source in full-access mode.</summary>
    public string[] PlayerModuleSource(string path, bool module)
    {
        if (module) path = path.Replace('.', '/') + ".lua";
        string besideInitializer = Path.GetRelativePath(project.Path, Path.Combine(Path.GetDirectoryName(project.InitScriptPath)!, path));
        bool encrypted = ScriptProtection.HasEmbeddedKey;
        string[] candidates = !module ? [path] : encrypted ? [path, besideInitializer] : [besideInitializer, path];
        foreach (string relative in candidates)
        {
            string full;
            try { full = PathSafety.EnsureInside(project.Path, VisualLua.ResolveTwin(Path.GetFullPath(relative, project.Path))); }
            catch (ArgumentException) { continue; }
            if (!File.Exists(full)) continue;
            if (encrypted || ScriptDocument.IsVisualLuaPath(full) ||
                ScriptDocument.IsLuaPath(relative) && !Path.GetExtension(relative).Equals(Path.GetExtension(full), StringComparison.OrdinalIgnoreCase))
                return [VisualLua.ReadProjectSource(full), full.Replace('\\', '/')];
            break;
        }
        return ["", ""];
    }
    private void InstallProjectModules()
    {
        Register(nameof(PlayerModuleSource), "kne_player_module");
        lua!.DoString("""
            do
                local originalLoadfile = loadfile
                function loadfile(path, mode, environment)
                    if type(path) ~= 'string' or mode == 'b' then return originalLoadfile(path, mode, environment) end
                    local value = kne_player_module(path, false)
                    if value[1] == '' then return originalLoadfile(path, mode, environment) end
                    return load(value[0], '@' .. value[1], 't', environment or _G)
                end
                function dofile(path) return assert(loadfile(path))() end
                table.insert(package.searchers, 2, function(name)
                    local value = kne_player_module(name, true)
                    if value[1] == '' then return nil end
                    return assert(load(value[0], '@' .. value[1], 't', _G)), value[1]
                end)
            end
            """, "KNE protected project modules");
    }

    /// <summary>Resolves a debug dependency inside the temporary project, including Visual Lua twins.</summary>
    public string[] DebugModuleSource(string path, bool module)
    {
        string relative = module ? path.Replace('.', '/') + ".lua" : path;
        if (!ScriptDocument.IsLuaPath(relative)) throw new InvalidDataException("Зависимость должна иметь формат .lua или .vlua.");
        string? scriptsDirectory = module ? Path.GetDirectoryName(project.InitScriptPath) : null;
        var candidates = scriptsDirectory == null ? new[] { relative } : [Path.Combine(scriptsDirectory, relative), relative];
        foreach (string candidate in candidates)
        {
            string full = project.Engine.Security.ResolveFile(project.Path, candidate);
            full = PathSafety.EnsureInside(project.Path, VisualLua.ResolveTwin(full));
            if (!File.Exists(full)) continue;
            string name = Path.GetRelativePath(project.Path, full).Replace('\\', '/');
            string text = editorSource?.RelativePath(project.Path).Equals(name, StringComparison.OrdinalIgnoreCase) == true
                ? editorSource.Source : VisualLua.ReadProjectSource(full);
            return [LuaSource.Prepare(StartupScripts.Declaration(text).Source), name];
        }
        throw new FileNotFoundException("Модуль не найден во временной копии: " + path);
    }

    private void InstallDebugModules()
    {
        Register(nameof(DebugModuleSource), "kne_debug_module_source");
        lua!.DoString("""
            do
                local read, compile = kne_debug_module_source, load
                local loaded = {}
                function loadfile(path, mode, environment)
                    if mode == 'b' then return nil, 'Разрешены только текстовые скрипты Lua' end
                    local ok, result = pcall(read, path, false)
                    if not ok then return nil, result end
                    return compile(result[0], '@' .. result[1], 't', environment or _G)
                end
                function dofile(path) return assert(loadfile(path))() end
                function require(name)
                    if loaded[name] then return loaded[name] end
                    local result = read(name, true)
                    local value = assert(compile(result[0], '@' .. result[1], 't', _G))(name, result[1])
                    if value == nil then value = true end
                    loaded[name] = value
                    return value, result[1]
                end
            end
            """, "KNE isolated Lua modules");
    }
}
