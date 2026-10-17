namespace KNE;

/// <summary>Four-space Lua indentation inferred from executable tokens, excluding strings and comments.</summary>
public static class LuaIndentation
{
    public const int Size = 4;

    public static int Columns(string leadingWhitespace)
    {
        int columns = 0;
        foreach (char value in leadingWhitespace)
        {
            if (value == ' ') columns++;
            else if (value == '\t') columns += Size - columns % Size;
            else break;
        }
        return columns;
    }

    public static string ForNewLine(string text, int caret)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(caret);
        if (caret > text.Length) throw new ArgumentOutOfRangeException(nameof(caret));
        int start = LineStart(text, caret);
        int indentation = Columns(text[start..caret]);
        int depth = 0;
        bool firstOnLine = true;
        foreach (var token in LuaSyntax.Parse(text[..caret]))
        {
            if (token.Start < start || token.Kind is LuaTokenKind.String or LuaTokenKind.Comment) continue;
            string value = text.Substring(token.Start, token.Length);
            if (value is "function" or "then" or "do" or "repeat" or "(" or "[" or "{" || value == "else" && firstOnLine) depth++;
            else if (value is "end" or "until" or ")" or "]" or "}") depth--;
            firstOnLine = false;
        }
        return new string(' ', indentation + Size * Math.Max(0, depth));
    }

    /// <summary>Returns the indentation of the opener for an end/else/until or a closing bracket line.</summary>
    public static int? ClosingColumns(string text, int lineStart)
    {
        if (LuaSyntax.Parse(text).Any(t => t.Start < lineStart && t.End > lineStart && t.Kind is LuaTokenKind.String or LuaTokenKind.Comment)) return null;
        int end = text.IndexOfAny(['\r', '\n'], lineStart);
        if (end < 0) end = text.Length;
        var first = LuaSyntax.Parse(text[lineStart..end]).FirstOrDefault();
        if (first == null) return null;
        string closing = text.Substring(lineStart + first.Start, first.Length);
        if (first.Kind is LuaTokenKind.String or LuaTokenKind.Comment || closing is not ("end" or "else" or "elseif" or "until" or ")" or "]" or "}")) return null;
        var stack = new Stack<(string Value, int Offset)>();
        bool elseifHeader = false;
        foreach (var token in LuaSyntax.Parse(text[..lineStart]))
        {
            if (token.Kind is LuaTokenKind.String or LuaTokenKind.Comment) continue;
            string value = text.Substring(token.Start, token.Length);
            if (value == "elseif") { elseifHeader = true; continue; }
            if (value == "if") elseifHeader = false;
            if (value == "then" && elseifHeader) { elseifHeader = false; continue; }
            if (value is "function" or "then" or "do" or "repeat" or "(" or "[" or "{") stack.Push((value, token.Start));
            else if (value is "end" or "until" or ")" or "]" or "}")
            {
                if (stack.Count > 0) stack.Pop();
            }
        }
        if (!stack.TryPeek(out var opening)) return null;
        bool compatible = closing switch
        {
            ")" => opening.Value == "(", "]" => opening.Value == "[", "}" => opening.Value == "{",
            "until" => opening.Value == "repeat", "else" or "elseif" => opening.Value == "then",
            _ => opening.Value is "function" or "then" or "do"
        };
        return compatible ? Columns(text[LineStart(text, opening.Offset)..opening.Offset]) : null;
    }

    private static int LineStart(string text, int offset)
    {
        while (offset > 0 && text[offset - 1] is not ('\r' or '\n')) offset--;
        return offset;
    }
}
