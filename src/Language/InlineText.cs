using System.Globalization;
using System.IO;
using System.Text;

namespace KNE;

/// <summary>Literal text with style ranges and pauses measured in displayed text offsets.</summary>
public sealed record InlineTextDocument(string Text, IReadOnlyList<InlineTextRun> Runs, IReadOnlyList<InlineTextPause> Pauses);
public sealed record InlineTextRun(int Start, int Length, string? Color)
{
    public bool Bold { get; init; }
    public bool Italic { get; init; }
    public double? Size { get; init; }
    public string? Font { get; init; }
}
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

    public static InlineTextDocument ParseValues(string source, Func<string, InlineTextValue>? resolveVariable, Func<string, bool>? fontIsConnected = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var text = new StringBuilder(source.Length);
        var runs = new List<InlineTextRun>();
        var pauses = new List<InlineTextPause>();
        var styles = new Stack<(string Tag, InlineTextRun Style)>();
        var plain = new InlineTextRun(0, 0, null);
        var style = plain;
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
            int equals = tag.IndexOf('=');
            string name = equals < 0 ? tag : tag[..equals];
            bool opening = tag is "b" or "i" || equals >= 0 && name is "color" or "font" or "size";
            bool closing = tag is "/color" or "/b" or "/i" or "/font" or "/size";
            bool pause = tag == "w" || tag.StartsWith("w=", StringComparison.Ordinal);
            bool variable = resolveVariable != null && LuaSource.IsIdentifier(tag) && tag != "w";
            if (!opening && !closing && !pause && !variable) continue;
            Append(source[copied..position]);
            if (opening)
            {
                if (styles.Count >= 128) throw new InvalidDataException("Слишком много вложенных тегов форматирования.");
                styles.Push((name, style));
                string argument = tag[(equals + 1)..];
                style = name switch
                {
                    "color" => style with { Color = ParseColor(argument) },
                    "b" => style with { Bold = true },
                    "i" => style with { Italic = true },
                    "size" => style with { Size = ParseSize(argument) },
                    _ => style with { Font = ParseFont(argument, fontIsConnected) }
                };
            }
            else if (closing)
            {
                if (!styles.TryPop(out var previous) || previous.Tag != tag[1..])
                    throw new InvalidDataException($"Тег {{{tag}}} закрывает неверный тег форматирования.");
                style = previous.Style;
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
        if (styles.TryPeek(out var unclosed)) throw new InvalidDataException($"Тег {{{unclosed.Tag}}} не закрыт тегом {{/{unclosed.Tag}}}.");
        return new(text.ToString(), runs.ToArray(), pauses.ToArray());

        void Append(string value, string? explicitColor = null)
        {
            int start = text.Length; text.Append(value);
            var applied = explicitColor == null ? style : style with { Color = explicitColor };
            if (value.Length != 0 && applied != plain)
                AppendRun(runs, applied with { Start = start, Length = value.Length });
        }
    }

    /// <summary>Re-encodes resolved text with balanced tags and escaped literal braces, without pauses.</summary>
    public static string ToMarkup(InlineTextDocument document, int? length = null, int offset = 0)
    {
        int copied = Math.Clamp(offset, 0, document.Text.Length);
        int end = copied + Math.Clamp(length ?? document.Text.Length, 0, document.Text.Length - copied);
        var result = new StringBuilder();
        foreach (var run in document.Runs)
        {
            if (run.Start >= end) break;
            if (run.Start + run.Length <= copied) continue;
            int start = Math.Max(copied, run.Start);
            result.Append(Escape(document.Text[copied..start]));
            copied = Math.Min(end, run.Start + run.Length);
            if (run.Color != null) result.Append("{color=").Append(run.Color).Append('}');
            if (run.Bold) result.Append("{b}");
            if (run.Italic) result.Append("{i}");
            if (run.Size is double size) result.Append("{size=").Append(size.ToString(CultureInfo.InvariantCulture)).Append('}');
            if (run.Font != null) result.Append("{font=").Append(run.Font).Append('}');
            result.Append(Escape(document.Text[start..copied]));
            if (run.Font != null) result.Append("{/font}");
            if (run.Size != null) result.Append("{/size}");
            if (run.Italic) result.Append("{/i}");
            if (run.Bold) result.Append("{/b}");
            if (run.Color != null) result.Append("{/color}");
        }
        return result.Append(Escape(document.Text[copied..end])).ToString();
    }

    public static InlineTextDocument Reverse(InlineTextDocument document, bool reverseWords, bool reverseLetters)
    {
        if (!reverseWords && !reverseLetters || document.Text.Length == 0) return document;
        int[] offsets = [.. StringInfo.ParseCombiningCharacters(document.Text), document.Text.Length];
        var groups = new List<(int Start, int End, bool Space)>();
        for (int index = 0; index < offsets.Length - 1;)
        {
            int start = index++;
            bool space = char.IsWhiteSpace(document.Text[offsets[start]]);
            while (index < offsets.Length - 1 && char.IsWhiteSpace(document.Text[offsets[index]]) == space) index++;
            groups.Add((start, index, space));
        }
        var words = groups.Where(group => !group.Space).ToArray();
        int word = reverseWords ? words.Length - 1 : 0;
        int[] reordered = groups.SelectMany(group =>
        {
            if (group.Space) return Enumerable.Range(group.Start, group.End - group.Start);
            var next = words[word];
            word += reverseWords ? -1 : 1;
            var letters = Enumerable.Range(next.Start, next.End - next.Start);
            return reverseLetters ? letters.Reverse() : letters;
        }).ToArray();
        var text = new StringBuilder(document.Text.Length);
        var runs = new List<InlineTextRun>();
        var outputOffsets = new int[offsets.Length];
        for (int index = 0; index < reordered.Length; index++)
        {
            int start = offsets[reordered[index]], end = offsets[reordered[index] + 1];
            foreach (var run in document.Runs)
            {
                if (run.Start >= end) break;
                int intersection = Math.Max(start, run.Start);
                int length = Math.Min(end, run.Start + run.Length) - intersection;
                if (length > 0) AppendRun(runs, run with { Start = text.Length + intersection - start, Length = length });
            }
            text.Append(document.Text.AsSpan(start, end - start));
            outputOffsets[index + 1] = text.Length;
        }
        return new(text.ToString(), runs, document.Pauses.Select(pause =>
        {
            int index = Array.BinarySearch(offsets, pause.Offset);
            return pause with { Offset = outputOffsets[index < 0 ? ~index : index] };
        }).ToArray());
    }

    private static void AppendRun(List<InlineTextRun> runs, InlineTextRun run)
    {
        if (runs.Count > 0 && runs[^1] is var previous && previous.Start + previous.Length == run.Start &&
            previous == (run with { Start = previous.Start, Length = previous.Length }))
            runs[^1] = previous with { Length = previous.Length + run.Length };
        else runs.Add(run);
    }

    private static double ParseSize(string value)
    {
        if (double.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double size) && size is >= 1 and <= 256)
            return size;
        throw new InvalidDataException("Размер {size=...}: от 1 до 256, десятичный разделитель — точка.");
    }

    private static string ParseFont(string alias, Func<string, bool>? isConnected)
    {
        if (alias.Length <= 80 && LuaSource.IsIdentifier(alias) && isConnected?.Invoke(alias) != false) return alias;
        throw new InvalidDataException($"Шрифт «{alias}» должен быть подключён по этому имени до использования {{font=...}}.");
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
