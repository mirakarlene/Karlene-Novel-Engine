using System.IO;
using System.Text;
using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    internal Task StartPreMenuAsync(CancellationToken token = default) =>
        RunAsync(() => StartCore(preMenu: true), token, "Сцена до главного меню");

    internal async Task<string?> RunProjectEventAsync(ProjectLifecycleEvent invocation, CancellationToken token = default)
    {
        string? result = null;
        if (string.IsNullOrWhiteSpace(ProjectLifecycle.Script(project.Configuration.EntryPoints, invocation.Kind))) return null;
        await RunAsync(() =>
        {
            if (invocation.Kind == "error") result = RunErrorHook(invocation.Value ?? "");
            else
            {
                bool started = initialized;
                if (!initialized) StartCore(bootstrapOnly: true);
                RecordProjectEvent(invocation, record: started);
            }
        }, token, "Событие проекта · " + invocation.Kind).ConfigureAwait(false);
        return result;
    }

    private void RecordProjectEvent(ProjectLifecycleEvent invocation, bool record = true)
    {
        string before = FrameSignature(Story);
        InvokeProjectEvent(invocation);
        if (record && !replaying) history.Add(new StoryAction("project_event", before, Command: JsonSerializer.Serialize(invocation)));
    }

    private void InvokeProjectEvent(ProjectLifecycleEvent invocation)
    {
        if (lua == null) return;
        InstallHook();
        using var function = (LuaFunction)lua["kne_project_event"];
        function.Call(invocation.Kind, invocation.Value);
    }

    private void InitializeProjectLifecycle()
    {
        Register(nameof(ReadProjectEventForLua), "kne_event_source");
        lua!.DoString("""
            do
                local read, compile = kne_event_source, load
                function kne_project_event(kind, value)
                    local result = read(kind)
                    if result[1] == '' then return end
                    local prior = project_event
                    project_event = { kind = kind, slot = kind == 'save' and value or nil }
                    local ok, problem = xpcall(function()
                        local chunk = assert(compile(result[0], '@' .. result[1], 't', _G))
                        chunk(project_event)
                    end, function(problem) return kne_capture_error(nil, problem) end)
                    project_event = prior
                    if not ok then error(problem, 0) end
                end
            end
            """, "KNE project events");
    }

    /// <summary>Reads the configured project event without searching external directories.</summary>
    public string[] ReadProjectEventForLua(string kind)
    {
        string? path = ProjectLifecycle.Resolve(project, ProjectLifecycle.Script(project.Configuration.EntryPoints, kind));
        if (path == null) return ["", ""];
        if (!ScriptDocument.IsCodePath(path) && !ScriptDocument.IsVisualLuaPath(path))
            throw new InvalidDataException("Событие проекта требует .lua, .vlua или .krl.");
        string relative = Path.GetRelativePath(project.Path, path).Replace('\\', '/');
        return [LuaSource.Prepare(StartupScripts.Declaration(VisualLua.ReadProjectSource(path)).Source), relative];
    }

    private string RunErrorHook(string message)
    {
        var source = ReadProjectEventForLua("error");
        using var errorLua = new Lua();
        errorLua.State.Encoding = Encoding.UTF8;
        using var check = errorLua.RegisterFunction("kne_event_timeout", this, GetType().GetMethod(nameof(ShouldStop))!);
        errorLua["kne_error_script"] = source[0];
        errorLua["kne_error_file"] = source[1];
        errorLua["kne_error_message"] = message;
        var result = errorLua.DoString("""
            debug.sethook(function() if kne_event_timeout() then error('Время обработки ошибки истекло', 0) end end, '', 10000)
            local event = { kind = 'error', message = kne_error_message }
            local env = { project_event = event, error_message = kne_error_message,
                assert=assert, error=error, ipairs=ipairs, pairs=pairs, tonumber=tonumber, tostring=tostring, type=type,
                string=string, table=table, math=math, utf8=utf8 }
            return assert(load(kne_error_script, '@' .. kne_error_file, 't', env))(event)
            """, "KNE error event");
        return result.FirstOrDefault() is string text && !string.IsNullOrWhiteSpace(text) && text.Length <= 4096 ? text :
            throw new InvalidDataException("Скрипт ошибки должен вернуть текст длиной до 4096 символов.");
    }
}
