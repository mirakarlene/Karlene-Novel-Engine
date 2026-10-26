using System.Globalization;
using System.IO;
using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private void InitializeInlineText()
    {
        Register(nameof(PrepareInlineForLua), "kne_prepare_inline");
        Register(nameof(PrepareInlineDialogueForLua), "kne_prepare_inline_dialogue");
        Register(nameof(InlinePlainForLua), "kne_inline_plain");
        lua!.DoString("""
            kne_inline_known = {}
            for key in pairs(state) do kne_inline_known[key] = true end
            setmetatable(state, {__newindex = function(values, key, value)
                kne_inline_known[key] = true; rawset(values, key, value)
            end})
            function kne_inline_text(text)
                local result = kne_prepare_inline(tostring(text))
                if result[1] ~= '' then error(result[1], 2) end
                return result[0]
            end
            """, "KNE inline text");
        // Lua removes nil keys. Remember declarations from the JSON state before any script runs.
        foreach (var property in project.Engine.InitialState.EnumerateObject())
            lua.DoString("kne_inline_known[" + LuaQuote(property.Name) + "] = true", "KNE inline declarations");
    }

    public string[] PrepareInlineForLua(string source)
    {
        try { return [InlineText.ToMarkup(ResolveInline(source)), ""]; }
        catch (InvalidDataException error) { return ["", "Инлайн-текст: " + error.Message]; }
    }

    /// <summary>Freezes values once per authored line, returning cumulative fragments and logical pauses.</summary>
    public string[] PrepareInlineDialogueForLua(string source)
    {
        try
        {
            var document = ResolveInline(source);
            var result = new List<string> { "" };
            var pauses = document.Pauses.Where(pause => pause.Seconds != 0).ToArray();
            foreach (var pause in pauses)
            {
                result.Add(InlineText.ToMarkup(document, pause.Offset));
                result.Add((pause.Seconds ?? -1).ToString("R", CultureInfo.InvariantCulture));
            }
            if (pauses.Length == 0 || pauses[^1].Offset != document.Text.Length)
            {
                result.Add(InlineText.ToMarkup(document)); result.Add("final");
            }
            result.Insert(1, ((result.Count - 1) / 2).ToString(CultureInfo.InvariantCulture));
            return result.ToArray();
        }
        catch (InvalidDataException error) { return ["Инлайн-текст: " + error.Message]; }
    }

    public string InlinePlainForLua(string markup) => InlineText.Parse(markup).Text;

    private InlineTextDocument ResolveInline(string source)
    {
        using var state = lua!["state"] as LuaTable;
        using var known = lua["kne_inline_known"] as LuaTable;
        return InlineText.ParseValues(source, name =>
        {
            if (state == null) return new("{error}", "#FF0000");
            object? value = state[name];
            if (value == null) return new(known?[name] is true ? "{null}" : "{missing}", "#808080");
            if (value is string text) return text.Length == 0 ? new("{null}", "#808080") : new(text);
            if (value is bool flag) return new(flag ? "true" : "false");
            if (value is long or int or double or float)
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                return double.IsFinite(number) ? new(Convert.ToString(value, CultureInfo.InvariantCulture)!) : new("{error}", "#FF0000");
            }
            if (value is LuaTable table)
            {
                using (table)
                    try { int budget = 10000; return new(JsonSerializer.Serialize(ReadInlineState(table, 0, ref budget))); }
                    catch (Exception error) when (error is InvalidDataException or ArgumentException) { return new("{error}", "#FF0000"); }
            }
            return new("{error}", "#FF0000");
        });
    }

    private static object ReadInlineState(LuaTable table, int depth, ref int budget)
    {
        if (depth > 16) throw new InvalidDataException("Слишком глубокая таблица или цикл.");
        var map = new Dictionary<string, object?>(); var array = new SortedDictionary<int, object?>();
        foreach (KeyValuePair<object, object> entry in table)
        {
            if (--budget < 0) throw new InvalidDataException("Таблица слишком большая.");
            object? value;
            if (entry.Value is LuaTable nested) { using (nested) value = ReadInlineState(nested, depth + 1, ref budget); }
            else if (entry.Value is double number && !double.IsFinite(number)) throw new InvalidDataException("Неконечное число.");
            else if (entry.Value is null or string or bool or long or int or double) value = entry.Value;
            else throw new InvalidDataException("Неподдерживаемое значение.");
            if (entry.Key is string key) map[key] = value;
            else if (entry.Key is double or long or int)
            {
                double index = Convert.ToDouble(entry.Key, CultureInfo.InvariantCulture);
                if (!double.IsFinite(index) || index != Math.Truncate(index) || index is < 1 or > 10000) throw new InvalidDataException("Некорректный индекс массива.");
                array[(int)index] = value;
            }
            else throw new InvalidDataException("Некорректный ключ.");
        }
        if (array.Count == 0) return map;
        if (map.Count > 0 || array.Keys.Last() != array.Count) throw new InvalidDataException("Смешанная таблица или пропуски в массиве.");
        return array.Values.ToArray();
    }
}
