using System.Text;

namespace KNE;

// Anchors source comments to the fragments that own their surrounding Lua syntax.
internal sealed record LuaCommentPlacement(string Id, string Node, string Part, int Offset, string Original)
{
    internal static void Import(VisualScript graph)
    {
        var placements = new List<LuaCommentPlacement>();
        var notes = new List<ScriptNode>();
        foreach (var node in graph.Nodes)
        {
            if (node.Lua is not { } lua) continue;
            Read(node.Id, "code", lua.Code, node.X, node.Y);
            Read(node.Id, "else", lua.Otherwise, node.X, node.Y);
            Read(node.Id, "close", lua.Close, node.X, node.Y);
        }
        Read("", "trailing", graph.LuaTrailingSource ?? "", 40, graph.Nodes.Max(node => node.Y) + 146);
        if (notes.Count == 0) return;
        graph.Nodes.AddRange(notes);
        graph.LuaComments = placements;

        void Read(string node, string part, string source, double x, double y)
        {
            if (!source.Contains("--", StringComparison.Ordinal)) return;
            int offset = 0;
            foreach (var token in LuaSyntax.Parse(source))
            {
                if (graph.Nodes.Count + notes.Count >= 500) break;
                if (token.Kind != LuaTokenKind.Comment || token.End - token.Start > 16_002) continue;
                string original = source[token.Start..token.End];
                var note = new ScriptNode { Kind = ScriptNodeKind.Comment, Category = "lua_raw",
                    Text = original[2..], X = x - 245, Y = y + 146 * offset++ };
                notes.Add(note);
                placements.Add(new(note.Id, node, part, token.Start, original));
            }
        }
    }

    internal static string Apply(string text, IEnumerable<LuaCommentPlacement> placements, IReadOnlyDictionary<string, ScriptNode> nodes)
    {
        var anchors = placements.ToArray();
        if (anchors.Length == 0) return text;
        var tokens = LuaSyntax.Parse(text).Where(token => token.Kind == LuaTokenKind.Comment).ToArray();
        var output = new StringBuilder(text.Length);
        int cursor = 0, first = 0;
        foreach (var anchor in anchors.OrderBy(anchor => anchor.Offset))
        {
            int index = Array.FindIndex(tokens, first, token => text.AsSpan(token.Start, token.End - token.Start).SequenceEqual(anchor.Original));
            if (index < 0) continue;
            var token = tokens[index];
            first = index + 1;
            output.Append(text.AsSpan(cursor, token.Start - cursor));
            if (nodes.TryGetValue(anchor.Id, out var note))
            {
                bool inline = token.End < text.Length && text[token.End] is not ('\n' or '\r');
                output.Append(note.Text == anchor.Original[2..] ? anchor.Original : Encode(note.Text, inline));
            }
            else
            {
                output.Append(' ');
                foreach (char character in anchor.Original)
                    if (character is '\r' or '\n') output.Append(character);
            }
            cursor = token.End;
        }
        return output.Append(text.AsSpan(cursor)).ToString();
    }

    internal static string Encode(string text, bool inline)
    {
        if (!inline && !text.Contains('\n') && !text.Contains('\r') && !text.StartsWith('[')) return "--" + text;
        string equals = "";
        while (text.Contains("]" + equals + "]", StringComparison.Ordinal) || text.EndsWith("]" + equals, StringComparison.Ordinal)) equals += "=";
        return "--[" + equals + "[" + text + "]" + equals + "]";
    }
}
