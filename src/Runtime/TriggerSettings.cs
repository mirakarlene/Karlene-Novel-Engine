using System.Text.Json.Serialization;
using System.Windows.Input;

namespace KNE;

/// <summary>An event delivered by the game UI and recorded in replay history.</summary>
public sealed record TriggerSignal(string Kind, string Keys = "", string Button = "", double X = 0, double Y = 0,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreviousX = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? PreviousY = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] double? Elapsed = null)
{
    public void Validate()
    {
        if (!TriggerBlocks.All.Values.Any(block => block.Event == Kind)) throw new ArgumentException("Неизвестное событие триггера.");
        if (Kind == "key") _ = TriggerSettings.NormalizeKeys(Keys);
        if (Kind == "sequence") _ = TriggerSettings.NormalizeSequence(Keys);
        if (Kind == "button" && (string.IsNullOrWhiteSpace(Button) || Button.Length > 256)) throw new ArgumentException("Укажите ID игровой кнопки.");
        if (!double.IsFinite(X) || !double.IsFinite(Y) || PreviousX is double x && !double.IsFinite(x) || PreviousY is double y && !double.IsFinite(y))
            throw new ArgumentException("Координаты события должны быть конечными.");
        if (Elapsed is double time && (!double.IsFinite(time) || time < 0)) throw new ArgumentException("Укажите корректное время события.");
        if (Kind == "double_click" && (Button is not ("left" or "right" or "middle") || Elapsed == null)) throw new ArgumentException("Для двойного щелчка нужны кнопка и интервал.");
        if (Kind == "long_press")
        {
            if (Elapsed == null || Button is not ("" or "left" or "right" or "middle"))
                throw new ArgumentException("Для удержания нужны длительность и клавиша или кнопка.");
            if (Button == "") _ = TriggerSettings.NormalizeSingleKey(Keys);
        }
        if (Kind == "wheel" && Button is not ("up" or "down") ||
            Kind == "focus" && Button is not ("gained" or "lost") ||
            Kind == "resize" && Button is not ("changing" or "finished") ||
            Kind == "clipboard" && Button is not ("copy" or "paste" or "cut"))
            throw new ArgumentException("Неизвестный вариант события триггера.");
        if (Kind == "gesture" && (Button is not ("left" or "right" or "middle") || !TriggerSettings.IsGesture(Keys)))
            throw new ArgumentException("Укажите поддерживаемый жест мыши и кнопку.");
    }
}

/// <summary>A scene-scoped input subscription, independent of the linear scenario.</summary>
public sealed record TriggerSettings
{
    public string Kind { get; init; } = "key";
    public string Keys { get; init; } = "E";
    public string Button { get; init; } = "next";
    public double X { get; init; }
    public double Y { get; init; }
    public double Width { get; init; } = 100;
    public double Height { get; init; } = 100;
    public bool Once { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Mode { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? MouseButton { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? HoldSeconds { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public double? DoubleClickMilliseconds { get; init; }

    internal double EffectiveHoldSeconds => HoldSeconds ?? 5;
    internal double EffectiveDoubleClickMilliseconds => DoubleClickMilliseconds ?? 300;
    internal string EffectiveMouseButton => MouseButton ?? "left";
    internal string EffectiveMode => Mode ?? (Kind switch { "resize" => "finished", "clipboard" => "copy", "gesture" => "circle", _ => "both" });

    public void Validate()
    {
        if (!TriggerBlocks.All.Values.Any(block => block.Event == Kind)) throw new ArgumentException("Неизвестное событие триггера.");
        if (Kind == "key") _ = NormalizeKeys(Keys);
        if (Kind == "sequence") _ = NormalizeSequence(Keys);
        if (Kind == "button" && (string.IsNullOrWhiteSpace(Button) || Button.Length > 256))
            throw new ArgumentException("Укажите ID игровой кнопки.");
        if (new[] { X, Y, Width, Height }.Any(value => !double.IsFinite(value)) || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 100000 || Y + Height > 100000)
            throw new ArgumentException("Зона триггера: положительные размеры и координаты от 0 до 100000.");
        if (MouseButton is not (null or "left" or "right" or "middle")) throw new ArgumentException("Выберите кнопку мыши.");
        if (Kind == "long_press" && MouseButton == null) _ = NormalizeSingleKey(Keys);
        if (!double.IsFinite(EffectiveHoldSeconds) || EffectiveHoldSeconds <= 0 ||
            !double.IsFinite(EffectiveDoubleClickMilliseconds) || EffectiveDoubleClickMilliseconds <= 0)
            throw new ArgumentException("Длительность удержания и интервал двойного щелчка должны быть больше нуля.");
        bool valid = Kind switch
        {
            "wheel" => EffectiveMode is "up" or "down" or "both",
            "focus" => EffectiveMode is "gained" or "lost" or "both",
            "resize" => EffectiveMode is "changing" or "finished",
            "clipboard" => EffectiveMode is "copy" or "paste" or "cut",
            "gesture" => IsGesture(EffectiveMode),
            _ => true
        };
        if (!valid) throw new ArgumentException("Выберите вариант срабатывания триггера.");
    }

    public bool Matches(TriggerSignal signal) => Kind == signal.Kind && Kind switch
    {
        "key" => NormalizeKeys(Keys) == NormalizeKeys(signal.Keys),
        "button" => Button.Equals(signal.Button, StringComparison.OrdinalIgnoreCase),
        "hotspot" => Contains(signal.X, signal.Y),
        "sequence" => NormalizeSequence(Keys) == NormalizeSequence(signal.Keys),
        "pointer_enter_zone" => Contains(signal.X, signal.Y) &&
            (signal.PreviousX is not double px || signal.PreviousY is not double py || !Contains(px, py)),
        "pointer_move" or "pointer_leave" or "text_closed" => true,
        "double_click" => EffectiveMouseButton == signal.Button && signal.Elapsed is double milliseconds && milliseconds <= EffectiveDoubleClickMilliseconds,
        "long_press" => EffectiveHoldSeconds == signal.Elapsed && (MouseButton == null
            ? signal.Button == "" && NormalizeSingleKey(Keys) == NormalizeSingleKey(signal.Keys)
            : MouseButton == signal.Button),
        "wheel" or "focus" or "resize" or "clipboard" => EffectiveMode == "both" || EffectiveMode == signal.Button,
        "gesture" => EffectiveMouseButton == signal.Button && EffectiveMode == signal.Keys,
        _ => false
    };

    private bool Contains(double x, double y) => x >= X && y >= Y && x < X + Width && y < Y + Height;

    internal static bool IsGesture(string value) => value is "circle" or "up" or "down" or "left" or "right";

    internal static string NormalizeSingleKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Укажите клавишу.");
        string trimmed = key.Trim();
        string normalized = NormalizeKeys(trimmed switch
        {
            "↑" => "Up", "↓" => "Down", "←" => "Left", "→" => "Right", "Esc" => "Escape", _ => trimmed
        });
        if (normalized.Contains('+')) throw new ArgumentException("Укажите отдельную клавишу без сочетания.");
        return normalized;
    }

    internal static string NormalizeSequence(string sequence)
    {
        if (string.IsNullOrWhiteSpace(sequence) || sequence.Length > 1024)
            throw new ArgumentException("Укажите последовательность отдельных клавиш через запятую.");
        return string.Join(',', sequence.Split(',').Select(NormalizeSingleKey));
    }

    /// <summary>Canonicalizes a chord without treating modifier aliases or key order as different actions.</summary>
    public static string NormalizeKeys(string chord)
    {
        if (chord == null || chord.Length > 160) throw new ArgumentException("Укажите клавишу или сочетание клавиш.");
        var keys = new SortedSet<string>(StringComparer.Ordinal);
        foreach (string part in chord.Split('+', StringSplitOptions.TrimEntries))
        {
            string key = part.ToUpperInvariant() switch
            {
                "CTRL" or "CONTROL" or "LEFTCTRL" or "RIGHTCTRL" => "Ctrl",
                "ALT" or "LEFTALT" or "RIGHTALT" => "Alt",
                "SHIFT" or "LEFTSHIFT" or "RIGHTSHIFT" => "Shift",
                "WIN" or "LWIN" or "RWIN" => "Win",
                _ => Enum.TryParse<Key>(part, true, out var value) && Enum.IsDefined(value) && value is not (Key.None or Key.System or Key.DeadCharProcessed or Key.ImeProcessed)
                    ? value.ToString() : throw new ArgumentException("Неизвестная клавиша: " + part)
            };
            keys.Add(key);
        }
        if (keys.Count is < 1 or > 8) throw new ArgumentException("Сочетание должно содержать от 1 до 8 клавиш.");
        return string.Join('+', keys);
    }
}
