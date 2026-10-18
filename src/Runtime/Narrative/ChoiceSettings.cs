using System.Globalization;

namespace KNE;

/// <summary>Logical-clock countdown and deterministic fallback for a timed choice.</summary>
public sealed record ChoiceSettings
{
    /// <summary>Countdown duration in logical scene seconds, greater than zero and at most one hour.</summary>
    public double Seconds { get; init; } = 10;
    /// <summary>Original one-based option selected on expiry, falling back to the first enabled option.</summary>
    public int DefaultOption { get; init; } = 1;
    /// <summary>Countdown display: bar or numeric.</summary>
    public string TimerDisplay { get; init; } = "bar";

    /// <summary>Rejects invalid durations, displays, or references outside the original option list.</summary>
    /// <param name="optionCount">Number of original options, including disabled ones.</param>
    public void Validate(int optionCount = 20)
    {
        if (!double.IsFinite(Seconds) || Seconds <= 0 || Seconds > 3600) throw new ArgumentException("Таймер выбора: больше 0 и не больше 3600 секунд.");
        if (DefaultOption < 1 || DefaultOption > optionCount) throw new ArgumentException("Вариант по умолчанию должен указывать на существующий ответ.");
        if (TimerDisplay is not ("bar" or "numeric")) throw new ArgumentException("Вид таймера: bar или numeric.");
    }
}

/// <summary>A data-only predicate: a true comparison locks the option when it is presented.</summary>
public sealed record OptionLockSettings
{
    /// <summary>Flat state key on the left side of the predicate.</summary>
    public string Variable { get; init; } = "locked";
    /// <summary>Typed equality or numeric ordering operation.</summary>
    public ScriptComparison Operator { get; init; }
    /// <summary>Literal on the right side when no comparison variable is supplied.</summary>
    public string Value { get; init; } = "true";
    /// <summary>Literal type: boolean, number, or string.</summary>
    public string ValueType { get; init; } = "boolean";
    /// <summary>Optional right-side state key, replacing the literal.</summary>
    public string CompareVariable { get; init; } = "";
    /// <summary>Disabled-label presentation: dim, strike, glitch, hide, or scramble.</summary>
    public string Effect { get; init; } = "dim";
    /// <summary>Whether hovering a locked answer reveals its hint.</summary>
    public bool ShowHint { get; init; }
    /// <summary>Author-defined explanatory hover text.</summary>
    public string Hint { get; init; } = "";
    /// <summary>Hover presentation over the answer or beside it.</summary>
    public string HintPlacement { get; init; } = "side";

    /// <summary>Validates state identifiers, literal syntax, and bounded presentation data.</summary>
    public void Validate()
    {
        if (Variable == null || Variable.Length > 128 || !LuaSource.IsIdentifier(Variable) || CompareVariable == null ||
            CompareVariable.Length > 128 || CompareVariable.Length > 0 && !LuaSource.IsIdentifier(CompareVariable))
            throw new ArgumentException("Переменные блокировки: латинские буквы, цифры и _, без цифры в начале.");
        if (!Enum.IsDefined(Operator) || Value == null || Value.Length > 16000 || Hint == null || Hint.Length > 16000)
            throw new ArgumentException("Некорректное условие блокировки или слишком длинный текст.");
        if (ValueType is not ("boolean" or "number" or "string")) throw new ArgumentException("Тип блокировки: boolean, number или string.");
        if (CompareVariable.Length == 0)
        {
            if (ValueType == "boolean" && !bool.TryParse(Value, out _)) throw new ArgumentException("Логическое значение: true или false.");
            if (ValueType == "number" && (!double.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) || !double.IsFinite(number)))
                throw new ArgumentException("Значение блокировки должно быть конечным числом.");
        }
        if (Effect is not ("dim" or "strike" or "glitch" or "hide" or "scramble")) throw new ArgumentException("Неизвестная анимация блокировки.");
        if (HintPlacement is not ("overlay" or "side")) throw new ArgumentException("Положение подсказки: overlay или side.");
    }
}

/// <summary>Resolved original-index option state, frozen for this presentation.</summary>
public sealed record ChoiceOptionState(string Text, bool IsEnabled, string Effect = "dim", string Hint = "", string HintPlacement = "side");

/// <summary>Remaining authoritative scene time; the player UI only displays this state.</summary>
public sealed record ChoiceTimerState(double Seconds, double RemainingSeconds, string Display);

/// <summary>Configuration accepted by kne_choice; labels and predicates are never Lua source.</summary>
public sealed record ChoiceRequest
{
    /// <summary>Question shown above the options.</summary>
    public string Prompt { get; init; } = "";
    /// <summary>Original-order answers and optional predicates.</summary>
    public IReadOnlyList<ChoiceRequestOption> Options { get; init; } = [];
    /// <summary>Optional countdown; null waits for explicit player selection.</summary>
    public ChoiceSettings? Timer { get; init; }

    /// <summary>Validates all options and the optional countdown before resolving predicates.</summary>
    public void Validate()
    {
        if (Prompt == null || Prompt.Length > 16000 || Options == null || Options.Count is < 1 or > 20 ||
            Options.Any(o => o == null || string.IsNullOrWhiteSpace(o.Text) || o.Text.Length > 16000))
            throw new ArgumentException("Выбор требует от 1 до 20 непустых ответов и текст до 16000 символов.");
        Timer?.Validate(Options.Count);
        foreach (var option in Options) option.Lock?.Validate();
    }
}

/// <summary>A label and optional lock predicate for the extended choice API.</summary>
public sealed record ChoiceRequestOption(string Text, OptionLockSettings? Lock = null);
