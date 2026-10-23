using System.Globalization;
using System.IO;
using System.Text;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private PluginHost? pluginHost;

    private void InitializePlugins()
    {
        DisposePlugins();
        pluginHost = new(project.Path, false, this);
        lua!["kne_plugins_enabled"] = pluginsEnabled;
        Register(nameof(ReadPluginForLua), "kne_plugin_source");
        Register(nameof(ExecuteCSharpPlugin), "kne_plugin_csharp");
        Register(nameof(MarkPluginFailure), "kne_mark_plugin_failure");
        lua!.DoString("""
            do
                if not kne_plugins_enabled then
                    local function disabled() error("Внешние Lua-модули отключены в режиме без плагинов", 2) end
                    require, dofile, loadfile = disabled, disabled, disabled
                    package.loadlib = disabled
                    package.cpath = ""
                end
                local loaded, loading = {}, {}
                local function execute(path, kind, once)
                    if not kne_plugins_enabled then
                        print_to_ui("Плагин отключён в безопасном режиме: " .. tostring(path))
                        return
                    end
                    for level = 2, 32 do
                        local info = debug.getinfo(level, "Sl")
                        if not info then break end
                        if info.source:sub(1, 1) == "@" then kne_error_context(nil, info); break end
                    end
                    local result = kne_plugin_source(path, kind)
                    if result[2] ~= "" then kne_mark_plugin_failure(); error(result[2], 0) end
                    local source, canonical = result[0], result[1]
                    local key = canonical:lower()
                    if once and loaded[key] then return end
                    if loading[key] then kne_mark_plugin_failure(); error("Циклическое подключение плагина: " .. canonical, 0) end
                    loading[key] = true
                    local function run()
                        if canonical:lower():sub(-3) == ".cs" then
                            local problem = kne_plugin_csharp(canonical, source)
                            if problem ~= "" then error(problem, 0) end
                        else
                            local chunk, problem = load(source, "@" .. canonical, "t", _G)
                            if not chunk then
                                kne_record_script_error(canonical, tonumber(problem:match(":(%d+):")) or 0, problem, problem)
                                error(problem, 0)
                            end
                            chunk()
                        end
                    end
                    local ok, problem = xpcall(run, function(problem) return kne_capture_error(nil, problem) end)
                    loading[key] = nil
                    if not ok then kne_mark_plugin_failure(); error(problem, 0) end
                    if once then loaded[key] = true end
                end
                function include_lua(path) execute(path, "lua", true); kne_editor_pause() end
                function include_cs(path) execute(path, "cs", true); kne_editor_pause() end
                function run_script(path) execute(path, "script", false); kne_editor_pause() end
            end
            """, "KNE plugins");
    }

    /// <summary>Marks a plugin error before the Lua coroutine converts it into script error data.</summary>
    public void MarkPluginFailure() => pluginOperationFailed = true;

    private void DisposePlugins()
    {
        pluginHost?.Dispose();
        pluginHost = null;
    }

    /// <summary>Reads bounded project-owned plugin source; errors cross the Lua coroutine boundary as data.</summary>
    /// <param name="relative">Project-relative source path.</param>
    /// <param name="kind">lua, cs, or script.</param>
    /// <returns>Source, canonical relative path, and error text.</returns>
    public string[] ReadPluginForLua(string relative, string kind)
    {
        try
        {
            if (!pluginsEnabled) return ["", "", "Сторонние плагины отключены в этом запуске движка."];
            PluginPrivileges.EnsureUserProcess();
            EngineDiagnostics.Trace("Чтение плагина", relative);
            string full = PathSafety.ResolveRelative(project.Path, relative);
            string extension = Path.GetExtension(full).ToLowerInvariant();
            bool allowed = kind switch { "lua" => extension == ".lua", "cs" => extension == ".cs", "script" => extension is ".lua" or ".krl" or ".cs", _ => false };
            if (!allowed) throw new ArgumentException("Поддерживаются .lua, .krl и C#-плагины .cs; расширение должно соответствовать блоку.");
            using var stream = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > 8 * 1024 * 1024) throw new InvalidDataException("Плагин больше 8 МБ.");
            using var reader = new StreamReader(stream, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
            string source = reader.ReadToEnd();
            return [extension == ".krl" ? LuaSource.Prepare(source) : source, Path.GetRelativePath(project.Path, full).Replace('\\', '/'), ""];
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or UnauthorizedAccessException or DecoderFallbackException)
        { return ["", "", error.Message]; }
    }

    /// <summary>Compiles and invokes one C# plugin inside the existing serialized game operation.</summary>
    /// <param name="relative">Canonical project-relative C# path.</param>
    /// <param name="source">Source just read by the plugin bridge.</param>
    /// <returns>An empty string on success or a script-safe failure message.</returns>
    public string ExecuteCSharpPlugin(string relative, string source)
    {
        if (!pluginsEnabled) return "Сторонние плагины отключены в этом запуске движка.";
        EngineDiagnostics.Trace("Выполнение C#-плагина", relative);
        using var context = new PluginContext(this);
        try
        {
            PluginPrivileges.EnsureUserProcess();
            if (pluginHost == null) throw new InvalidOperationException("Плагины ещё не инициализированы.");
            pluginHost.Execute(relative, source, context);
            return "";
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            pluginOperationFailed = true;
            PluginSourceException? sourceError = null;
            for (Exception? cause = error; cause != null; cause = cause.InnerException)
                if (cause is PluginSourceException found) { sourceError = found; break; }
            RecordScriptError(sourceError?.File ?? relative, sourceError?.Line ?? 0, error.ToString(), error.Message);
            pluginFailureCaptured = true;
            return error.Message;
        }
    }

    private sealed class PluginContext(LuaRuntime owner) : IPluginContext, IDisposable
    {
        public PluginApi Api { get { Guard(); return owner.pluginHost!.Api; } }
        public System.Text.Json.JsonElement GetJson(string name)
        {
            Guard(); ValidateName(name);
            using var state = owner.lua!["state"] as LuaTable ?? throw new InvalidOperationException("state должен быть таблицей.");
            object? value = state[name];
            if (value is LuaTable table) { using (table) return System.Text.Json.JsonSerializer.SerializeToElement(ReadState(table, 0, allowArray: true)); }
            return System.Text.Json.JsonSerializer.SerializeToElement(Scalar(value));
        }
        public void SetJson(string name, System.Text.Json.JsonElement value)
        {
            Guard(); ValidateName(name);
            if (value.GetRawText().Length > 1024 * 1024) throw new ArgumentException("Структура плагина больше 1 МБ.");
            owner.lua!.DoString("state[" + LuaQuote(name) + "]=" + StateLiteral(value), "Plugin state");
        }
        public int InvokeLua(string function, string parametersJson)
        {
            Guard(); if (!LuaSource.IsIdentifier(function)) throw new ArgumentException("Требуется имя Lua-функции.");
            using var callback = owner.lua![function] as LuaFunction ?? throw new InvalidOperationException("Не найдена Lua-функция: " + function);
            return Convert.ToInt32(callback.Call(parametersJson).FirstOrDefault(), CultureInfo.InvariantCulture);
        }
        private readonly int thread = Environment.CurrentManagedThreadId;
        private bool active = true;
        public string ProjectDirectory { get { Guard(); return owner.project.Path; } }
        public CancellationToken CancellationToken { get { Guard(); return owner.executionToken; } }
        public object? Get(string name)
        {
            Guard();
            ValidateName(name);
            using var state = owner.lua!["state"] as LuaTable ?? throw new InvalidOperationException("state должен быть таблицей.");
            object? value = state[name];
            if (value is LuaTable table) { table.Dispose(); throw new ArgumentException("C#-плагинам доступны только скалярные значения state."); }
            if (value is LuaFunction function) { function.Dispose(); throw new ArgumentException("C#-плагинам недоступны Lua-функции."); }
            return Scalar(value);
        }
        public void Set(string name, object? value)
        {
            Guard();
            ValidateName(name);
            value = Scalar(value);
            using var state = owner.lua!["state"] as LuaTable ?? throw new InvalidOperationException("state должен быть таблицей.");
            state[name] = value;
        }
        public void Log(string message)
        {
            Guard();
            ArgumentNullException.ThrowIfNull(message);
            if (message.Length > 10000) throw new ArgumentException("Сообщение плагина длиннее 10000 символов.");
            owner.PrintToUI(message);
        }
        private static void ValidateName(string name)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            if (name.Length > 256 || name.Any(char.IsControl)) throw new ArgumentException("Недопустимое имя переменной state.");
        }
        private static object? Scalar(object? value)
        {
            if (value is null or bool or string) return value;
            if (value is byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsFinite(number)) return number;
            }
            throw new ArgumentException("Значение плагина: строка, bool, конечное число или null.");
        }
        private void Guard()
        {
            ObjectDisposedException.ThrowIf(!active, this);
            if (Environment.CurrentManagedThreadId != thread) throw new InvalidOperationException("Контекст плагина доступен только в потоке Execute().");
            owner.executionToken.ThrowIfCancellationRequested();
            if (owner.ShouldStop()) throw new TimeoutException("Превышено время выполнения плагина.");
        }
        public void Dispose() => active = false;
    }
}
