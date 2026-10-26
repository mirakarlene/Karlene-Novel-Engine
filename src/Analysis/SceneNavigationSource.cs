using System.Text;

namespace KNE;

/// <summary>Conservative static links: strings/comments/member calls never become scene transitions.</summary>
internal static class SceneNavigationSource
{
    internal sealed record Reference(int? Owner, string Target, string Kind, bool Path = false, bool Required = true);
    internal sealed record Result(IReadOnlyList<Reference> References, int Dynamic);
    private sealed record Lexeme(LuaToken Token, string Text, int Line);

    internal static Result Read(string source, IReadOnlyList<LuaFunctionDeclaration> functions)
    {
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < source.Length; i++)
            if (source[i] == '\n' || source[i] == '\r' && (i + 1 == source.Length || source[i + 1] != '\n')) lineStarts.Add(i + 1);
        var tokens = LuaSyntax.Parse(source).Where(token => token.Kind != LuaTokenKind.Comment).Select(token =>
        {
            int line = lineStarts.BinarySearch(token.Start);
            return new Lexeme(token, source[token.Start..token.End], line >= 0 ? line + 1 : ~line);
        }).ToArray();
        var ranges = new List<(int Owner, int Start, int End)>();
        var used = new HashSet<int>();
        for (int index = 0; index < functions.Count; index++)
        {
            var declaration = functions[index];
            int name = Enumerable.Range(0, tokens.Length).FirstOrDefault(i => tokens[i].Line == declaration.Line && tokens[i].Text == declaration.Name && !used.Contains(tokens[i].Token.Start) &&
                (i > 0 && tokens[i - 1].Text == "function" || i + 2 < tokens.Length && tokens[i + 1].Text == "=" && tokens[i + 2].Text == "function"), -1);
            if (name < 0) continue;
            used.Add(tokens[name].Token.Start);
            int opening = name + 1;
            while (opening < tokens.Length && tokens[opening].Text != "(") opening++;
            if (opening == tokens.Length || tokens[opening].Token.Partner is not { } close) continue;
            int closing = Array.FindIndex(tokens, opening, token => token.Token.Start == close);
            if (closing < 0) continue;
            int end = BlockEnd(tokens, closing + 1);
            ranges.Add((index, closing + 1, end < 0 ? tokens.Length : end));
        }
        var shadowed = ShadowedNames(tokens);
        var result = new List<Reference>(); int dynamic = 0;
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            var token = tokens[i];
            if (token.Token.Kind == LuaTokenKind.String || tokens[i + 1].Text != "(" || i > 0 && tokens[i - 1].Text is "function" or "." or ":") continue;
            if (shadowed.Any(local => local.Name == token.Text && i >= local.Start && i < local.End)) continue;
            int? owner = ranges.Where(range => i >= range.Start && i < range.End).OrderBy(range => range.End - range.Start).Select(range => (int?)range.Owner).FirstOrDefault();
            if (token.Text is "goto" or "goto_scene" or "jump" or "call" or "call_scene" or "kne_subsystem")
            {
                if (i + 3 >= tokens.Length || tokens[i + 3].Text is not (")" or ",")) { dynamic++; continue; }
                bool path = token.Text == "kne_subsystem";
                string? target = Literal(tokens[i + 2]);
                bool literal = target != null;
                if (target == null && !path && LuaSource.IsIdentifier(tokens[i + 2].Text)) target = tokens[i + 2].Text;
                if (target == null) { dynamic++; continue; }
                result.Add(new(owner, target, token.Text, path, literal));
            }
            else if (LuaSource.IsIdentifier(token.Text)) result.Add(new(owner, token.Text, "Вызов", Required: false));
        }
        return new(result, dynamic);
    }

    private static List<(string Name, int Start, int End)> ShadowedNames(Lexeme[] tokens)
    {
        var scopes = new List<(int Start, int End)>();
        var result = new List<(string, int, int)>();
        for (int i = 0; i < tokens.Length; i++)
        {
            if (tokens[i].Text != "function" || tokens[i].Token.Kind == LuaTokenKind.String) continue;
            int opening = i + 1;
            while (opening < tokens.Length && tokens[opening].Text is not ("(" or "end")) opening++;
            if (opening == tokens.Length || tokens[opening].Token.Partner is not { } close) continue;
            int closing = Array.FindIndex(tokens, opening, token => token.Token.Start == close);
            if (closing < 0) continue;
            int end = BlockEnd(tokens, closing + 1); if (end < 0) end = tokens.Length;
            scopes.Add((closing + 1, end));
            for (int parameter = opening + 1; parameter < closing; parameter++)
                if (LuaSource.IsIdentifier(tokens[parameter].Text)) result.Add((tokens[parameter].Text, closing + 1, end));
        }
        for (int i = 0; i + 1 < tokens.Length; i++)
        {
            if (tokens[i].Text != "local" || tokens[i].Token.Kind == LuaTokenKind.String) continue;
            int end = scopes.Where(scope => i >= scope.Start && i < scope.End).OrderBy(scope => scope.End - scope.Start).Select(scope => scope.End).FirstOrDefault(tokens.Length);
            int name = i + 1; if (tokens[name].Text == "function") name++;
            while (name < tokens.Length && LuaSource.IsIdentifier(tokens[name].Text))
            {
                result.Add((tokens[name].Text, i, end));
                if (name + 1 >= tokens.Length || tokens[name + 1].Text != ",") break;
                name += 2;
            }
        }
        return result;
    }

    private static int BlockEnd(IReadOnlyList<Lexeme> tokens, int start)
    {
        int depth = 1, awaitingDo = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            if (tokens[i].Token.Kind == LuaTokenKind.String) continue;
            string text = tokens[i].Text;
            if (text is "function" or "if" or "repeat") depth++;
            else if (text is "while" or "for") { depth++; awaitingDo++; }
            else if (text == "do") { if (awaitingDo > 0) awaitingDo--; else depth++; }
            else if (text is "end" or "until" && --depth == 0) return i;
        }
        return -1;
    }

    private static string? Literal(Lexeme token)
    {
        if (token.Token.Kind != LuaTokenKind.String || token.Text.Length < 2) return null;
        string text = token.Text;
        if (text[0] == '[')
        {
            int opening = text.IndexOf('[', 1);
            if (opening < 0 || text.Length < (opening + 1) * 2) return null;
            return text[(opening + 1)..^(opening + 1)].TrimStart('\r', '\n');
        }
        if (text[^1] != text[0]) return null;
        var result = new StringBuilder();
        for (int i = 1; i < text.Length - 1; i++)
        {
            char value = text[i];
            if (value != '\\') { result.Append(value); continue; }
            if (++i >= text.Length - 1) return null;
            value = text[i];
            if (value is '\\' or '\'' or '"') result.Append(value);
            else if (value is 'n' or 'r' or 't') result.Append(value == 'n' ? '\n' : value == 'r' ? '\r' : '\t');
            else return null;
        }
        return result.ToString();
    }
}
