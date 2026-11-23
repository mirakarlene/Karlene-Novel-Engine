using System.IO;

namespace KNE;

public static partial class ProjectAnalyzer
{
    private static readonly IReadOnlyDictionary<string, SystemBlock> SystemFunctions =
        SystemBlocks.All.Values.ToDictionary(block => block.Function, StringComparer.Ordinal);

    private static void AnalyzeSystemUse(SystemBlock? block, string path, int? line, string? id, List<ProjectDiagnostic> findings)
    {
        if (block?.Function == "enable_console")
            findings.Add(new(ProjectDiagnosticSeverity.Information, "KNE601", path, line,
                "Теоретическая угроза безопасности. Сценарий включает игровую консоль независимо от параметров релиза.", id));
        else if (block?.Function is "open_browser" or "clipboard_write" or "clipboard_read" or "take_screenshot" or
                 "read_text_file" or "write_text_file" or "path_exists" or "file_size" or "list_files" or "create_folder")
            findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE602", path, line,
                $"«{block.Title}» обращается к браузеру, буферу обмена или файловой системе устройства. Проверьте разрешения проекта и необходимые пути.", id));
    }

    private static void AnalyzeSceneReachability(EntryPoints? entries, List<Function> functions, List<Source> sources,
        Dictionary<string, (VisualScript Graph, HashSet<string> Reachable)> graphs, List<ProjectDiagnostic> findings, CancellationToken token)
    {
        var byName = functions.ToLookup(function => function.Name, StringComparer.Ordinal);
        var byFile = functions.ToLookup(function => function.Path, StringComparer.OrdinalIgnoreCase);
        var entryFiles = ProjectLifecycle.Paths(entries).Concat(new[] { entries?.MainScene, entries?.InitScript }.OfType<string>())
            .Select(NormalizeReferencePath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var roots = functions.Where(function => function.Name == "main" || entryFiles.Contains(function.Path)).ToHashSet();
        var links = new Dictionary<Function, HashSet<Function>>();
        void Link(Function? from, IEnumerable<Function> targets)
        {
            if (from == null) { roots.UnionWith(targets); return; }
            if (!links.TryGetValue(from, out var destinations)) links.Add(from, destinations = []);
            destinations.UnionWith(targets);
        }

        foreach (var (path, graph) in graphs)
        {
            token.ThrowIfCancellationRequested();
            var owner = byFile[path].Single();
            foreach (var block in graph.Graph.Nodes.Where(block => graph.Reachable.Contains(block.Id)))
            {
                if (block.Kind is ScriptNodeKind.CallScene or ScriptNodeKind.GotoScene or ScriptNodeKind.ConditionalGoto)
                    Link(owner, byName[block.Path]);
                else if (block.Kind is ScriptNodeKind.Subsystem or ScriptNodeKind.Script or ScriptNodeKind.IncludeLua or ScriptNodeKind.CallVisualLua)
                    Link(owner, byFile[NormalizeReferencePath(block.Path)]);
            }
        }

        foreach (var source in sources)
        {
            token.ThrowIfCancellationRequested();
            var declarations = LuaSource.Functions(source.Text);
            var owners = byFile[source.Path].ToLookup(function => (function.Name, function.Line));
            var navigation = SceneNavigationSource.Read(source.Text, declarations);
            // A computed dispatch may reach any scene. A static advisory must not claim it is unused.
            if (navigation.Dynamic > 0 || source.Tokens.Any(token => token.Text is "_G" or "_ENV" or "load" or "dofile" or "loadfile")) return;
            foreach (var reference in navigation.References)
            {
                var destinations = reference.Path ? byFile[NormalizeReferencePath(reference.Target)] : byName[reference.Target];
                if (!reference.Required && !destinations.Any() && reference.Kind is "call" or "call_scene" or "goto" or "goto_scene" or "jump") return;
                var owner = reference.Owner is int index ? declarations[index] : null;
                Link(owner == null ? null : owners[(owner.Name, owner.Line)].FirstOrDefault(), destinations);
            }
        }

        var reachable = new HashSet<Function>();
        var pending = new Stack<Function>(roots);
        while (pending.TryPop(out var function))
            if (reachable.Add(function) && links.TryGetValue(function, out var destinations))
                foreach (var target in destinations) pending.Push(target);

        foreach (var function in functions.Where(function => !reachable.Contains(function) &&
                     (ScriptDocument.IsVisualPath(function.Path) || Path.GetExtension(function.Path).Equals(".krl", StringComparison.OrdinalIgnoreCase))))
            findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE307", function.Path, function.Line,
                $"Сцена {function.Name}() недостижима из точек входа проекта."));
    }

    private static string NormalizeReferencePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }

    private sealed class VariableAnalysis
    {
        private readonly Dictionary<string, ProjectDiagnostic> assignments = new(StringComparer.Ordinal);
        private readonly HashSet<string> reads = new(StringComparer.Ordinal);
        private bool dynamic;

        internal void Read(IReadOnlyList<Lexeme> tokens, string path, bool graph = false, bool generatedCode = false)
        {
            if (dynamic) return;
            string? block = null;
            for (int i = 0; i < tokens.Count; i++)
            {
                string text = tokens[i].Text;
                if (graph && text == "__kne_node" && i + 2 < tokens.Count && tokens[i + 1].Text == "==")
                    block = Literal(tokens[i + 2]);
                if (text is "copy_variable" or "reset_variable" or "save_read" && IsGlobalCall(tokens, i))
                {
                    var args = LiteralArguments(tokens, i + 1);
                    if (text != "save_read" && args.FirstOrDefault() is { } target) Assign(target, tokens[i].Line);
                    if (text != "reset_variable" && args.ElementAtOrDefault(1) is { } source) reads.Add(source);
                }
                if (text != "state" || i > 0 && tokens[i - 1].Text is "." or ":") continue;
                int next = i + 3;
                string? name = null;
                if (next <= tokens.Count && tokens[i + 1].Text == "." && LuaSource.IsIdentifier(tokens[i + 2].Text))
                    name = tokens[i + 2].Text;
                else if (next < tokens.Count && tokens[i + 1].Text == "[" && tokens[next].Text == "]")
                {
                    name = Literal(tokens[i + 2]);
                    next++;
                }
                if (name == null) { dynamic = true; return; }
                if (next < tokens.Count && tokens[next].Text == "=") Assign(name, tokens[i].Line);
                else reads.Add(name);
            }

            void Assign(string name, int line) => assignments.TryAdd(name, new(ProjectDiagnosticSeverity.Warning,
                "KNE308", path, graph || generatedCode ? null : line, $"Значение переменной state.{name} записывается, но нигде явно не читается.", block));
        }

        internal void AddFindings(HashSet<string> inlineReads, List<ProjectDiagnostic> findings)
        {
            if (dynamic) return;
            reads.UnionWith(inlineReads);
            foreach (var (name, finding) in assignments)
                if (!reads.Contains(name)) findings.Add(finding);
        }
    }
}
