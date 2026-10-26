using System.Globalization;
using System.Text.Json;
using NLua;

namespace KNE;

public sealed partial class LuaRuntime
{
    private void InitializeChoices()
    {
        Register(nameof(PrepareChoiceForLua), "kne_prepare_choice");
        lua!.DoString("""
            function kne_choice(json)
                if not kne_interactive or not kne_scene_owned(coroutine.running()) then error("kne_choice() доступен только внутри интерактивной игровой сцены", 2) end
                local result = kne_prepare_choice(json)
                if result[0] ~= "" then error(result[0], 2) end
                local labels = {}
                for index = 1, tonumber(result[5]) do labels[index] = result[index + 5] end
                local duration = tonumber(result[2])
                return coroutine.yield(kne_event_location({kind = "choice", text = result[6 + #labels], options = labels,
                    choice_states = result[1], seconds = duration >= 0 and duration or nil,
                    default_option = tonumber(result[3]), timer_display = result[4]}))
            end
            """, "KNE choices");
    }

    /// <summary>Resolves lock predicates as state data and freezes them for a single choice presentation.</summary>
    public string[] PrepareChoiceForLua(string json)
    {
        try
        {
            if (json == null || json.Length > 1_000_000) throw new ArgumentException("Конфигурация выбора слишком длинная.");
            var request = JsonSerializer.Deserialize<ChoiceRequest>(json, NarrativeValidation.JsonOptions) ?? throw new ArgumentException("Выбор не может быть null.");
            request.Validate();
            using var state = lua!["state"] as LuaTable ?? throw new ArgumentException("state должен оставаться таблицей.");
            string Text(string text) => InlineText.ToMarkup(ResolveInline(text));
            var options = request.Options.Select(option => new ChoiceOptionState(Text(option.Text),
                option.Lock == null || !IsLocked(option.Lock, state), option.Lock?.Effect ?? "dim",
                option.Lock is { ShowHint: true } locked ? Text(locked.Hint) : "", option.Lock?.HintPlacement ?? "side")).ToArray();
            int firstEnabled = Array.FindIndex(options, option => option.IsEnabled);
            if (firstEnabled < 0) throw new ArgumentException("Все варианты выбора заблокированы. Оставьте хотя бы один доступный ответ.");
            int fallback = request.Timer?.DefaultOption ?? 1;
            if (!options[fallback - 1].IsEnabled) fallback = firstEnabled + 1;
            return new[] { "", JsonSerializer.Serialize(options, EngineSettings.JsonOptions),
                (request.Timer?.Seconds ?? -1).ToString("R", CultureInfo.InvariantCulture), fallback.ToString(CultureInfo.InvariantCulture),
                request.Timer?.TimerDisplay ?? "", options.Length.ToString(CultureInfo.InvariantCulture) }
                .Concat(options.Select(option => option.Text)).Append(Text(request.Prompt)).ToArray();
        }
        catch (Exception error) when (error is ArgumentException or JsonException or System.IO.InvalidDataException)
        { return ["kne_choice: " + error.Message]; }
    }

    private static bool IsLocked(OptionLockSettings settings, LuaTable state)
    {
        object? left = ReadPrimitive(state[settings.Variable]);
        object? right = settings.CompareVariable.Length > 0 ? ReadPrimitive(state[settings.CompareVariable]) : settings.ValueType switch
        {
            "boolean" => bool.Parse(settings.Value),
            "number" => double.Parse(settings.Value, CultureInfo.InvariantCulture),
            _ => settings.Value
        };
        bool equal = Equals(left, right);
        if (settings.Operator == ScriptComparison.Equal) return equal;
        if (settings.Operator == ScriptComparison.NotEqual) return !equal;
        if (left is not double a || right is not double b) throw new ArgumentException("Сравнения больше/меньше в блокировке требуют две числовые переменные.");
        return settings.Operator switch
        {
            ScriptComparison.Greater => a > b,
            ScriptComparison.Less => a < b,
            ScriptComparison.GreaterOrEqual => a >= b,
            ScriptComparison.LessOrEqual => a <= b,
            _ => false
        };
    }

    private static object? ReadPrimitive(object? value)
    {
        if (value is LuaTable table) { table.Dispose(); throw new ArgumentException("Условие блокировки требует строку, число или флаг, а не таблицу."); }
        if (value is long or int or float or double) return Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (value is null or string or bool) return value;
        throw new ArgumentException("Неподдерживаемый тип переменной в блокировке.");
    }

    private static StoryFrame ReadChoiceFrame(StoryFrame frame, LuaTable value)
    {
        if (frame.Kind != "choice") return frame;
        IReadOnlyList<ChoiceOptionState>? options = value["choice_states"] is string json ?
            JsonSerializer.Deserialize<ChoiceOptionState[]>(json, NarrativeValidation.JsonOptions) : null;
        ChoiceTimerState? timer = value["seconds"] is { } seconds ?
            new(Convert.ToDouble(seconds), Convert.ToDouble(value["remaining"] ?? seconds), value["timer_display"] as string ?? "bar") : null;
        return frame with { ChoiceOptions = options, ChoiceTimer = timer };
    }

    private static bool IsChoiceEnabled(StoryFrame frame, int option) =>
        frame.ChoiceOptions == null || option <= frame.ChoiceOptions.Count && frame.ChoiceOptions[option - 1].IsEnabled;
}
