using System.Globalization;
using System.IO;
using System.Text;

namespace KNE;

/// <summary>Literal text with explicit color ranges and pauses measured in displayed text offsets.</summary>
public sealed record InlineTextDocument(string Text, IReadOnlyList<InlineTextRun> Runs, IReadOnlyList<InlineTextPause> Pauses);
public sealed record InlineTextRun(int Start, int Length, string Color);
public sealed record InlineTextPause(int Offset, double? Seconds);
public sealed record InlineTextValue(string Text, string? Color = null);

/// <summary>Parses narrative markup without executing expressions or interpreting substituted values as markup.</summary>
public static class InlineText
{
    private static readonly IReadOnlyDictionary<string, string> Colors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = "#FF0000", ["blue"] = "#0000FF", ["green"] = "#008000", ["yellow"] = "#FFFF00",
        ["white"] = "#FFFFFF", ["black"] = "#000000", ["gray"] = "#808080", ["orange"] = "#FFA500",
        ["purple"] = "#800080", ["pink"] = "#FFC0CB", ["cyan"] = "#00FFFF", ["magenta"] = "#FF00FF"
    };

    public static InlineTextDocument Parse(string source, Func<string, string>? resolveVariable = null)
        => ParseValues(source, resolveVariable == null ? null : name => new(resolveVariable(name)));

    public static InlineTextDocument ParseValues(string source, Func<string, InlineTextValue>? resolveVariable)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = new StringBuilder(source.Length);
        var runs = new List<InlineTextRun>();
        var pauses = new List<InlineTextPause>();
        var colors = new Stack<string>();
        int copied = 0;
        for (int position = 0; position < source.Length; position++)
        {
            if (position + 1 < source.Length && source[position] is '{' or '}' && source[position + 1] == source[position])
            {
                Append(source[copied..position]); Append(source[position].ToString());
                copied = position + 2; position++; continue;
            }
            if (source[position] != '{') continue;
            int end = source.IndexOf('}', position + 1);
            if (end < 0) break;
            int nested = source.AsSpan(position + 1, end - position - 1).LastIndexOf('{');
            if (nested >= 0) { position += nested; continue; }
            string tag = source[(position + 1)..end];
            bool color = tag.StartsWith("color=", StringComparison.Ordinal);
            bool pause = tag == "w" || tag.StartsWith("w=", StringComparison.Ordinal);
            bool variable = resolveVariable != null && LuaSource.IsIdentifier(tag) && tag != "w";
            if (!color && !pause && tag != "/color" && !variable) continue;
            Append(source[copied..position]);
            if (color)
            {
                if (colors.Count >= 128) throw new InvalidDataException("Слишком много вложенных тегов {color}.");
                colors.Push(ParseColor(tag[6..]));
            }
            else if (tag == "/color")
            {
                if (colors.Count == 0) throw new InvalidDataException("Тег {/color} не имеет открывающего {color=...}.");
                colors.Pop();
            }
            else if (pause)
            {
                double? seconds = null;
                if (tag != "w")
                {
                    if (!double.TryParse(tag.AsSpan(2), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double duration) ||
                        !double.IsFinite(duration) || duration is < 0 or > 3600)
                        throw new InvalidDataException("Пауза {w=...}: от 0 до 3600 секунд, десятичный разделитель — точка.");
                    seconds = duration;
                }
                pauses.Add(new(text.Length, seconds));
            }
            else
            {
                var value = resolveVariable!(tag); Append(value.Text, value.Color);
            }
            copied = end + 1; position = end;
        }
        Append(source[copied..]);
        if (colors.Count != 0) throw new InvalidDataException("Тег {color=...} не закрыт тегом {/color}.");
        return new(text.ToString(), runs.ToArray(), pauses.ToArray());

        void Append(string value, string? explicitColor = null)
        {
            int start = text.Length; text.Append(value);
            if (value.Length == 0 || colors.Count == 0 && explicitColor == null) return;
            string color = explicitColor ?? colors.Peek();
            if (runs.Count != 0 && runs[^1] is var previous && previous.Start + previous.Length == start && previous.Color == color)
                runs[^1] = previous with { Length = previous.Length + value.Length };
            else runs.Add(new(start, value.Length, color));
        }
    }

    /// <summary>Re-encodes a resolved prefix with balanced colors and escaped literal braces, without pauses.</summary>
    public static string ToMarkup(InlineTextDocument document, int? length = null)
    {
        int end = Math.Clamp(length ?? document.Text.Length, 0, document.Text.Length), copied = 0;
        var result = new StringBuilder();
        foreach (var run in document.Runs)
        {
            if (run.Start >= end) break;
            result.Append(Escape(document.Text[copied..run.Start]));
            copied = Math.Min(end, run.Start + run.Length);
            result.Append("{color=").Append(run.Color).Append('}').Append(Escape(document.Text[run.Start..copied])).Append("{/color}");
        }
        return result.Append(Escape(document.Text[copied..end])).ToString();
    }

    private static string Escape(string value) => value.Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);

    private static string ParseColor(string value)
    {
        if (Colors.TryGetValue(value, out string? named)) return named;
        if (value.Length == 7 && value[0] == '#' && value[1..].All(Uri.IsHexDigit)) return value.ToUpperInvariant();
        if (value.StartsWith("rgb(", StringComparison.OrdinalIgnoreCase) && value.EndsWith(')'))
        {
            string[] channels = value[4..^1].Split(',');
            if (channels.Length == 3 && byte.TryParse(channels[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte r) &&
                byte.TryParse(channels[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte g) &&
                byte.TryParse(channels[2].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out byte b)) return $"#{r:X2}{g:X2}{b:X2}";
        }
        throw new InvalidDataException("Цвет {color=...}: ключевое слово, #RRGGBB или rgb(0–255,0–255,0–255). Получено: " + value);
    }
}
