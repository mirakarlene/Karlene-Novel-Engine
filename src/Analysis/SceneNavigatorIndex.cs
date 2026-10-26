using System.IO;
using System.Text.Json;

namespace KNE;

public sealed record SceneNavigationNode(string Id, string Function, string File, int Line, bool Reachable, IReadOnlyList<string> Errors);
public sealed record SceneNavigationLink(string Source, string Target, string Kind);
public sealed record SceneNavigationIndex(IReadOnlyList<SceneNavigationNode> Nodes, IReadOnlyList<SceneNavigationLink> Links, int DynamicReferences)
{
    /// <summary>Reads saved or open scene sources and compiles syntax in isolation. Never invokes project or plugin code.</summary>
    public static Task<SceneNavigationIndex> BuildAsync(ProjectInfo project, IReadOnlyDictionary<string, string>? openSources = null, CancellationToken token = default)
        => Task.Run(() => BuildCoreAsync(project, openSources, token), token);

    private sealed record Reference(string Source, string Target, string Kind, bool Path = false, bool Required = true);

    private static async Task<SceneNavigationIndex> BuildCoreAsync(ProjectInfo project, IReadOnlyDictionary<string, string>? openSources, CancellationToken token)
    {
        var nodes = new List<SceneNavigationNode>();
        var references = new List<Reference>();
        var errors = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        int dynamic = 0;
        var paths = ProjectLoader.EnumerateFiles(project.Path, token).Files.Concat(openSources?.Keys ?? [])
            .Where(path => Path.GetExtension(path).Equals(".krl", StringComparison.OrdinalIgnoreCase) || ScriptDocument.IsVisualPath(path))
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(path => path, StringComparer.OrdinalIgnoreCase);
        await using var syntax = new SyntaxCheckSession();
        foreach (string candidate in paths)
        {
            token.ThrowIfCancellationRequested();
            string full = PathSafety.EnsureInside(project.Path, candidate);
            string file = Path.GetRelativePath(project.Path, full).Replace('\\', '/');
            var fileErrors = new List<string>(); errors[file] = fileErrors;
            int first = nodes.Count;
            SceneNavigationNode Add(string name, int line)
            {
                var node = new SceneNavigationNode(file + "#" + nodes.Count, name, file, line, false, []); nodes.Add(node); return node;
            }
            try
            {
                if (File.Exists(full) && new FileInfo(full).Length > 8 * 1024 * 1024) throw new InvalidDataException("Сценарий больше 8 МБ.");
                string source = openSources?.GetValueOrDefault(full) ?? await File.ReadAllTextAsync(full, token).ConfigureAwait(false);
                if (source.Length > 8 * 1024 * 1024) throw new InvalidDataException("Сценарий больше 8 МБ.");
                if (ScriptDocument.IsVisualPath(full))
                {
                    var graph = VisualScript.Parse(source); var entry = Add(graph.FunctionName, 1);
                    foreach (var block in graph.Nodes)
                        if (block.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene or ScriptNodeKind.ConditionalGoto)
                            references.Add(new(entry.Id, block.Path, block.Title));
                        else if (block.Kind == ScriptNodeKind.Subsystem)
                            references.Add(new(entry.Id, block.Path, "Подсистема", Path: true));
                    try { graph.Compile(); }
                    catch (InvalidDataException error) { fileErrors.Add(error.Message); }
                }
                else
                {
                    var declarations = LuaSource.Functions(source);
                    var entries = declarations.Select(declaration => Add(declaration.Name, declaration.Line)).ToArray();
                    if (entries.Length == 0) entries = [Add("(нет функций)", 1)];
                    var analysis = SceneNavigationSource.Read(source, declarations);
                    dynamic += analysis.Dynamic;
                    foreach (var reference in analysis.References)
                    {
                        var owners = reference.Owner is { } ownerIndex ? new[] { entries[ownerIndex] } : entries;
                        foreach (var owner in owners)
                            references.Add(new(owner.Id, reference.Target, reference.Kind, reference.Path, reference.Required));
                    }
                    var prepared = LuaSource.Prepare(StartupScripts.Declaration(source).Source);
                    var check = await syntax.CheckAsync(prepared, token).ConfigureAwait(false);
                    if (check.Error != null) fileErrors.Add((check.Line is { } line ? "Строка " + line + ": " : "") + check.Error);
                }
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
            { fileErrors.Add(error.Message); }
            if (first == nodes.Count) Add(Path.GetFileNameWithoutExtension(file), 1);
        }
        if (!nodes.Any(node => node.Function == "main"))
        {
            string entryFile = Path.GetRelativePath(project.Path, project.MainScenePath).Replace('\\', '/');
            if (errors.TryGetValue(entryFile, out var entryErrors)) entryErrors.Add("В проекте не найдена функция main().");
        }
        foreach (var duplicate in nodes.Where(node => !node.Function.StartsWith('(')).GroupBy(node => node.Function, StringComparer.Ordinal).Where(group => group.Count() > 1))
            foreach (var node in duplicate) errors[node.File].Add("Повторное имя функции: " + duplicate.Key + "().");
        var names = nodes.GroupBy(node => node.Function, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var files = nodes.GroupBy(node => node.File, StringComparer.OrdinalIgnoreCase).ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);
        var byId = nodes.ToDictionary(node => node.Id, StringComparer.Ordinal);
        var links = new HashSet<SceneNavigationLink>();
        foreach (var reference in references)
        {
            token.ThrowIfCancellationRequested();
            string target = reference.Target;
            if (reference.Path)
            {
                try { target = Path.GetRelativePath(project.Path, PathSafety.ResolveRelative(project.Path, target)).Replace('\\', '/'); }
                catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException)
                { errors[byId[reference.Source].File].Add(error.Message); continue; }
            }
            var destinations = (reference.Path ? files : names).GetValueOrDefault(target);
            if (destinations == null)
            {
                if (reference.Required) errors[byId[reference.Source].File].Add("Не найдена цель перехода: " + target);
                else if (reference.Kind is "call" or "call_scene" or "goto" or "goto_scene" or "jump") dynamic++;
                continue;
            }
            foreach (var destination in destinations) links.Add(new(reference.Source, destination.Id, reference.Kind));
        }
        var reachable = new HashSet<string>(StringComparer.Ordinal);
        var outgoing = links.GroupBy(link => link.Source).ToDictionary(group => group.Key, group => group.Select(link => link.Target).ToArray());
        var pending = new Queue<string>(nodes.Where(node => node.Function == "main").Select(node => node.Id));
        while (pending.TryDequeue(out string? id))
            if (reachable.Add(id))
                foreach (string next in outgoing.GetValueOrDefault(id) ?? []) pending.Enqueue(next);
        return new(nodes.Select(node => node with { Reachable = reachable.Contains(node.Id), Errors = errors[node.File].Distinct().ToArray() }).ToArray(),
            links.OrderBy(link => link.Source, StringComparer.Ordinal).ThenBy(link => link.Target, StringComparer.Ordinal).ToArray(), dynamic);
    }
}
