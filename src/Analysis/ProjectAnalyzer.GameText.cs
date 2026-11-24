using System.Text.Json;

namespace KNE;

public static partial class ProjectAnalyzer
{
    internal static IReadOnlyList<GameTextEntry> ReadLuaGameText(string source, string file)
    {
        var tokens = Lex(source);
        var entries = new List<GameTextEntry>();
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            string function = tokens[i].Text;
            if (function is not ("d" or "kne_say" or "choice" or "kne_choice" or "text_input" or "show_title" or "set_window_title"
                or "watermark" or "register_character" or "update_character") || !IsGlobalCall(tokens, i)) continue;
            string location = file + ":" + tokens[i].Line;
            var arguments = LiteralArguments(tokens, i + 1);
            string? first = arguments.FirstOrDefault();
            void Add(GameTextCategory category, string? text, bool includeEmpty = false)
            {
                if (text != null && (includeEmpty || !string.IsNullOrWhiteSpace(text))) entries.Add(new(category, text, location));
            }
            switch (function)
            {
                case "d" or "kne_say":
                    Add(GameTextCategory.Dialogue, arguments.ElementAtOrDefault(1), includeEmpty: true);
                    Add(GameTextCategory.Other, first);
                    break;
                case "choice":
                    Add(GameTextCategory.Interface, first);
                    int options = ChoiceTable(tokens, i + 1);
                    if (options >= 0)
                        foreach (string? label in LiteralArguments(tokens, options))
                            Add(GameTextCategory.Interface, label);
                    break;
                case "kne_choice":
                    if (Read<ChoiceRequest>(first) is { } choice)
                    {
                        Add(GameTextCategory.Interface, choice.Prompt);
                        foreach (var option in choice.Options ?? [])
                        {
                            Add(GameTextCategory.Interface, option?.Text);
                            if (option?.Lock is { ShowHint: true } rules) Add(GameTextCategory.Interface, rules.Hint);
                        }
                    }
                    break;
                case "text_input":
                    if (Read<TextInputSettings>(first) is { } input)
                        foreach (string text in GameTextCatalog.InputTexts(input)) Add(GameTextCategory.Interface, text);
                    break;
                case "register_character" or "update_character":
                    Add(GameTextCategory.Other, Read<CharacterProfile>(first)?.Name);
                    break;
                default:
                    Add(GameTextCategory.Other, first);
                    break;
            }
        }
        return entries;

        static T? Read<T>(string? json) where T : class
        {
            try { return json == null ? null : JsonSerializer.Deserialize<T>(json, NarrativeValidation.JsonOptions); }
            catch (JsonException) { return null; }
        }
    }

    private static int ChoiceTable(IReadOnlyList<Lexeme> tokens, int opening)
    {
        if (tokens[opening].Token.Partner is not int end) return -1;
        for (int index = opening + 1; index + 1 < tokens.Count && tokens[index].Token.Start < end; index++)
        {
            if (tokens[index].Text == ",") return tokens[index + 1].Text == "{" ? index + 1 : -1;
            if (tokens[index].Text is "(" or "[" or "{" && tokens[index].Token.Partner is int nested)
                while (index + 1 < tokens.Count && tokens[index + 1].Token.Start <= nested) index++;
        }
        return -1;
    }
}
