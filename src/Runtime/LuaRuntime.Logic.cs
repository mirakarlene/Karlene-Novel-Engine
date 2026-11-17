using System.Text;
using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private void InitializeLogic()
    {
        Register(nameof(LogicTextForLua), "kne_logic_text"); Register(nameof(LogicInitialForLua), "kne_logic_initial");
        lua!.DoString(LogicRuntimeLua.Source, "KNE lists and text");
    }
    /// <summary>Unicode-aware text operations returning errors as data across the Lua boundary.</summary>
    public object[] LogicTextForLua(string operation, string a, string b, string c)
    {
        try
        {
            object result = operation switch
            {
                "length" => (double)a.EnumerateRunes().Count(), "contains" => a.Contains(b, StringComparison.Ordinal),
                "replace" => b.Length == 0 ? throw new ArgumentException("Заменяемая строка не должна быть пустой.") : a.Replace(b, c, StringComparison.Ordinal),
                "upper" => a.ToUpperInvariant(), "lower" => a.ToLowerInvariant(), _ => throw new ArgumentException("Неизвестная строковая операция.")
            };
            return [result, ""];
        }
        catch (ArgumentException error) { return ["", error.Message]; }
    }
    /// <summary>Resets from the active scene's initial state, then the project's initial state.</summary>
    public string LogicInitialForLua(string name)
    {
        if (sceneFrames.TryGetValue(executingScene, out var frame) && frame.Scene.Settings.InitialState.TryGetProperty(name, out var sceneValue)) return StateLiteral(sceneValue);
        return project.Engine.InitialState.TryGetProperty(name, out var projectValue) ? StateLiteral(projectValue) : "nil";
    }
    private JsonElement CaptureLists()
    {
        var result = new Dictionary<string, object>();
        using var all = (LuaTable)lua!.DoString("return kne_list_export()", "KNE list capture")[0];
        foreach (KeyValuePair<object, object> pair in all)
        {
            using var list = (LuaTable)pair.Value;
            object value = ReadState(list, 0, allowArray: true);
            result.Add((string)pair.Key, value is Dictionary<string, object?> empty && empty.Count == 0 ? Array.Empty<object>() : value);
        }
        return JsonSerializer.SerializeToElement(result);
    }
}
