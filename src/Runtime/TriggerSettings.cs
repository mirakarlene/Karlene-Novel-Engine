using System.Windows.Input;

namespace KNE;

/// <summary>An event delivered by the game UI and recorded in replay history.</summary>
public sealed record TriggerSignal(string Kind, string Keys = "", string Button = "", double X = 0, double Y = 0)
{
    public void Validate()
    {
        if (Kind is not ("key" or "button" or "hotspot" or "text_closed")) throw new ArgumentException("Неизвестное событие триггера.");
        if (Kind == "key") _ = TriggerSettings.NormalizeKeys(Keys);
        if (Kind == "button" && (string.IsNullOrWhiteSpace(Button) || Button.Length > 256)) throw new ArgumentException("Укажите ID игровой кнопки.");
        if (!double.IsFinite(X) || !double.IsFinite(Y)) throw new ArgumentException("Координаты события должны быть конечными.");
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

    public void Validate()
    {
        new TriggerSignal(Kind, Keys, Button, X, Y).Validate();
        if (new[] { X, Y, Width, Height }.Any(value => !double.IsFinite(value)) || X < 0 || Y < 0 || Width <= 0 || Height <= 0 || X + Width > 100000 || Y + Height > 100000)
            throw new ArgumentException("Зона триггера: положительные размеры и координаты от 0 до 100000.");
    }

    public bool Matches(TriggerSignal signal) => Kind == signal.Kind && Kind switch
    {
        "key" => NormalizeKeys(Keys) == NormalizeKeys(signal.Keys),
        "button" => Button.Equals(signal.Button, StringComparison.OrdinalIgnoreCase),
        "hotspot" => signal.X >= X && signal.Y >= Y && signal.X < X + Width && signal.Y < Y + Height,
        "text_closed" => true,
        _ => false
    };

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
