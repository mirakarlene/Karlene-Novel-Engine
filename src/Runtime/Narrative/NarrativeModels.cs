using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Media;

namespace KNE;

/// <summary>A named, reusable dialogue presentation, independent of sprites.</summary>
public sealed record CharacterProfile
{
    public string Id { get; init; } = "character";
    public string Name { get; init; } = "Персонаж";
    public string NameColor { get; init; } = "#FFF5EEFF";
    public string TextAlignment { get; init; } = "left";
    public string FontFamily { get; init; } = "Segoe UI";
    public double FontSize { get; init; } = 24;

    public void Validate()
    {
        if (!LuaSource.IsIdentifier(Id) || Id.Length > 128) throw new ArgumentException("ID персонажа: латиница, цифры и подчёркивания, без пробелов; начало — буква или подчёркивание.");
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 512 || Name.Any(char.IsControl)) throw new ArgumentException("Нужно отображаемое имя персонажа длиной до 512 символов.");
        NarrativeValidation.Color(NameColor);
        if (TextAlignment is not ("left" or "center" or "right" or "justify")) throw new ArgumentException("Позиция текста: left, center, right или justify.");
        if (string.IsNullOrWhiteSpace(FontFamily) || FontFamily.Length > 256 || FontFamily.Any(char.IsControl) || FontFamily.Contains("://") || FontFamily.IndexOfAny(['#', '/', '\\']) >= 0)
            throw new ArgumentException("Нужно локальное семейство шрифта.");
        if (!double.IsFinite(FontSize) || FontSize < 1 || FontSize > 512) throw new ArgumentException("Размер шрифта: от 1 до 512.");
    }
}

/// <summary>The per-letter effect associated with one dialogue frame.</summary>
public sealed record TextEffectSettings
{
    public string Kind { get; init; } = "shake";
    public double Speed { get; init; } = 1;
    public bool Loop { get; init; } = true;
    public string Color1 { get; init; } = "#FFFF4040";
    public string Color2 { get; init; } = "#FF000000";
    public static IReadOnlyList<string> Kinds { get; } = Array.AsReadOnly(new[] { "shake", "wave", "sway", "bounce", "float", "explode", "pulse", "spin", "squash", "glitch", "blur", "dissolve", "scramble", "rainbow", "color_cycle", "drip" });

    public void Validate()
    {
        if (!Kinds.Contains(Kind)) throw new ArgumentException("Неизвестный эффект текста: " + Kind);
        if (!double.IsFinite(Speed) || Speed <= 0 || Speed > 100) throw new ArgumentException("Скорость эффекта: больше 0 и не больше 100.");
        NarrativeValidation.Color(Color1); NarrativeValidation.Color(Color2);
    }
}

internal static class NarrativeValidation
{
    internal static readonly JsonSerializerOptions JsonOptions = new(EngineSettings.JsonOptions)
    { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    internal static void Color(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 40) throw new ArgumentException("Нужен цвет текста.");
        try { _ = (Color)ColorConverter.ConvertFromString(value); }
        catch (Exception error) when (error is FormatException or NotSupportedException or ArgumentException)
        { throw new ArgumentException("Некорректный цвет текста: " + value, error); }
    }
}
