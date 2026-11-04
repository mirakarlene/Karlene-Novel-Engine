using System.IO;
using System.Text;

namespace KNE;

/// <summary>Plans conservative, conflict-checked changes to declarations and statically resolvable scene references.</summary>
internal static class SceneRenaming
{
    internal static Dictionary<string, byte[]> Plan(ProjectInfo project, IReadOnlyDictionary<SceneDefinition, string> renames)
    {
        var changes = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        if (renames.Count == 0) return changes;
        var scenes = SceneDefinition.Read(project);
        var names = scenes.Select(scene => renames.GetValueOrDefault(scene, scene.Function))
            .Concat(project.Files.Where(file => ScriptDocument.IsCodePath(file.FilePath) && !ScenePerformanceDocument.IsScenePath(file.FilePath))
                .SelectMany(file => LuaSource.Functions(ScriptProtection.ReadText(file.FilePath))).Select(function => function.Name)).ToArray();
        if (names.Distinct(StringComparer.Ordinal).Count() != names.Length) throw new InvalidDataException("Имена функций сцен должны быть уникальны во всём проекте.");
        foreach (var pair in renames)
        {
            if (pair.Key.Function == "main" && pair.Value != "main") throw new InvalidDataException("Функция main остаётся точкой запуска; измените отображаемое название сцены.");
            if (!LuaSource.IsIdentifier(pair.Value)) throw new InvalidDataException("Имя функции должно быть допустимым Lua-идентификатором.");
        }
        foreach (var file in project.Files.Where(f => ScriptDocument.IsCodePath(f.FilePath) || ScriptDocument.IsVisualPath(f.FilePath)))
        {
            string relative = Path.GetRelativePath(project.Path, file.FilePath).Replace('\\', '/'); string before = ScriptProtection.ReadText(file.FilePath), after = before;
            if (ScriptDocument.IsVisualPath(file.FilePath))
            {
                var graph = VisualScript.Parse(before);
                var map = renames.ToDictionary(pair => pair.Key.Function, pair => pair.Value, StringComparer.Ordinal);
                var declaration = renames.FirstOrDefault(pair => relative.Equals(pair.Key.Path, StringComparison.OrdinalIgnoreCase) && pair.Key.Function == graph.FunctionName);
                if (declaration.Key != null) graph.FunctionName = declaration.Value;
                foreach (var node in graph.Nodes)
                    if (node.Kind is ScriptNodeKind.CallScene or ScriptNodeKind.GotoScene or ScriptNodeKind.ConditionalGoto && map.TryGetValue(node.Path, out string? next)) node.Path = next;
                after = graph.Serialize();
                // Avoid rewriting unrelated graphs solely because formatting differs.
                if (VisualScript.Parse(before).Serialize() == after) after = before;
            }
            else after = RenameCode(before, renames.ToDictionary(pair => pair.Key.Function, pair => pair.Value, StringComparer.Ordinal), renames.Where(pair => relative.Equals(pair.Key.Path, StringComparison.OrdinalIgnoreCase)).Select(pair => pair.Key.Function).ToHashSet(StringComparer.Ordinal));
            if (after != before) changes.Add(file.FilePath, EncodeLike(File.ReadAllBytes(file.FilePath), after));
        }
        return changes;
    }
    internal static string RenameCode(string source, string oldName, string newName, bool declaration) => RenameCode(source, new Dictionary<string, string> { [oldName] = newName }, declaration ? new HashSet<string> { oldName } : []);
    private static string RenameCode(string source, IReadOnlyDictionary<string, string> names, HashSet<string> declarations)
    {
        var edits = new Dictionary<int, (int Length, string Text)>();
        foreach (var pair in names)
        {
            foreach (var token in SceneNavigationSource.RenameTokens(source, pair.Key)) edits[token.Start] = (token.Length, token.Quoted ? "\"" + pair.Value + "\"" : pair.Value);
            foreach (var reference in SceneNavigationSource.Read(source, LuaSource.Functions(source)).References.Where(r => !r.Path && r.Target == pair.Key && r.Start.HasValue))
                edits[reference.Start!.Value] = (reference.Length, reference.Quoted ? "\"" + pair.Value + "\"" : pair.Value);
        }
        foreach (var function in LuaSource.FunctionPositions(source).Where(f => declarations.Contains(f.Function.Name)))
            edits[function.Offset] = (function.Function.Name.Length, names[function.Function.Name]);
        var result = new StringBuilder(source);
        foreach (var edit in edits.OrderByDescending(e => e.Key)) result.Remove(edit.Key, edit.Value.Length).Insert(edit.Key, edit.Value.Text);
        return result.ToString();
    }
    private static byte[] EncodeLike(byte[] original, string text)
    {
        var utf32Big = new UTF32Encoding(bigEndian: true, byteOrderMark: true);
        Encoding encoding = original.AsSpan().StartsWith(Encoding.UTF32.GetPreamble()) ? Encoding.UTF32 : original.AsSpan().StartsWith(utf32Big.GetPreamble()) ? utf32Big :
            original.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()) ? Encoding.Unicode : original.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()) ? Encoding.BigEndianUnicode : new UTF8Encoding(original.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()));
        return encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
    }
}
