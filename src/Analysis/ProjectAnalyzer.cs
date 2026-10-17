using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace KNE;

public enum ProjectDiagnosticSeverity { Warning, Error }

public sealed record ProjectDiagnostic(ProjectDiagnosticSeverity Severity, string Code, string File, int? Line,
    string Message, string? BlockId = null, FrameIdentity? Frame = null);

public sealed record ProjectAnalysisResult(string ProjectName, DateTimeOffset OccurredAt, IReadOnlyList<ProjectDiagnostic> Findings)
{
    public int ErrorCount => Findings.Count(f => f.Severity == ProjectDiagnosticSeverity.Error);
    public int WarningCount => Findings.Count(f => f.Severity == ProjectDiagnosticSeverity.Warning);

    public string ToText()
    {
        var text = new StringBuilder("Анализ проекта KNE\nПроект: ").Append(ProjectName)
            .Append("\nВремя: ").Append(OccurredAt.ToString("O", CultureInfo.InvariantCulture))
            .Append("\nОшибок: ").Append(ErrorCount).Append("; предупреждений: ").Append(WarningCount)
            .Append("\n\nСценарии не запускались. Предупреждения — статические эвристики, а не доказательство ошибки.\n")
            .Append("Динамические пути, вычисляемые переходы и значения переменных требуют проверки в игре.\n\n");
        foreach (var item in Findings)
            text.Append(item.Severity == ProjectDiagnosticSeverity.Error ? "ОШИБКА " : "ПРЕДУПРЕЖДЕНИЕ ")
                .Append(item.Code).Append(" | ").Append(item.File)
                .Append(item.Line is { } number ? ":" + number.ToString(CultureInfo.InvariantCulture) : "")
                .Append(item.BlockId is { } block ? " | блок " + block : "")
                .Append(item.Frame is { } frame ? " | фрейм сцены " + frame.SceneFrameId + ", игры " + frame.GlobalFrameId : "")
                .Append(" | ").AppendLine(item.Message);
        if (Findings.Count == 0) text.AppendLine("Статические проверки не обнаружили проблем.");
        return text.ToString();
    }
}

/// <summary>Best-effort diagnostics over saved project sources; project code is never executed.</summary>
public static class ProjectAnalyzer
{
    private const long SourceLimit = 8 * 1024 * 1024;
    private sealed record Source(string Path, string Text, List<Lexeme> Tokens);
    private sealed record Lexeme(LuaToken Token, string Text, int Line);
    private sealed record Function(string Name, string Path, int Line, IReadOnlyList<Lexeme> Body);
    private sealed record Reference(string Name, string Path, int Line, string? Block = null, bool Identifier = false);

    public static Task<ProjectAnalysisResult> AnalyzeAsync(string projectRoot, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        return Task.Run(() => AnalyzeCoreAsync(projectRoot, cancellationToken), cancellationToken);
    }

    private static async Task<ProjectAnalysisResult> AnalyzeCoreAsync(string projectRoot, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        string root = PathSafety.NormalizeDirectory(projectRoot);
        PathSafety.RejectLinks(root);
        var findings = new List<ProjectDiagnostic>();
        var functions = new List<Function>();
        var references = new List<Reference>();
        var sources = new List<Source>();
        string projectName = Path.GetFileName(root);
        void Add(string code, string path, int? line, string message, ProjectDiagnosticSeverity severity = ProjectDiagnosticSeverity.Error, string? block = null)
            => findings.Add(new(severity, code, path, line, message, block));

        var files = ProjectLoader.EnumerateFiles(root, token);
        foreach (string warning in files.Warnings) Add("KNE001", "project.json", null, warning, ProjectDiagnosticSeverity.Warning);
        string configPath = PathSafety.ResolveRelative(root, "project.json");
        try
        {
            string config = await File.ReadAllTextAsync(configPath, token).ConfigureAwait(false);
            var configuration = JsonSerializer.Deserialize<ProjectConfiguration>(config, ProjectConfiguration.JsonOptions)
                ?? throw new InvalidDataException("project.json не должен содержать null.");
            projectName = configuration.DisplayName(projectName);
            foreach (string? entry in new[] { configuration.EntryPoints?.InitScript, configuration.EntryPoints?.MainScene })
                if (!string.IsNullOrWhiteSpace(entry))
                    try { if (!File.Exists(PathSafety.ResolveRelative(root, entry))) Add("KNE002", "project.json", null, "Не найден файл entry_points: " + entry); }
                    catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException) { Add("KNE002", "project.json", null, error.Message); }
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException)
        { Add("KNE003", "project.json", JsonLine(error), error.Message); }

        foreach (string file in files.Files)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.GetRelativePath(root, file).Replace('\\', '/');
            bool code = ScriptDocument.IsCodePath(file), graph = ScriptDocument.IsVisualPath(file);
            bool config = path.StartsWith("engine/", StringComparison.OrdinalIgnoreCase) && Path.GetExtension(file).Equals(".json", StringComparison.OrdinalIgnoreCase);
            if (!code && !graph && !config) continue;
            try
            {
                if (new FileInfo(file).Length > SourceLimit) { Add("KNE004", path, null, "Файл превышает допустимые 8 МБ."); continue; }
                string text = await File.ReadAllTextAsync(file, token).ConfigureAwait(false);
                if (config)
                {
                    using var json = JsonDocument.Parse(text);
                    if (json.RootElement.ValueKind is not JsonValueKind.Object) Add("KNE005", path, 1, "Конфигурация должна содержать JSON-объект.");
                }
                else if (graph)
                {
                    var visual = VisualScript.Parse(text);
                    functions.Add(new(visual.FunctionName, path, 1, []));
                    foreach (var node in visual.Nodes)
                    {
                        if (node.Kind is ScriptNodeKind.Background or ScriptNodeKind.Character or ScriptNodeKind.Music or ScriptNodeKind.Sound or ScriptNodeKind.Voice or ScriptNodeKind.IncludeLua or ScriptNodeKind.IncludeCs or ScriptNodeKind.Script && !string.IsNullOrWhiteSpace(node.Path))
                            CheckResource(root, path, null, node.Path, findings, node.Id);
                        if (node.Ui?.Texture is { Length: > 0 } texture) CheckResource(root, path, null, texture, findings, node.Id);
                        if (node.Kind is ScriptNodeKind.CallScene or ScriptNodeKind.GotoScene)
                            references.Add(new(node.Path, path, 1, node.Id));
                    }
                    AnalyzeGraph(visual, path, findings);
                    try { visual.Compile(); }
                    catch (InvalidDataException error) { Add("KNE101", path, null, error.Message); }
                }
                else
                {
                    string prepared;
                    try { prepared = LuaSource.Prepare(StartupScripts.Declaration(text).Source); }
                    catch (InvalidDataException error) { Add("KNE102", path, LuaSource.FirstExecutableLine(text), error.Message); prepared = LuaSource.Prepare(text); }
                    var source = new Source(path, text, Lex(text));
                    sources.Add(new(path, prepared, source.Tokens));
                    var declarations = LuaSource.Functions(text);
                    foreach (var declaration in declarations)
                    {
                        var body = FunctionBody(source, declaration);
                        functions.Add(new(declaration.Name, path, declaration.Line, body));
                        if (body.Count == 0) Add("KNE301", path, declaration.Line, "Пустая функция " + declaration.Name + "().", ProjectDiagnosticSeverity.Warning);
                    }
                    AnalyzeCalls(root, source, references, findings);
                    AnalyzeLoops(source, findings);
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
            { Add("KNE006", path, JsonLine(error), error.Message); }
        }

        await using (var syntax = new SyntaxCheckSession())
            foreach (var source in sources)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var result = await syntax.CheckAsync(source.Text, token).ConfigureAwait(false);
                    if (result.Error != null) Add("KNE100", source.Path, result.Line, result.Error);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
                { Add("KNE007", source.Path, null, "Проверка синтаксиса не завершена: " + error.Message); }
            }
        var mains = functions.Where(f => f.Name == "main").ToArray();
        if (mains.Length == 0) Add("KNE200", "project.json", null, "Нужна ровно одна глобальная функция main(); ни одной не найдено.");
        foreach (var group in functions.GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() > 1))
            foreach (var declaration in group)
                Add(group.Key == "main" ? "KNE201" : "KNE202", declaration.Path, declaration.Line,
                    "Повторное глобальное имя функции " + group.Key + "(): " + string.Join(", ", group.Select(f => f.Path + ":" + f.Line)));
        var names = functions.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var assigned = sources.SelectMany(s => s.Tokens.Select((item, index) => (item, index, s.Tokens)))
            .Where(p => p.index + 1 < p.Tokens.Count && p.Tokens[p.index + 1].Text == "=" && LuaSource.IsIdentifier(p.item.Text))
            .Select(p => p.item.Text).ToHashSet(StringComparer.Ordinal);
        foreach (var source in sources)
            assigned.UnionWith(ParameterNames(source.Tokens));
        foreach (var reference in references)
            if (!names.Contains(reference.Name) && !(reference.Identifier && assigned.Contains(reference.Name)))
                Add("KNE203", reference.Path, reference.Line, "Не найдена глобальная функция перехода: " + reference.Name + "().", block: reference.Block);
        AnalyzeRecursion(functions, findings);
        try
        {
            token.ThrowIfCancellationRequested();
            var frames = FrameCatalog.Load(new ProjectInfo { Path = root, Configuration = new() });
            for (int i = 0; i < findings.Count; i++)
            {
                var finding = findings[i];
                FrameIdentity? frame = finding.BlockId is { } node
                    ? frames.Entries.FirstOrDefault(e => e.File.Equals(finding.File, StringComparison.OrdinalIgnoreCase) && e.NodeId == node)?.Identity
                    : frames.Resolve(finding.File, finding.Line);
                findings[i] = finding with { Frame = frame };
            }
        }
        catch (Exception error) when (error is InvalidDataException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Invalid source documents already have their own diagnostics. A broken ID registry is a separate issue.
            if (error.Message.Contains("frame-ids.json", StringComparison.OrdinalIgnoreCase))
                Add("KNE008", "engine/frame-ids.json", JsonLine(error), error.Message);
        }
        return new(projectName, DateTimeOffset.Now, findings.OrderBy(f => f.File, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.Line ?? 0).ThenBy(f => f.Code, StringComparer.Ordinal).ToArray());
    }

    public static async Task<string> SaveReportAsync(string projectRoot, ProjectAnalysisResult result, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        cancellationToken.ThrowIfCancellationRequested();
        string root = PathSafety.NormalizeDirectory(projectRoot);
        string directory = PathSafety.ResolveRelative(root, "reports");
        Directory.CreateDirectory(directory);
        PathSafety.RejectLinks(directory);
        string name = "project-analysis-" + result.OccurredAt.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N") + ".txt";
        string target = PathSafety.ResolveRelative(root, "reports/" + name);
        string temporary = PathSafety.ResolveRelative(root, "reports/.analysis-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await File.WriteAllTextAsync(temporary, result.ToText(), new UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target);
            return target;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static int? JsonLine(Exception error) => error is JsonException { LineNumber: { } line } ? checked((int)line + 1)
        : error.InnerException is { } inner ? JsonLine(inner) : null;

    private static List<Lexeme> Lex(string text)
    {
        var lines = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++)
            if (text[i] == '\n' || text[i] == '\r' && (i + 1 >= text.Length || text[i + 1] != '\n')) lines.Add(i + 1);
        return LuaSyntax.Parse(text).Where(t => t.Kind != LuaTokenKind.Comment).Select(t =>
        {
            int line = lines.BinarySearch(t.Start);
            return new Lexeme(t, text.Substring(t.Start, t.Length), line >= 0 ? line + 1 : ~line);
        }).ToList();
    }

    private static bool IsGlobalCall(IReadOnlyList<Lexeme> tokens, int index) => index + 1 < tokens.Count && tokens[index + 1].Text == "(" &&
        (index == 0 || tokens[index - 1].Text is not ("function" or "." or ":"));

    private static void AnalyzeCalls(string root, Source source, List<Reference> references, List<ProjectDiagnostic> findings)
    {
        var tokens = source.Tokens;
        var shadowed = ParameterNames(tokens);
        for (int i = 0; i < tokens.Count; i++)
            if (i > 0 && tokens[i - 1].Text is "local" or "function" || i + 1 < tokens.Count && tokens[i + 1].Text == "=")
                shadowed.Add(tokens[i].Text);
        for (int i = 0; i + 2 < tokens.Count; i++)
        {
            if (!IsGlobalCall(tokens, i) || shadowed.Contains(tokens[i].Text)) continue;
            string name = tokens[i].Text;
            AnalyzeNilArguments(source, i, findings);
            int argument = i + 2;
            bool simple = argument + 1 < tokens.Count && tokens[argument + 1].Text is "," or ")";
            if (!simple) continue;
            if (name == "play_audio" && tokens[argument + 1].Text == "," && argument + 3 < tokens.Count && tokens[argument + 3].Text is "," or ")")
            {
                string? audio = Literal(tokens[argument + 2]);
                if (!string.IsNullOrEmpty(audio)) CheckResource(root, source.Path, tokens[i].Line, audio, findings);
            }
            if (name is "call" or "call_scene" or "goto" or "goto_scene" or "jump")
            {
                string? target = Literal(tokens[argument]);
                if (target == null && LuaSource.IsIdentifier(tokens[argument].Text)) target = tokens[argument].Text;
                if (target != null) references.Add(new(target, source.Path, tokens[i].Line, Identifier: tokens[argument].Token.Kind != LuaTokenKind.String));
            }
            if (name is "background" or "show_character" or "play_music" or "play_sound" or "include_lua" or "include_cs" or "run_script")
            {
                string? resource = Literal(tokens[argument]);
                if (resource != null && resource.Length > 0) CheckResource(root, source.Path, tokens[i].Line, resource, findings);
            }
        }
    }

    private static void AnalyzeNilArguments(Source source, int call, List<ProjectDiagnostic> findings)
    {
        var tokens = source.Tokens;
        int[] required = tokens[call].Text switch
        {
            "d" or "choice" or "kne_say" => [1], "set_window_size" => [0, 1],
            "register_character" or "text_pause" or "kne_random_branch" => [0],
            "show_character" or "play_music" or "play_sound" or "call" or "call_scene" or "goto" or "goto_scene" or
                "set_window_title" or "set_bg_color" or "show_title" or "show_screen" or "stop_audio" or
                "set_master_volume" or "kne_pause" or "kne_timer" or "kne_parallel" or "kne_async" or "kne_join" or "kne_wait_condition" or "kne_end" => [0],
            "play_audio" or "set_audio_channel" => [0, 1], "move_visual" or "animate_visual" or "camera" => [0, 1, 2, 3],
            "screen_shake" or "screen_flash" => [0, 1], _ => []
        };
        if (required.Length == 0) return;
        int start = call + 2, depth = 0, argument = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (depth == 0 && text is "," or ")")
            {
                if (required.Contains(argument) && i == start + 1 && tokens[start].Text == "nil")
                    findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE303", source.Path, tokens[call].Line,
                        "Явный nil в обязательном аргументе " + (argument + 1) + " функции " + tokens[call].Text + "()."));
                if (text == ")") break;
                start = i + 1; argument++;
            }
            else if (tokens[i].Token.Kind is LuaTokenKind.Bracket or LuaTokenKind.InvalidBracket)
            {
                if (text is "(" or "[" or "{") depth++;
                else if (text is ")" or "]" or "}") depth--;
            }
        }
    }

    private static HashSet<string> ParameterNames(IReadOnlyList<Lexeme> tokens)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Text != "function" || tokens[i].Token.Kind == LuaTokenKind.String) continue;
            int start = i + 1;
            while (start < tokens.Count && tokens[start].Text is not ("(" or "end")) start++;
            if (start >= tokens.Count || tokens[start].Text != "(") continue;
            for (int p = start + 1; p < tokens.Count && tokens[p].Text != ")"; p++)
                if (LuaSource.IsIdentifier(tokens[p].Text)) names.Add(tokens[p].Text);
        }
        return names;
    }

    private static void CheckResource(string root, string path, int? line, string resource, List<ProjectDiagnostic> findings, string? block = null)
    {
        try
        {
            string full = PathSafety.ResolveRelative(root, resource);
            if (!File.Exists(full)) findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE302", path, line, "Не найден ресурс с буквальным путём: " + resource, block));
        }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
        { findings.Add(new(ProjectDiagnosticSeverity.Error, "KNE204", path, line, "Недопустимый путь ресурса «" + resource + "»: " + error.Message, block)); }
    }

    private static void AnalyzeGraph(VisualScript graph, string path, List<ProjectDiagnostic> findings)
    {
        var map = graph.Nodes.ToDictionary(n => n.Id, StringComparer.Ordinal);
        foreach (var node in graph.Nodes)
            foreach (var (_, target) in node.Outputs())
                if ((target == null && node.Kind != ScriptNodeKind.Comment) || (target != null && !map.ContainsKey(target)))
                    findings.Add(new(ProjectDiagnosticSeverity.Error, "KNE103", path, null, "Выход блока не соединён с существующим блоком: " + (target ?? "(нет цели)"), node.Id));
        var starts = graph.Nodes.Where(n => n.Kind == ScriptNodeKind.Start).ToArray();
        if (starts.Length != 1) return;
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>(); pending.Push(starts[0].Id);
        while (pending.TryPop(out string? id))
            if (visited.Add(id))
                foreach (var (_, next) in map[id].Outputs())
                    if (next != null && map.ContainsKey(next)) pending.Push(next);
        foreach (var node in graph.Nodes.Where(n => n.Kind != ScriptNodeKind.Comment && !visited.Contains(n.Id)))
            findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE304", path, null, "Блок недостижим из «Начала».", node.Id));
    }

    private static string? Literal(Lexeme token)
    {
        if (token.Token.Kind != LuaTokenKind.String || token.Text.Length < 2) return null;
        string text = token.Text;
        if (text[0] == '[')
        {
            int opening = text.IndexOf('[', 1);
            if (opening < 0 || text.Length < (opening + 1) * 2) return null;
            string body = text[(opening + 1)..^(opening + 1)];
            return body.StartsWith("\r\n", StringComparison.Ordinal) ? body[2..] : body.StartsWith('\n') || body.StartsWith('\r') ? body[1..] : body;
        }
        if (text[^1] != text[0]) return null;
        var result = new StringBuilder();
        for (int i = 1; i < text.Length - 1; i++)
        {
            if (text[i] != '\\') { result.Append(text[i]); continue; }
            if (++i >= text.Length - 1) return null;
            char c = text[i];
            if (c is '\\' or '\'' or '"') result.Append(c);
            else if (c is 'n' or 'r' or 't') result.Append(c == 'n' ? '\n' : c == 'r' ? '\r' : '\t');
            else return null; // An unrecognized escape is not a proven literal path.
        }
        return result.ToString();
    }

    private static IReadOnlyList<Lexeme> FunctionBody(Source source, LuaFunctionDeclaration declaration)
    {
        var tokens = source.Tokens;
        int name = tokens.FindIndex(t => t.Line == declaration.Line && t.Text == declaration.Name);
        if (name < 0) return [];
        int opening = name + 1;
        while (opening < tokens.Count && tokens[opening].Text != "(") opening++;
        if (opening == tokens.Count || tokens[opening].Token.Partner is not { } closingPosition) return [];
        int closing = tokens.FindIndex(opening, t => t.Token.Start == closingPosition);
        if (closing < 0) return [];
        int end = BlockEnd(tokens, closing + 1);
        return end < 0 ? tokens.Skip(closing + 1).ToArray() : tokens.Skip(closing + 1).Take(end - closing - 1).Where(t => t.Text != ";").ToArray();
    }

    // The caller starts just after the outer block's header; nested do/loop headers share a scope.
    private static int BlockEnd(IReadOnlyList<Lexeme> tokens, int start)
    {
        int depth = 1, awaitingDo = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            string text = tokens[i].Text;
            if (tokens[i].Token.Kind == LuaTokenKind.String) continue;
            if (text is "function" or "if" or "repeat") depth++;
            else if (text is "while" or "for") { depth++; awaitingDo++; }
            else if (text == "do") { if (awaitingDo > 0) awaitingDo--; else depth++; }
            else if (text is "end" or "until") { if (--depth == 0) return i; }
        }
        return -1;
    }

    private static bool HasYield(IReadOnlyList<Lexeme> tokens) => tokens.Select((token, i) => (token, i))
        .Any(pair => pair.token.Text is "d" or "choice" or "kne_say" && IsGlobalCall(tokens, pair.i));

    private static void AnalyzeLoops(Source source, List<ProjectDiagnostic> findings)
    {
        for (int i = 0; i < source.Tokens.Count; i++)
        {
            if (source.Tokens[i].Text is not ("while" or "repeat") || source.Tokens[i].Token.Kind == LuaTokenKind.String) continue;
            if (source.Tokens[i].Text == "while" && i + 2 < source.Tokens.Count && source.Tokens[i + 1].Text == "false" && source.Tokens[i + 2].Text == "do") continue;
            int start = i + 1;
            if (source.Tokens[i].Text == "while")
            {
                while (start < source.Tokens.Count && source.Tokens[start].Text != "do") start++;
                start++;
            }
            int end = BlockEnd(source.Tokens, start);
            if (end < start) continue;
            if (!HasYield(source.Tokens.Skip(start).Take(end - start).ToArray()))
                findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE401", source.Path, source.Tokens[i].Line,
                    "Цикл без явного ожидания d()/kne_say()/choice(): проверьте условие завершения и длительность вычислений; возможна задержка или зависание."));
        }
    }

    private static void AnalyzeRecursion(List<Function> functions, List<ProjectDiagnostic> findings)
    {
        var map = functions.GroupBy(f => f.Name, StringComparer.Ordinal).Where(g => g.Count() == 1)
            .Select(g => g.First()).Where(f => !HasYield(f.Body)).ToDictionary(f => f.Name, StringComparer.Ordinal);
        var edges = map.Values.ToDictionary(f => f.Name, f => f.Body.Select((token, i) => (token, i))
            .Where(p => IsGlobalCall(f.Body, p.i)).Select(p =>
                p.token.Text is "call" or "call_scene" or "goto" or "goto_scene" && p.i + 3 < f.Body.Count && f.Body[p.i + 3].Text == ")"
                    ? Literal(f.Body[p.i + 2]) ?? f.Body[p.i + 2].Text : p.token.Text)
            .Where(map.ContainsKey).Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
        foreach (var function in map.Values)
        {
            var pending = new Stack<string>(edges[function.Name]);
            var visited = new HashSet<string>(StringComparer.Ordinal);
            bool recursive = false;
            while (pending.TryPop(out string? name))
            {
                if (name == function.Name) { recursive = true; break; }
                if (visited.Add(name)) foreach (string next in edges[name]) pending.Push(next);
            }
            if (recursive) findings.Add(new(ProjectDiagnosticSeverity.Warning, "KNE402", function.Path, function.Line,
                "Возможная рекурсия без явных d()/choice() в цепочке вызовов функции " + function.Name + "(); проверьте базовое условие и риск переполнения стека."));
        }
    }
}
