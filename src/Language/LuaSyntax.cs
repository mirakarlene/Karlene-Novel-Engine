namespace KNE;

public enum LuaTokenKind
{
    Keyword, Control, Function, Identifier, Number, String, Comment, Operator, Bracket, InvalidBracket
}

public sealed record LuaToken(int Start, int Length, LuaTokenKind Kind, int Depth = 0, int? Partner = null)
{
    public int End => Start + Length;
}

/// <summary>Source-preserving Lua/KRL tokens and bracket pairs for the code editor.</summary>
public static class LuaSyntax
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
        { "local", "function", "true", "false", "nil" };
    private static readonly HashSet<string> Controls = new(StringComparer.Ordinal)
        { "and", "break", "do", "else", "elseif", "end", "for", "goto", "if", "in", "not", "or", "repeat", "return", "then", "until", "while" };
    private static readonly HashSet<string> Functions = new(StringComparer.Ordinal)
    {
        "assert", "collectgarbage", "dofile", "error", "getmetatable", "ipairs", "load", "loadfile", "next", "pairs", "pcall",
        "print", "rawequal", "rawget", "rawlen", "rawset", "require", "select", "setmetatable", "tonumber", "tostring", "type", "warn", "xpcall",
        "init", "d", "choice", "scene", "call", "call_scene", "goto_scene", "jump", "print_to_ui", "set_window_title", "set_window_size", "set_bg_color",
        "background", "show_character", "hide_character", "play_music", "stop_music", "play_sound",
        "show_title", "hide_background", "move_visual", "animate_visual", "screen_shake", "screen_flash", "camera", "show_screen",
        "set_audio_channel", "play_audio", "stop_audio", "set_master_volume", "kne_parallel", "kne_async", "kne_join", "kne_pause", "kne_timer", "kne_wait_condition", "kne_end",
        "include_lua", "include_cs", "run_script", "configure_ui", "clear_backgrounds", "clear_sprites",
        "register_character", "kne_say", "text_pause", "clear_text", "console_write", "kne_random_branch"
    };

    public static IReadOnlyList<LuaToken> Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<LuaToken>();
        var brackets = new Stack<int>();
        int position = 0;
        while (position < text.Length)
        {
            if (char.IsWhiteSpace(text[position])) { position++; continue; }
            int start = position;
            char character = text[position];
            LuaTokenKind kind;
            if (character == '-' && At(text, position + 1, '-'))
            {
                position += 2;
                if (LongOpening(text, position, out int content, out int level)) position = LongEnd(text, content, level);
                else while (position < text.Length && text[position] is not '\r' and not '\n') position++;
                kind = LuaTokenKind.Comment;
            }
            else if (character is '\'' or '"')
            {
                position = QuotedEnd(text, position);
                kind = LuaTokenKind.String;
            }
            else if (character == '[' && LongOpening(text, position, out int content, out int level))
            {
                position = LongEnd(text, content, level);
                kind = LuaTokenKind.String;
            }
            else if (char.IsAsciiLetter(character) || character == '_')
            {
                position++;
                while (position < text.Length && (char.IsAsciiLetterOrDigit(text[position]) || text[position] == '_')) position++;
                string name = text[start..position];
                kind = Keywords.Contains(name) ? LuaTokenKind.Keyword : Controls.Contains(name) ? LuaTokenKind.Control
                    : Functions.Contains(name) ? LuaTokenKind.Function : LuaTokenKind.Identifier;
            }
            else if (char.IsAsciiDigit(character) || (character == '.' && position + 1 < text.Length && char.IsAsciiDigit(text[position + 1])))
            {
                position = NumberEnd(text, position);
                kind = LuaTokenKind.Number;
            }
            else if (character is '(' or '[' or '{')
            {
                tokens.Add(new(start, 1, LuaTokenKind.Bracket, brackets.Count));
                brackets.Push(tokens.Count - 1);
                position++;
                continue;
            }
            else if (character is ')' or ']' or '}')
            {
                position++;
                if (brackets.TryPop(out int openingIndex))
                {
                    LuaToken opening = tokens[openingIndex];
                    if (Matches(text[opening.Start], character))
                    {
                        tokens[openingIndex] = opening with { Partner = start };
                        tokens.Add(new(start, 1, LuaTokenKind.Bracket, opening.Depth, opening.Start));
                    }
                    else
                    {
                        tokens[openingIndex] = opening with { Kind = LuaTokenKind.InvalidBracket };
                        tokens.Add(new(start, 1, LuaTokenKind.InvalidBracket, opening.Depth));
                    }
                }
                else tokens.Add(new(start, 1, LuaTokenKind.InvalidBracket));
                continue;
            }
            else
            {
                position += OperatorLength(text, position);
                kind = LuaTokenKind.Operator;
            }
            tokens.Add(new(start, position - start, kind));
        }
        foreach (int index in brackets) tokens[index] = tokens[index] with { Kind = LuaTokenKind.InvalidBracket };

        // Look ahead through comments once, so incomplete declarations and call sites also receive function colors.
        int nextSignificant = -1;
        for (int index = tokens.Count - 1; index >= 0; index--)
        {
            LuaToken token = tokens[index];
            if (token.Kind == LuaTokenKind.Comment) continue;
            if (token.Kind == LuaTokenKind.Identifier && nextSignificant >= 0 && text[tokens[nextSignificant].Start] == '(')
                tokens[index] = token with { Kind = LuaTokenKind.Function };
            nextSignificant = index;
        }
        bool declaration = false;
        for (int index = 0; index < tokens.Count; index++)
        {
            LuaToken token = tokens[index];
            if (token.Kind == LuaTokenKind.Comment) continue;
            if (declaration && (token.Kind is LuaTokenKind.Identifier or LuaTokenKind.Function))
            {
                int next = index + 1;
                while (next < tokens.Count && tokens[next].Kind == LuaTokenKind.Comment) next++;
                bool qualified = next < tokens.Count && tokens[next].Kind == LuaTokenKind.Operator && (text[tokens[next].Start] is '.' or ':');
                if (!qualified) tokens[index] = token with { Kind = LuaTokenKind.Function };
                declaration = qualified;
            }
            else if (!declaration || token.Kind != LuaTokenKind.Operator || text[token.Start] is not '.' and not ':')
                declaration = token.Kind == LuaTokenKind.Keyword && text.AsSpan(token.Start, token.Length).SequenceEqual("function");
        }
        return tokens.AsReadOnly();
    }

    private static bool At(string text, int position, char expected) => position < text.Length && text[position] == expected;
    private static bool Matches(char opening, char closing) => (opening, closing) is ('(', ')') or ('[', ']') or ('{', '}');

    private static bool LongOpening(string text, int position, out int content, out int level)
    {
        content = position;
        level = 0;
        if (!At(text, position, '[')) return false;
        int cursor = position + 1;
        while (At(text, cursor, '=')) { level++; cursor++; }
        if (!At(text, cursor, '[')) return false;
        content = cursor + 1;
        return true;
    }

    private static int LongEnd(string text, int position, int level)
    {
        while (position < text.Length)
        {
            if (text[position++] != ']') continue;
            int closingStart = position;
            while (At(text, position, '=')) position++;
            if (position - closingStart == level && At(text, position, ']')) return position + 1;
        }
        return text.Length;
    }

    private static int QuotedEnd(string text, int position)
    {
        char quote = text[position++];
        while (position < text.Length)
        {
            char character = text[position++];
            if (character == quote) return position;
            if (character is '\r' or '\n') return position - 1;
            if (character != '\\' || position >= text.Length) continue;
            char escaped = text[position++];
            if (escaped == 'z') while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
            else if (escaped == '\r' && At(text, position, '\n')) position++;
        }
        return position;
    }

    private static int NumberEnd(string text, int position)
    {
        bool hex = At(text, position, '0') && position + 2 < text.Length && (text[position + 1] is 'x' or 'X') &&
            (char.IsAsciiHexDigit(text[position + 2]) || (text[position + 2] == '.' && position + 3 < text.Length && char.IsAsciiHexDigit(text[position + 3])));
        if (hex) position += 2;
        while (position < text.Length && Digit(text[position], hex)) position++;
        if (At(text, position, '.') && !At(text, position + 1, '.'))
        {
            position++;
            while (position < text.Length && Digit(text[position], hex)) position++;
        }
        if (position < text.Length && (hex ? text[position] is 'p' or 'P' : text[position] is 'e' or 'E'))
        {
            int exponent = position + 1;
            if (exponent < text.Length && text[exponent] is '+' or '-') exponent++;
            if (exponent < text.Length && char.IsAsciiDigit(text[exponent]))
            {
                position = exponent + 1;
                while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
            }
        }
        return position;
    }

    private static bool Digit(char character, bool hex) => hex ? char.IsAsciiHexDigit(character) : char.IsAsciiDigit(character);

    private static int OperatorLength(string text, int position)
    {
        if (At(text, position, '.') && At(text, position + 1, '.') && At(text, position + 2, '.')) return 3;
        if (position + 1 >= text.Length) return 1;
        return (text[position], text[position + 1]) is ('.', '.') or ('/', '/') or ('<', '<') or ('>', '>') or ('=', '=')
            or ('~', '=') or ('<', '=') or ('>', '=') or (':', ':') ? 2 : 1;
    }
}
