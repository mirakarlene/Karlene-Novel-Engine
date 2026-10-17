using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace KNE;

/// <summary>The rule applied to an answer after length and mask validation.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<TextInputValidationMode>))]
public enum TextInputValidationMode { None, Exact, Regex }

/// <summary>Immutable configuration for a foreground player-input frame.</summary>
public sealed record TextInputSettings
{
    public string Variable { get; init; } = "player_answer";
    public string Prompt { get; init; } = "Введите ответ:";
    public int MaximumLength { get; init; } = 32;
    public TextInputValidationMode ValidationMode { get; init; }
    public string ExpectedValue { get; init; } = "";
    public bool IgnoreCase { get; init; }
    public string Pattern { get; init; } = "";
    public string FailureMessage { get; init; } = "Попробуйте снова";
    public string Placeholder { get; init; } = "";
    public bool AutoFocus { get; init; } = true;
    public string InputMask { get; init; } = "";
    public bool IsPassword { get; init; }
    public bool BranchOnResult { get; init; }
    public bool HasErrorOutput { get; init; }
    public bool HasCancelOutput { get; init; }

    /// <summary>Checks author-supplied settings before compiling or presenting an input frame.</summary>
    public void Validate()
    {
        if (Variable is null || Variable.Length > 80 || !LuaSource.IsIdentifier(Variable))
            throw new ArgumentException("Переменная ответа: латинские буквы, цифры и _, без цифры в начале и ключевых слов Lua.");
        if (MaximumLength is < 1 or > 256) throw new ArgumentException("Максимальная длина ввода: от 1 до 256 символов.");
        if (!Enum.IsDefined(ValidationMode)) throw new ArgumentException("Неизвестный режим проверки ввода.");
        if (Prompt is null || ExpectedValue is null || Pattern is null || FailureMessage is null || Placeholder is null || InputMask is null)
            throw new ArgumentException("Параметры ввода текста не могут содержать null.");
        if (Prompt.Length > 16000 || ExpectedValue.Length > 8192 || Pattern.Length > 2048 || FailureMessage.Length > 16000 || Placeholder.Length > 512 || InputMask.Length > 512)
            throw new ArgumentException("Слишком длинный параметр ввода текста.");
        if (!BranchOnResult && (HasErrorOutput || HasCancelOutput)) throw new ArgumentException("Выходы ошибки и отмены требуют включить ветвление результата.");
        if (InputMask.Length > 0)
        {
            if (MaskParts(InputMask).Count > MaximumLength) throw new ArgumentException("Маска ввода длиннее максимальной длины ответа.");
        }
        if (ValidationMode == TextInputValidationMode.Regex)
        {
            try { _ = AnswerRegex(); }
            catch (ArgumentException error) { throw new ArgumentException("Некорректное регулярное выражение ввода: " + error.Message, error); }
        }
    }

    /// <summary>Counts displayed Unicode text elements instead of UTF-16 code units.</summary>
    public static int CountCharacters(string text) => new StringInfo(text).LengthInTextElements;

    /// <summary>Validates a complete answer; regex evaluation is limited to 100 milliseconds.</summary>
    public bool ValidateAnswer(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 8192 || CountCharacters(text) > MaximumLength || !MatchesMask(text)) return false;
        return ValidationMode switch
        {
            TextInputValidationMode.None => true,
            TextInputValidationMode.Exact => string.Equals(text, ExpectedValue, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal),
            TextInputValidationMode.Regex => RegexMatches(text),
            _ => false
        };
    }

    private Regex AnswerRegex() => new("\\A(?:" + Pattern + ")\\z", RegexOptions.CultureInvariant | (IgnoreCase ? RegexOptions.IgnoreCase : RegexOptions.None), TimeSpan.FromMilliseconds(100));

    private bool RegexMatches(string text)
    {
        try { return AnswerRegex().IsMatch(text); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private bool MatchesMask(string text)
    {
        if (InputMask.Length == 0) return true;
        var parts = MaskParts(InputMask);
        var elements = StringInfo.GetTextElementEnumerator(text);
        int index = 0;
        while (elements.MoveNext())
        {
            if (index >= parts.Count) return false;
            string element = elements.GetTextElement();
            var part = parts[index++];
            bool ascii = element.Length == 1;
            if (!(part.Token switch
            {
                '0' => ascii && char.IsAsciiDigit(element[0]),
                'L' => ascii && char.IsAsciiLetter(element[0]),
                'A' => ascii && char.IsAsciiLetterOrDigit(element[0]),
                '&' => true,
                _ => element == part.Literal
            })) return false;
        }
        return index == parts.Count;
    }

    private static List<(char Token, string Literal)> MaskParts(string mask)
    {
        var parts = new List<(char, string)>();
        var elements = StringInfo.GetTextElementEnumerator(mask);
        while (elements.MoveNext())
        {
            string element = elements.GetTextElement();
            if (element == "\\")
            {
                if (!elements.MoveNext()) throw new ArgumentException("Маска ввода не может заканчиваться одиночным \\. Экранируйте литерал вторым \\.");
                parts.Add(('\0', elements.GetTextElement()));
            }
            else parts.Add((element.Length == 1 && element[0] is '0' or 'L' or 'A' or '&' ? element[0] : '\0', element));
        }
        return parts;
    }
}
