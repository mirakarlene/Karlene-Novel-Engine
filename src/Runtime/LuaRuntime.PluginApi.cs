using System.Text.Json;
using System.Diagnostics;
using System.IO;

namespace KNE;

public sealed partial class LuaRuntime
{
    public bool IsReplaying => replaying;
    public IReadOnlyList<string> PluginScreens => pluginHost?.Api.ActiveScreens ?? [];
    public Task NotifyPluginInputAsync(PluginInput input, CancellationToken cancellationToken = default) => RunAsync(() =>
    {
        if (!initialized || !pluginsEnabled) return;
        string before = FrameSignature(Story); PublishPluginInputEvent(input);
        if (!replaying) history.Add(new("plugin_event_input", before, Command: JsonSerializer.Serialize(input)));
    }, cancellationToken, "Событие ввода плагина");
    private void PublishPluginInputEvent(PluginInput input) => PublishPluginEvent(input.Kind == "key" ? "on_key_press" : "on_mouse_click", new { input.Kind, input.Key, input.X, input.Y });
    public async Task<bool> InterceptInputAsync(PluginInput input, CancellationToken cancellationToken = default)
    {
        bool handled = false;
        await RunAsync(() =>
        {
            if (!initialized || !pluginsEnabled) return;
            string before = FrameSignature(Story);
            PublishPluginInputEvent(input);
            handled = pluginHost!.Api.Input(input);
            if (!replaying) history.Add(new("plugin_input", before, Command: JsonSerializer.Serialize(input)));
        }, cancellationToken, "Ввод плагина").ConfigureAwait(false);
        return handled;
    }
    private void InitializePluginApi()
    {
        if (pluginsEnabled) new LuaPluginModule(pluginHost!.Api, lua!).Install();
        Register(nameof(PluginBlockForLua), "kne_plugin_block_bridge");
        Register(nameof(PluginDialogueForLua), "kne_plugin_dialogue");
        Register(nameof(PluginNodeForLua), "kne_plugin_node_bridge");
        lua!.DoString("""
            function kne_execute_plugin_block(id, parameters)
                for level = 2, 32 do
                    local info = debug.getinfo(level, "Sl")
                    if not info then break end
                    if info.source:sub(1, 1) == "@" then kne_error_context(nil, info); break end
                end
                local result = kne_plugin_block_bridge(id, parameters)
                if result[1] ~= "" then error(result[1], 2) end
                return tonumber(result[0])
            end
            function kne_plugin_node(kind, id)
                if not kne_sdk_node_hooks then return end
                local info = debug.getinfo(2, "Sl")
                if info and info.source:sub(1, 1) == "@" then kne_error_context(nil, info) end
                local problem = kne_plugin_node_bridge(kind, id)
                if problem ~= "" then error(problem, 2) end
            end
            """, "KNE Plugin API");
        pluginHost!.Api.BindingsChanged = () =>
        {
            lua["kne_sdk_node_hooks"] = pluginHost.Api.HasNodeHooks;
            lua["kne_sdk_dialogue_hooks"] = pluginHost.Api.HasDialogueListeners;
        };
        pluginHost.Api.BindingsChanged();
        if (pluginsEnabled) pluginHost!.Load(() => new PluginContext(this), executionToken);
        if (pluginHost!.Api.GameTexture is { } texture) lua.DoString("background(" + LuaQuote(texture) + ")", "KNE plugin texture");
        Register(nameof(PluginSceneForLua), "kne_plugin_scene");
    }
    internal void LoadLuaPluginManifest(string relative, string source) => lua!.DoString(source, "@" + relative);

    public string[] PluginBlockForLua(string id, string json)
    {
        try
        {
            if (!pluginsEnabled) throw new InvalidOperationException("Плагины отключены: " + id);
            using var context = new PluginContext(this);
            int output = pluginHost!.Api.Execute(id, context, json);
            var definition = pluginHost.Api.Blocks.Single(b => b.Id == id);
            if (output < 1 || output > (definition.Outputs?.Length ?? 1))
            {
                if (output == 0 && definition.Outputs is { Length: 0 }) return ["0", ""];
                throw new InvalidOperationException("Блок плагина вернул недопустимый номер выхода: " + output);
            }
            return [output.ToString(System.Globalization.CultureInfo.InvariantCulture), ""];
        }
        catch (Exception error) when (error is not OperationCanceledException) { CapturePluginError(error); return ["0", "Ошибка блока плагина «" + id + "»: " + error.Message]; }
    }
    public string PluginNodeForLua(string kind, string id)
    {
        try
        {
            if (pluginsEnabled && kind is not ("Dialogue" or "AutoDialogue" or "EffectDialogue"))
            {
                using var context = new PluginContext(this); pluginHost!.Api.Hook(new(context, kind, id));
            }
            return "";
        }
        catch (Exception error) when (error is not OperationCanceledException) { CapturePluginError(error); return "Ошибка hook плагина: " + error.Message; }
    }
    public string[] PluginDialogueForLua(string text, string speaker)
    {
        try
        {
            if (!pluginsEnabled) return [text, speaker, ""];
            using var context = new PluginContext(this);
            var execution = new PluginExecution(context, "Dialogue", currentScriptFrame?.BlockId ?? "", text, speaker);
            pluginHost!.Api.Hook(execution);
            if (execution.Text == null || execution.Speaker == null || execution.Text.Length > 16000) throw new ArgumentException("Hook вернул некорректную реплику.");
            PublishPluginEvent("on_text_displayed", new { Text = execution.Text, Speaker = execution.Speaker });
            return [execution.Text, execution.Speaker, ""];
        }
        catch (Exception error) when (error is not OperationCanceledException) { CapturePluginError(error); return ["", "", "Ошибка hook реплики: " + error.Message]; }
    }
    public string PluginSceneForLua(string scene)
    {
        try { PublishPluginEvent("on_scene_start", new { Scene = scene }); return ""; }
        catch (Exception error) when (error is not OperationCanceledException) { return "Ошибка события сцены плагина: " + error.Message; }
    }
    private void PublishPluginEvent(string name, object? data = null)
    {
        if (!pluginsEnabled || pluginHost == null || replaying && name is "on_save" or "on_load") return;
        using var context = new PluginContext(this);
        try { pluginHost.Api.Publish(name, JsonSerializer.SerializeToElement(data), context); }
        catch (Exception error) { CapturePluginError(error); throw; }
    }
    private void CapturePluginError(Exception error)
    {
        for (Exception? cause = error; cause != null; cause = cause.InnerException)
            foreach (var frame in new StackTrace(cause, true).GetFrames() ?? [])
            {
                string? source = frame.GetFileName();
                if (source == null || Path.IsPathRooted(source) || frame.GetFileLineNumber() <= 0) continue;
                try
                {
                    string full = PathSafety.ResolveRelative(project.Path, source);
                    if (!File.Exists(full)) continue;
                    RecordScriptError(source, frame.GetFileLineNumber(), error.ToString(), error.Message); pluginFailureCaptured = true; return;
                }
                catch (Exception invalid) when (invalid is IOException or ArgumentException or UnauthorizedAccessException) { }
            }
    }
}
