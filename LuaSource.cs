using System.Text;

namespace KNE;

/// <summary>Lexes Lua source without treating comments or string contents as executable code.</summary>
public static class LuaSource
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    { "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "goto", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while" };

    public static bool IsIdentifier(string name) => !string.IsNullOrEmpty(name) &&
        (char.IsAsciiLetter(name[0]) || name[0] == '_') && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_') && !Keywords.Contains(name);

    /// <summary>Finds named global functions, including name = function(...) assignments.</summary>
    public static IReadOnlyList<LuaFunctionDeclaration> Functions(string source)
    {
        var tokens = Tokens(source);
        var functions = new List<LuaFunctionDeclaration>();
        var scopes = new List<Scope> { new("root", 0) };
        int tableDepth = 0;
        bool Local(string name) => scopes.Any(s => s.Locals.Contains(name));
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Text == "{") { tableDepth++; continue; }
            if (token.Text == "}") { tableDepth = Math.Max(0, tableDepth - 1); continue; }
            if (token.Text == "local")
            {
                int name = i + 1;
                if (name < tokens.Count && tokens[name].Text == "function") name++;
                while (name < tokens.Count && IsIdentifier(tokens[name].Text))
                {
                    scopes[^1].Locals.Add(tokens[name].Text);
                    if (name + 1 >= tokens.Count || tokens[name + 1].Text != ",") break;
                    name += 2;
                }
                continue;
            }
            if (token.Text == "function")
            {
                int name = i + 1;
                bool explicitGlobal = name + 2 < tokens.Count && tokens[name].Text == "_G" && tokens[name + 1].Text == ".";
                if (explicitGlobal) name += 2;
                if (name + 1 < tokens.Count && IsIdentifier(tokens[name].Text) && tokens[name + 1].Text == "(" &&
                    (explicitGlobal || !Local(tokens[name].Text)) && (i == 0 || tokens[i - 1].Text != "local"))
                    functions.Add(new(tokens[name].Text, tokens[name].Line));
                var scope = new Scope("function", tableDepth);
                int parameters = name;
                while (parameters < tokens.Count && tokens[parameters].Text is not ("(" or "end")) parameters++;
                for (int p = parameters + 1; p < tokens.Count && tokens[p].Text != ")"; p++)
                    if (IsIdentifier(tokens[p].Text)) scope.Locals.Add(tokens[p].Text);
                scopes.Add(scope); tableDepth = 0;
                continue;
            }
            if (token.Text is "if" or "while" or "for" or "repeat")
            {
                var scope = new Scope(token.Text, tableDepth) { AwaitingDo = token.Text is "while" or "for" };
                if (token.Text == "for")
                    for (int p = i + 1; p < tokens.Count && tokens[p].Text is not ("=" or "in"); p++)
                        if (IsIdentifier(tokens[p].Text)) scope.Locals.Add(tokens[p].Text);
                scopes.Add(scope); continue;
            }
            if (token.Text == "do")
            {
                if (scopes[^1].AwaitingDo) scopes[^1].AwaitingDo = false;
                else scopes.Add(new("do", tableDepth));
                continue;
            }
            if (token.Text is "else" or "elseif") { scopes[^1].Locals.Clear(); continue; }
            if (token.Text is "end" or "until")
            {
                if (scopes.Count > 1)
                {
                    if (scopes[^1].Kind == "function") tableDepth = scopes[^1].TableDepth;
                    scopes.RemoveAt(scopes.Count - 1);
                }
                continue;
            }
            if (!IsIdentifier(token.Text)) continue;
            bool globalField = i >= 2 && tokens[i - 2].Text == "_G" && tokens[i - 1].Text == ".";
            bool assignment = i + 3 < tokens.Count && tokens[i + 1].Text == "=" && tokens[i + 2].Text == "function" && tokens[i + 3].Text == "(" &&
                (globalField || tableDepth == 0 && !Local(token.Text) && (i == 0 || tokens[i - 1].Text is not ("local" or ":" or ".")));
            if (assignment) functions.Add(new(token.Text, token.Line));
        }
        return functions;
    }

    /// <summary>Supports gameplay goto(...) while preserving native Lua goto label statements.</summary>
    public static string Prepare(string source)
    {
        var tokens = Tokens(source);
        var result = new StringBuilder(source.Length);
        int copied = 0;
        for (int i = 0; i + 1 < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Text != "goto" || tokens[i + 1].Text != "(") continue;
            result.Append(source, copied, token.Offset - copied).Append("goto_scene");
            copied = token.Offset + token.Text.Length;
        }
        return copied == 0 ? source : result.Append(source, copied, source.Length - copied).ToString();
    }

    private sealed record Token(string Text, int Offset, int Line);
    private sealed class Scope(string kind, int tableDepth)
    {
        public string Kind { get; } = kind;
        public int TableDepth { get; } = tableDepth;
        public bool AwaitingDo { get; set; }
        public HashSet<string> Locals { get; } = new(StringComparer.Ordinal);
    }

    private static List<Token> Tokens(string source)
    {
        var tokens = new List<Token>();
        int position = 0, line = 1;
        void Move(int end)
        {
            while (position < end)
            {
                if (source[position] == '\r' || source[position] == '\n' && (position == 0 || source[position - 1] != '\r')) line++;
                position++;
            }
        }
        int LongEnd(int start)
        {
            if (start >= source.Length || source[start] != '[') return -1;
            int marker = start + 1;
            while (marker < source.Length && source[marker] == '=') marker++;
            if (marker >= source.Length || source[marker] != '[') return -1;
            string close = "]" + source.Substring(start + 1, marker - start - 1) + "]";
            int end = source.IndexOf(close, marker + 1, StringComparison.Ordinal);
            return end < 0 ? source.Length : end + close.Length;
        }
        while (position < source.Length)
        {
            char c = source[position];
            if (char.IsWhiteSpace(c) || c == '\uFEFF') { Move(position + 1); continue; }
            if (c == '-' && position + 1 < source.Length && source[position + 1] == '-')
            {
                int end = LongEnd(position + 2);
                if (end < 0)
                {
                    end = source.IndexOfAny(['\r', '\n'], position + 2);
                    if (end < 0) end = source.Length;
                }
                Move(end); continue;
            }
            if (c is '\'' or '"')
            {
                int end = position + 1;
                while (end < source.Length)
                {
                    if (source[end] == '\\') { end = Math.Min(source.Length, end + 2); continue; }
                    if (source[end++] == c) break;
                }
                tokens.Add(new("<string>", position, line)); Move(end); continue;
            }
            int longEnd = c == '[' ? LongEnd(position) : -1;
            if (longEnd >= 0) { tokens.Add(new("<string>", position, line)); Move(longEnd); continue; }
            int start = position, tokenLine = line;
            if (char.IsAsciiLetter(c) || c == '_')
            {
                int end = position + 1;
                while (end < source.Length && (char.IsAsciiLetterOrDigit(source[end]) || source[end] == '_')) end++;
                Move(end);
            }
            else Move(position + 1);
            tokens.Add(new(source[start..position], start, tokenLine));
        }
        return tokens;
    }
}

public sealed record LuaFunctionDeclaration(string Name, int Line);
