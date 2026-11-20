using System.IO;
using System.Text;

namespace KNE;

/// <summary>Converts Lua chunks to structured flow and emits the original source fragments in graph order.</summary>
internal static class VisualLua
{
    internal const string LayoutDirectory = ".kne-vlua-layout";

    internal static string ReadProjectSource(string path)
    {
        string text = ScriptProtection.ReadText(path);
        if (!ScriptDocument.IsVisualLuaPath(path)) return text;
        var graph = VisualScript.Parse(text);
        if (!graph.IsVisualLua) throw new InvalidDataException("Для .vlua нужна блок-схема Visual Lua.");
        return graph.Compile();
    }

    internal static string ResolveTwin(string path)
    {
        if (!File.Exists(path) && ScriptDocument.IsLuaPath(path))
        {
            string twin = Path.ChangeExtension(path, ScriptDocument.IsVisualLuaPath(path) ? ".lua" : ".vlua");
            if (File.Exists(twin)) return twin;
        }
        return path;
    }

    internal static VisualScript ParseSource(string source)
    {
        VisualScript graph;
        try { graph = new Reader(source).Read(); }
        catch (InvalidDataException)
        {
            // An incomplete or unusually large chunk still round-trips as an editable source block.
            graph = EmptyGraph();
            var raw = VisualLuaCatalog.Create("raw");
            raw.X = 360; raw.Y = 60; raw.Lua = new() { Code = source };
            graph.Nodes[0].Next = raw.Id;
            graph.Nodes.Add(raw);
        }
        LuaCommentPlacement.Import(graph);
        return graph;
    }

    private static VisualScript EmptyGraph() => new()
    {
        IsVisualLua = true,
        Nodes = [new() { Id = "start", Kind = ScriptNodeKind.Start, X = 40, Y = 60 }]
    };

    internal static string Compile(VisualScript graph, Dictionary<string, int>? lines = null)
    {
        graph.ValidateStructure();
        var roots = graph.Nodes.Where(node => node.Kind == ScriptNodeKind.Start).ToArray();
        if (roots.Length != 1) throw new InvalidDataException("В схеме Lua нужен один блок «Начало».");
        var nodes = graph.Nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal) { roots[0].Id };
        bool hasContinue = graph.Nodes.Any(node => node.Lua?.Operation == "continue");
        var code = new StringBuilder();
        var comments = (graph.LuaComments ?? []).ToLookup(placement => (placement.Node, placement.Part));
        var imported = (graph.LuaComments ?? []).Select(placement => placement.Id).ToHashSet();
        foreach (var note in graph.Nodes.Where(node => node.Kind == ScriptNodeKind.Comment && !imported.Contains(node.Id)))
            code.Append(LuaCommentPlacement.Encode(note.Text, inline: false)).Append('\n');
        Emit(roots[0].Next, null);
        if (used.Count != graph.Nodes.Count(node => node.Kind != ScriptNodeKind.Comment))
            throw new InvalidDataException("Соедините все блоки Lua с началом или удалите лишние блоки.");
        return code.Append(WithComments("", "trailing", graph.LuaTrailingSource ?? "")).ToString();

        string WithComments(string id, string part, string text) =>
            LuaCommentPlacement.Apply(text, comments[(id, part)], nodes);

        void Emit(string? id, string? continueLabel, bool allowElseIf = false)
        {
            for (; id != null; id = nodes[id].Next)
            {
                if (!nodes.TryGetValue(id, out var node) || node.Lua is not { } part)
                    throw new InvalidDataException("Связь Visual Lua ведёт к отсутствующему или недопустимому блоку.");
                if (!used.Add(id)) throw new InvalidDataException("Ветви Visual Lua должны иметь собственные блоки. Используйте порт «Далее» для общего продолжения.");
                if (part.ElseIf && !allowElseIf) throw new InvalidDataException("Ветка elseif должна подключаться к порту «Иначе» предыдущего условия.");
                allowElseIf = false;
                string statement = WithComments(id, "code", part.Code);
                if (lines != null)
                {
                    int line = LuaSource.FirstExecutableLine(statement) ?? 1;
                    for (int i = 0; i < code.Length; i++) if (code[i] == '\n') line++;
                    lines[id] = line;
                }
                var shape = VisualLuaCatalog.Blocks[part.Operation].Shape;
                if (shape == LuaBlockShape.Continue)
                {
                    if (continueLabel == null) throw new InvalidDataException("Блок «Следующая итерация» должен находиться внутри цикла.");
                    code.Append("\ngoto ").Append(continueLabel).Append('\n');
                    continue;
                }
                code.Append(statement);
                if (!VisualLuaCatalog.Blocks[part.Operation].HasBody) continue;
                if (shape == LuaBlockShape.Loop && hasContinue)
                {
                    string label = "__kne_continue_" + used.Count;
                    code.Append("\ndo\n");
                    Emit(node.True, label);
                    code.Append("\nend\n::").Append(label).Append("::\n");
                }
                else Emit(node.True, shape == LuaBlockShape.Function ? null : continueLabel);
                if (shape == LuaBlockShape.If && (node.False != null || part.Otherwise.Length > 0))
                {
                    bool chained = part.Otherwise.Length == 0 && node.False != null &&
                        nodes.TryGetValue(node.False, out var alternative) && alternative.Lua?.ElseIf == true;
                    if (!chained) code.Append(part.Otherwise.Length > 0 ? WithComments(id, "else", part.Otherwise) : "\nelse\n");
                    Emit(node.False, continueLabel, chained);
                }
                code.Append(WithComments(id, "close", part.Close));
            }
        }
    }

    private sealed class Reader(string source)
    {
        private readonly LuaToken[] tokens = LuaSyntax.Parse(source).Where(token => token.Kind != LuaTokenKind.Comment).ToArray();
        private readonly VisualScript graph = EmptyGraph();
        private int index, depth, row;
        private int End => index == 0 ? 0 : tokens[index - 1].End;
        private string Current => At(index);
        private string At(int position) => position < tokens.Length ? source[tokens[position].Start..tokens[position].End] : "";

        internal VisualScript Read()
        {
            int cursor = 0;
            graph.Nodes[0].Next = Block(ref cursor);
            if (Current.Length != 0) throw new InvalidDataException("Неожиданный конец блока Lua.");
            graph.LuaTrailingSource = source[cursor..];
            return graph;
        }

        private string? Block(ref int cursor)
        {
            if (++depth > 120) throw new InvalidDataException("Слишком глубокая вложенность Lua.");
            ScriptNode? first = null, previous = null;
            while (Current is not ("" or "end" or "else" or "elseif" or "until"))
            {
                var next = Statement(ref cursor);
                if (previous != null) previous.Next = next.Id;
                first ??= next;
                previous = next;
            }
            depth--;
            return first?.Id;
        }

        private ScriptNode Statement(ref int cursor)
        {
            int initial = index;
            int start = cursor;
            string head = Current;
            string? operation = null;
            switch (head)
            {
                case "if": index++; Expr(); Need("then"); return Body("if", ref cursor);
                case "while": index++; Expr(); Need("do"); return Body("while", ref cursor);
                case "do": index++; return Body("sequence", ref cursor);
                case "for":
                    index++; Name();
                    if (Take("=")) { Expressions(); operation = "for_numeric"; }
                    else
                    {
                        while (Take(",")) Name();
                        Need("in"); operation = Current is "pairs" or "ipairs" && At(index + 1) == "(" ? Current : "for_in";
                        Expressions();
                    }
                    Need("do");
                    return Body(operation, ref cursor);
                case "function":
                    index++; Name(); while (Take(".")) Name(); if (Take(":")) Name();
                    Parameters();
                    return Body("function", ref cursor);
                case "local":
                    index++;
                    if (Take("function")) { Name(); Parameters(); return Body("function", ref cursor); }
                    int count = 0;
                    do { Name(); count++; if (Take("<")) { Name(); Need(">"); } } while (Take(","));
                    if (Take("="))
                    {
                        int value = index;
                        Expressions();
                        operation = count == 1 ? ValueOperation(value, index, "local") : "multi";
                        if (count == 1 && At(value) == "function" && At(index - 1) == "end")
                        {
                            int after = index;
                            index = value + 1; Parameters();
                            if (FunctionEnd(index) == after)
                                return Body("anonymous", ref cursor);
                            index = after;
                        }
                    }
                    else operation = count == 1 ? "local" : "multi";
                    break;
                case "return":
                    index++;
                    if (Current is not ("" or ";" or "end" or "else" or "elseif" or "until")) Expressions();
                    operation = "return"; break;
                case "break": index++; operation = "break"; break;
                case "goto": index++; if (Current == "(") SkipBalanced(); else Name(); operation = "raw"; break;
                case "::": index++; Name(); Need("::"); operation = "raw"; break;
                case ";": index++; operation = "raw"; break;
                case "repeat":
                    index++; SkipBlock(); Need("until"); Expr(); operation = "raw"; break;
                default:
                    Prefix();
                    if (Current is "," or "=")
                    {
                        bool multiple = Current == ",";
                        while (Take(",")) Prefix();
                        Need("="); int value = index; Expressions();
                        operation = multiple ? "multi" : ValueOperation(value, index, "assign");
                    }
                    else operation = ValueOperation(initial, index, "call");
                    break;
            }
            var node = Add(operation, source[start..End]);
            cursor = End;
            return node;
        }

        private ScriptNode Body(string operation, ref int cursor, int conditions = 0)
        {
            if (conditions > 120) throw new InvalidDataException("Слишком много веток elseif.");
            int headerRow = row++;
            string header = source[cursor..End];
            cursor = End;
            string? body = Block(ref cursor), alternative = null;
            string otherwise = "";
            if (operation == "if" && Take("elseif"))
            {
                Expr(); Need("then");
                alternative = Body("if", ref cursor, conditions + 1).Id;
            }
            else if (operation == "if" && Take("else"))
            {
                otherwise = source[cursor..End]; cursor = End; alternative = Block(ref cursor);
            }
            if (conditions == 0) Need("end");
            var node = Add(operation, header, headerRow);
            node.Lua = node.Lua! with { Otherwise = otherwise, Close = source[cursor..End], ElseIf = conditions > 0 };
            node.True = body; node.False = alternative; cursor = End;
            return node;
        }

        private int FunctionEnd(int bodyIndex)
        {
            int previous = index;
            index = bodyIndex;
            SkipBlock(); Need("end");
            int after = index;
            index = previous;
            return after;
        }

        private void SkipBlock()
        {
            int count = graph.Nodes.Count, cursor = End, savedRow = row;
            Block(ref cursor);
            graph.Nodes.RemoveRange(count, graph.Nodes.Count - count);
            row = savedRow;
        }

        private ScriptNode Add(string operation, string code, int? layoutRow = null)
        {
            if (graph.Nodes.Count >= 500) throw new InvalidDataException("Слишком много блоков Lua.");
            var node = VisualLuaCatalog.Create(operation);
            node.Lua = new() { Operation = operation, Code = code };
            node.X = 40 + 315 * depth; node.Y = 60 + 146 * (layoutRow ?? row++);
            graph.Nodes.Add(node);
            return node;
        }

        private string ValueOperation(int start, int end, string fallback)
        {
            string first = At(start);
            if (start >= end) return fallback;
            string symbol = At(start + 1) == "." ? first + "." + At(start + 2) : first;
            if (VisualLuaCatalog.Symbols.TryGetValue(symbol, out var known)) return known;
            if (first == "kne_async" && Enumerable.Range(start + 1, end - start - 1).Any(i => At(i) == "call_scene"))
                return "krl_async";
            if (first == "_ENV") return At(start + 1) == "[" ? "global" : "get_env";
            if (first == "{") return Enumerable.Range(start, end - start).Any(i => At(i) == "=") ? "table" : "array";
            if (first == "...") return "varargs";
            if (first is "true" or "false") return "boolean";
            if (first == "nil") return "nil";
            if (first is "-" or "#" or "~" or "not") return first switch { "-" => "negative", "#" => "length", "~" => "bitwise", _ => "logical" };
            for (int i = start + 1; i < end; i++)
            {
                string token = At(i);
                if (token == "[") return "index";
                if (Priority(token) > 0)
                    return token switch
                    {
                        "and" or "or" => "logical", "==" or "~=" or "<" or ">" or "<=" or ">=" => "comparison",
                        ".." => "concat", "|" or "~" or "&" or "<<" or ">>" => "bitwise", _ => "math"
                    };
            }
            if (tokens[start].Kind == LuaTokenKind.Number) return "number";
            if (tokens[start].Kind == LuaTokenKind.String) return "string";
            return fallback;
        }

        private void Expressions() { do { Expr(); } while (Take(",")); }

        private void Expr(int min = 1, int recursion = 0)
        {
            if (recursion > 120) throw new InvalidDataException("Слишком глубокое выражение Lua.");
            if (Current is "-" or "~" or "#" or "not") { index++; Expr(11, recursion + 1); }
            else Atom();
            while (Priority(Current) >= min)
            {
                string op = Current; int precedence = Priority(op); index++;
                Expr(op is "^" or ".." ? precedence : precedence + 1, recursion + 1);
            }
        }

        private void Atom()
        {
            if (index >= tokens.Length) throw new InvalidDataException("Ожидается выражение Lua.");
            if (Current == "function") { index++; Parameters(); SkipBlock(); Need("end"); }
            else if (Current == "{") SkipBalanced();
            else if (tokens[index].Kind is LuaTokenKind.Number or LuaTokenKind.String || Current is "nil" or "true" or "false" or "...") index++;
            else Prefix();
        }

        private void Prefix()
        {
            if (Current == "(") SkipBalanced(); else Name();
            while (true)
            {
                if (Take(".")) Name();
                else if (Current is "[" or "(" or "{") SkipBalanced();
                else if (index < tokens.Length && tokens[index].Kind == LuaTokenKind.String) index++;
                else if (Take(":")) Name();
                else break;
            }
        }

        private void Parameters() { if (Current != "(") throw new InvalidDataException("Ожидаются параметры Lua."); SkipBalanced(); }
        private void SkipBalanced()
        {
            if (index >= tokens.Length || tokens[index].Partner is not int finish || finish <= tokens[index].Start)
                throw new InvalidDataException("Незакрытая скобка Lua.");
            while (index < tokens.Length && tokens[index].Start <= finish) index++;
        }
        private void Name()
        {
            if (!LuaSource.IsIdentifier(Current)) throw new InvalidDataException("Ожидается имя Lua.");
            index++;
        }
        private bool Take(string token) { if (Current != token) return false; index++; return true; }
        private void Need(string token) { if (!Take(token)) throw new InvalidDataException("Ожидается " + token); }
        private static int Priority(string token) => token switch
        {
            "or" => 1, "and" => 2, "<" or ">" or "<=" or ">=" or "~=" or "==" => 3, "|" => 4, "~" => 5,
            "&" => 6, "<<" or ">>" => 7, ".." => 8, "+" or "-" => 9, "*" or "/" or "//" or "%" => 10, "^" => 12, _ => 0
        };
    }
}
