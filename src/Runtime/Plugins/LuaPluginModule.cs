using System.Text;
using System.Text.Json;
using NLua;

namespace KNE;

/// <summary>The JSON registration surface shared by Lua manifests and ordinary include_lua modules.</summary>
internal sealed class LuaPluginModule(PluginApi api, Lua lua)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    internal void Install()
    {
        void Bind(string method, string name) => lua.RegisterFunction(name, this, GetType().GetMethod(method)!);
        Bind(nameof(Register), "kne_sdk_register"); Bind(nameof(Event), "kne_sdk_event"); Bind(nameof(TextHook), "kne_sdk_text_hook");
        Bind(nameof(Http), "kne_sdk_http"); Bind(nameof(Download), "kne_sdk_download"); Bind(nameof(Speak), "kne_sdk_speak");
        lua["kne_sdk_is_editor"] = api.IsEditor; lua["kne_sdk_is_player"] = api.IsPlayer;
        lua.DoString("""
            kne = kne or {}
            kne.is_editor, kne.is_player = kne_sdk_is_editor, kne_sdk_is_player
            local function check(problem) if problem ~= "" then error(problem, 3) end end
            function kne.register_group(json) check(kne_sdk_register("group", json, "")) end
            function kne.register_block(json, callback) check(kne_sdk_register("block", json, callback)) end
            function kne.register_palette(json) check(kne_sdk_register("palette", json, "")) end
            function kne.on(event, callback) check(kne_sdk_event(event, callback)) end
            function kne.before_text(callback) check(kne_sdk_text_hook(callback)) end
            function kne.http_get(url)
                local result = kne_sdk_http(url); check(result[1]); return result[0]
            end
            function kne.download(url, extension)
                local result = kne_sdk_download(url, extension); check(result[1]); return result[0]
            end
            function kne.speak(text) check(kne_sdk_speak(text)) end
            """, "KNE Lua Plugin API");
    }
    public string Register(string kind, string json, string callback) => Protect(() =>
    {
        if (json.Length > 65536) throw new ArgumentException("Описание расширения больше 64 КБ.");
        switch (kind)
        {
            case "group": api.RegisterGroup(JsonSerializer.Deserialize<BlockGroup>(json, Json)!); break;
            case "block": api.RegisterLuaBlock(JsonSerializer.Deserialize<PluginBlock>(json, Json)!, callback); break;
            case "palette": api.RegisterPalette(JsonSerializer.Deserialize<PluginPalette>(json, Json)!); break;
            default: throw new ArgumentException("Неизвестная регистрация: " + kind);
        }
    });
    public string Event(string name, string callback) => Protect(() =>
    {
        CheckFunction(callback);
        api.On(name, value => { using var function = lua.GetFunction(callback); function.Call(value.Data.GetRawText()); });
    });
    public string TextHook(string callback) => Protect(() =>
    {
        CheckFunction(callback);
        api.BeforeExecute("Dialogue", value =>
        {
            using var function = lua.GetFunction(callback); var result = function.Call(value.Text, value.Speaker);
            if (result.Length > 0 && result[0] != null) value.Text = result[0] as string ?? throw new ArgumentException("Hook текста должен вернуть строку.");
            if (result.Length > 1 && result[1] != null) value.Speaker = result[1] as string ?? throw new ArgumentException("Hook имени должен вернуть строку.");
        });
    });
    public string[] Http(string url) => Result(() =>
    {
        using var request = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, url);
        return Encoding.UTF8.GetString(api.Assets.SendAsync(request).GetAwaiter().GetResult());
    });
    public string[] Download(string url, string extension) => Result(() => api.Assets.DownloadAsync(new Uri(url), extension).GetAwaiter().GetResult());
    public string Speak(string text) => Protect(() => api.Media.Speak(text));
    private void CheckFunction(string callback)
    {
        if (!LuaSource.IsIdentifier(callback)) throw new ArgumentException("Укажите имя Lua-функции обратного вызова.");
        using var function = lua.GetFunction(callback) ?? throw new ArgumentException("Не найдена Lua-функция: " + callback);
    }
    private static string Protect(Action action) { try { action(); return ""; } catch (Exception error) when (error is not OperationCanceledException) { return "Plugin API: " + error.Message; } }
    private static string[] Result(Func<string> action) { try { return [action(), ""]; } catch (Exception error) when (error is not OperationCanceledException) { return ["", "Plugin API: " + error.Message]; } }
}
