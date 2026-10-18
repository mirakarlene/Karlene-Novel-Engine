using System.IO;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace KNE;

/// <summary>A stable numeric command identity; text commands are the visible frames of a scene.</summary>
public sealed record FrameIdentity(string Scene, int SceneFrameId, int GlobalFrameId, string BlockId);

/// <summary>A source location and its persistent identity. Line numbers are informational, never identity keys.</summary>
public sealed record FrameEntry(string Key, string File, string Function, int FunctionLine, int Line,
    string? NodeId, bool HasText, FrameIdentity Identity);

/// <summary>Allocates the first free positive ID, retaining identities of surviving commands across edits.</summary>
public sealed class FrameCatalog
{
    private const int MaximumEntries = 100_000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> SyncGates = new(StringComparer.OrdinalIgnoreCase);
    private sealed record Registry(int Version, IReadOnlyList<FrameEntry> Entries);
    private sealed record Candidate(string Key, string File, string Function, int FunctionLine, int Line, string? NodeId, bool HasText)
    {
        public string Scene => File.ToUpperInvariant() + "::" + Function;
    }

    public IReadOnlyList<FrameEntry> Entries { get; }
    private readonly string projectPath;
    private FrameCatalog(string projectPath, IReadOnlyList<FrameEntry> entries) { this.projectPath = projectPath; Entries = entries; }

    /// <summary>Builds an in-memory catalog. Previewing never creates or updates project files.</summary>
    public static FrameCatalog Load(ProjectInfo project, GameScripts? scripts = null, EditorPreviewSource? editorSource = null)
    {
        string path = PathSafety.ResolveRelative(project.Path, "engine/frame-ids.json");
        IReadOnlyList<FrameEntry> previous = Array.Empty<FrameEntry>();
        if (System.IO.File.Exists(path))
        {
            if (new FileInfo(path).Length > 32 * 1024 * 1024) throw new InvalidDataException("Реестр ID фреймов больше 32 МБ.");
            try
            {
                var registry = JsonSerializer.Deserialize<Registry>(System.IO.File.ReadAllText(path), JsonOptions);
                if (registry is not { Version: 1, Entries: not null } || registry.Entries.Count > MaximumEntries)
                    throw new InvalidDataException("Неподдерживаемый реестр ID фреймов: engine/frame-ids.json.");
                previous = registry.Entries;
                Validate(previous);
            }
            catch (JsonException error) { throw new InvalidDataException("Некорректный реестр ID фреймов: engine/frame-ids.json.", error); }
        }
        var candidates = Discover(project, scripts, editorSource).ToArray();
        if (candidates.Length > MaximumEntries) throw new InvalidDataException("Допускается не более 100000 команд проекта.");
        var currentKeys = candidates.Select(candidate => candidate.Key).ToHashSet(StringComparer.Ordinal);
        var retained = previous.Where(entry => currentKeys.Contains(entry.Key)).ToDictionary(entry => entry.Key, StringComparer.Ordinal);
        var globals = retained.Values.Select(entry => entry.Identity.GlobalFrameId).ToHashSet();
        var locals = retained.Values.GroupBy(entry => entry.Identity.Scene, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Select(entry => entry.Identity.SceneFrameId).ToHashSet(), StringComparer.Ordinal);
        var entries = new List<FrameEntry>();
        int nextGlobal = 1;
        var nextLocals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            FrameIdentity identity;
            if (retained.TryGetValue(candidate.Key, out var existing))
            {
                if (!existing.File.Equals(candidate.File, StringComparison.OrdinalIgnoreCase) || existing.Function != candidate.Function ||
                    existing.NodeId != candidate.NodeId || existing.Identity.Scene != candidate.Scene)
                    throw new InvalidDataException("Реестр ID фреймов содержит противоречивый ключ команды: engine/frame-ids.json.");
                identity = existing.Identity;
            }
            else
            {
                if (!locals.TryGetValue(candidate.Scene, out var used)) locals[candidate.Scene] = used = [];
                int local = FirstFree(used, nextLocals.GetValueOrDefault(candidate.Scene, 1)), global = FirstFree(globals, nextGlobal);
                nextLocals[candidate.Scene] = local + 1; nextGlobal = global + 1;
                used.Add(local); globals.Add(global);
                identity = new(candidate.Scene, local, global, candidate.NodeId ?? "code-" + candidate.Key[^20..]);
            }
            entries.Add(new(candidate.Key, candidate.File, candidate.Function, candidate.FunctionLine, candidate.Line,
                candidate.NodeId, candidate.HasText, identity));
        }
        return new(project.Path, entries.AsReadOnly());
    }

    /// <summary>Persists a compact registry after an explicit project/document save; deleted IDs become available.</summary>
    public static async Task<FrameCatalog> SynchronizeAsync(ProjectInfo project, CancellationToken cancellationToken = default)
    {
        string target = PathSafety.ResolveRelative(project.Path, "engine/frame-ids.json");
        var gate = SyncGates.GetOrAdd(target, _ => new(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var snapshot = await Task.Run(() =>
            {
                string? baseline = System.IO.File.Exists(target) ? System.IO.File.ReadAllText(target) : null;
                return (Baseline: baseline, Catalog: Load(project));
            }, cancellationToken).ConfigureAwait(false);
            string content = JsonSerializer.Serialize(new Registry(1, snapshot.Catalog.Entries), JsonOptions);
            if (content == snapshot.Baseline) return snapshot.Catalog;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            string staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await System.IO.File.WriteAllTextAsync(staging, content, cancellationToken).ConfigureAwait(false);
                PathSafety.EnsureInside(project.Path, target);
                string? current = System.IO.File.Exists(target) ? await System.IO.File.ReadAllTextAsync(target, cancellationToken).ConfigureAwait(false) : null;
                if (current != snapshot.Baseline) throw new IOException("Реестр ID фреймов изменён другой программой. Повторите сохранение.");
                cancellationToken.ThrowIfCancellationRequested();
                System.IO.File.Move(staging, target, overwrite: true);
            }
            finally { if (System.IO.File.Exists(staging)) System.IO.File.Delete(staging); }
            return snapshot.Catalog;
        }
        finally { gate.Release(); }
    }

    /// <summary>Resolves the executing command. A graph marker is accepted only at that block's generated source span.</summary>
    public FrameIdentity? Resolve(string? file, int? line, string? nodeId = null, int? functionLine = null)
    {
        if (file == null || line is not > 0) return null;
        string? relative = NormalizeFile(file);
        if (relative == null) return null;
        var entries = Entries.Where(entry => entry.File.Equals(relative, StringComparison.OrdinalIgnoreCase));
        if (functionLine is > 0 && entries.Any(entry => entry.FunctionLine == functionLine))
            entries = entries.Where(entry => entry.FunctionLine == functionLine);
        var ordered = entries.Where(entry => entry.Line <= line).OrderByDescending(entry => entry.Line).ThenBy(entry => entry.HasText).ToArray();
        if (ordered.Length == 0) return null;
        var selected = ordered[0];
        if (selected.NodeId != null && nodeId != null && selected.NodeId != nodeId) return null;
        return selected.Identity;
    }

    internal FrameIdentity? ResolveKey(string? key, string file, int line, int? functionLine = null) => key == null ? null : Entries
        .FirstOrDefault(entry => entry.Key == key && entry.File.Equals(NormalizeFile(file), StringComparison.OrdinalIgnoreCase)
            && entry.Line <= line && (functionLine is not > 0 || entry.FunctionLine == functionLine))?.Identity;

    private string? NormalizeFile(string file)
    {
        string relative = Path.IsPathRooted(file) ? Path.GetRelativePath(projectPath, file) : file;
        relative = relative.Replace('\\', '/');
        return relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative) ? null : relative;
    }

    /// <summary>Adds source-preserving call identities, including distinct dialogue calls on the same Lua line.</summary>
    internal static string Instrument(string file, string source)
    {
        if (ScriptDocument.IsVisualPath(file)) return source;
        var calls = TextCalls(source).ToArray();
        var entries = CodeEntries(file, source).Where(entry => entry.HasText).ToArray();
        if (calls.Length == 0) return source;
        var result = new StringBuilder(source.Length + calls.Length * 100);
        int copied = 0;
        for (int index = 0; index < calls.Length; index++)
        {
            var call = calls[index];
            result.Append(source, copied, call.Name.Start - copied).Append("kne_frame_call(\"").Append(entries[index].Key)
                .Append("\", ").Append(source, call.Name.Start, call.Name.Length);
            if (call.HasArguments) result.Append(',');
            // Replace the existing '(' and preserve any intervening comments/whitespace and their line numbers.
            result.Append(source, call.Name.End, call.Open.Start - call.Name.End);
            copied = call.Open.End;
        }
        return result.Append(source, copied, source.Length - copied).ToString();
    }

    private sealed record TextCall(LuaToken Name, LuaToken Open, LuaToken Close, bool HasArguments);
    private static IEnumerable<TextCall> TextCalls(string source)
    {
        var tokens = LuaSyntax.Parse(source).Where(token => token.Kind != LuaTokenKind.Comment).ToArray();
        var byOffset = tokens.ToDictionary(token => token.Start);
        for (int index = 0; index + 1 < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token.Kind != LuaTokenKind.Function || source.Substring(token.Start, token.Length) is not ("d" or "choice" or "kne_choice" or "kne_say" or "text_input") ||
                index > 0 && (source.AsSpan(tokens[index - 1].Start, tokens[index - 1].Length).SequenceEqual("function") ||
                    source[tokens[index - 1].Start] is '.' or ':') || source[tokens[index + 1].Start] != '(' ||
                tokens[index + 1].Partner is not { } closing || !byOffset.TryGetValue(closing, out var close)) continue;
            yield return new(token, tokens[index + 1], close, index + 2 < tokens.Length && tokens[index + 2].Start < closing);
        }
    }

    private static int FirstFree(HashSet<int> used, int start)
    {
        for (int id = start; id <= MaximumEntries + 1; id++) if (!used.Contains(id)) return id;
        throw new InvalidDataException("Не удалось выделить ID фрейма.");
    }

    private static void Validate(IReadOnlyList<FrameEntry> entries)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal); var globals = new HashSet<int>();
        var locals = new HashSet<(string, int)>();
        foreach (var entry in entries)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key) || string.IsNullOrEmpty(entry.File) ||
                string.IsNullOrEmpty(entry.Function) || entry.Identity == null ||
                string.IsNullOrEmpty(entry.Identity.Scene) || string.IsNullOrEmpty(entry.Identity.BlockId) ||
                entry.Line < 1 || entry.FunctionLine < 0 ||
                entry.Identity.Scene != entry.File.ToUpperInvariant() + "::" + entry.Function ||
                entry.NodeId != null && entry.Identity.BlockId != entry.NodeId ||
                entry.Identity.SceneFrameId is < 1 or > MaximumEntries || entry.Identity.GlobalFrameId is < 1 or > MaximumEntries ||
                !keys.Add(entry.Key) || !globals.Add(entry.Identity.GlobalFrameId) || !locals.Add((entry.Identity.Scene, entry.Identity.SceneFrameId)))
                throw new InvalidDataException("Реестр ID фреймов содержит некорректные или повторяющиеся ID: engine/frame-ids.json.");
        }
    }

    private static IEnumerable<Candidate> Discover(ProjectInfo project, GameScripts? scripts, EditorPreviewSource? editorSource)
    {
        var files = scripts?.Files ?? ProjectLoader.EnumerateFiles(project.Path, CancellationToken.None).Files
            .Where(path => ScriptDocument.IsCodePath(path) || ScriptDocument.IsVisualPath(path))
            .OrderBy(path => Path.GetRelativePath(project.Path, path), StringComparer.Ordinal)
            .Select(path =>
            {
                path = PathSafety.EnsureInside(project.Path, path);
                if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Сценарий больше 8 МБ.");
                string relative = Path.GetRelativePath(project.Path, path).Replace('\\', '/');
                bool overridden = editorSource != null && editorSource.RelativePath(project.Path).Equals(relative, StringComparison.OrdinalIgnoreCase);
                string source = overridden ? editorSource!.Source : System.IO.File.ReadAllText(path);
                return new GameScript(relative, source, null,
                    ScriptDocument.IsVisualPath(path) && !overridden ? VisualScript.Parse(source) : null);
            }).ToArray();
        foreach (var file in files)
        {
            if (file.Graph != null)
            {
                // A draft graph does not have to compile in order to reserve IDs for its blocks.
                var lines = new Dictionary<string, int>(StringComparer.Ordinal);
                try { lines = GraphLines(file.Graph.Compile(editorSource?.StepCommands == true)); }
                catch (InvalidDataException) { }
                foreach (var node in file.Graph.Nodes)
                    yield return new(Key(file.Path, file.Graph.FunctionName, "node:" + node.Id), file.Path, file.Graph.FunctionName,
                        2, lines.GetValueOrDefault(node.Id, 1), node.Id, node.Kind is ScriptNodeKind.Dialogue or ScriptNodeKind.Choice or ScriptNodeKind.TimedChoice or ScriptNodeKind.AutoDialogue or ScriptNodeKind.EffectDialogue or ScriptNodeKind.TextInput);
                continue;
            }
            if (ScriptDocument.IsVisualPath(file.Path) && file.Source.StartsWith("-- Сгенерировано из блок-схемы KNE.", StringComparison.Ordinal))
            {
                string function = LuaSource.Functions(file.Source).FirstOrDefault()?.Name ?? "main";
                foreach (var node in GraphLines(file.Source))
                    yield return new(Key(file.Path, function, "node:" + node.Key), file.Path, function, 2, node.Value, node.Key, false);
                continue;
            }
            string prepared;
            try { prepared = StartupScripts.Declaration(file.Source).Source; }
            catch (InvalidDataException) { prepared = file.Source; } // IDs remain available while an init declaration is being repaired.
            foreach (var candidate in CodeEntries(file.Path, LuaSource.Prepare(prepared))) yield return candidate;
        }
    }

    private static IEnumerable<Candidate> CodeEntries(string file, string source)
    {
        var declarations = LuaSource.Functions(source).OrderBy(function => function.Line).ToArray();
        var offsets = LineOffsets(source);
        int Line(int offset) => Array.BinarySearch(offsets, offset) is var found && found >= 0 ? found + 1 : ~found;
        var allTokens = LuaSyntax.Parse(source).Where(token => token.Kind != LuaTokenKind.Comment).ToArray();
        var byOffset = allTokens.Select((token, index) => (token.Start, index)).ToDictionary(item => item.Start, item => item.index);
        var groups = allTokens.GroupBy(token => Line(token.Start));
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var group in groups)
        {
            var tokens = group.ToArray();
            if (tokens.Length == 1 && source.AsSpan(tokens[0].Start, tokens[0].Length).SequenceEqual("end")) continue;
            var declaration = declarations.LastOrDefault(function => function.Line <= group.Key);
            string function = declaration?.Name ?? "<init>";
            int functionLine = declaration?.Line ?? 0;
            string anchor = string.Join("\u001f", tokens.Select(token => source.Substring(token.Start, token.Length)));
            string occurrenceKey = function + "\u001e" + anchor;
            int occurrence = occurrences.GetValueOrDefault(occurrenceKey) + 1;
            occurrences[occurrenceKey] = occurrence;
            yield return new(Key(file, function, "line:" + anchor + "\u001e" + occurrence), file, function, functionLine, group.Key, null, false);
        }
        occurrences.Clear();
        foreach (var call in TextCalls(source))
        {
            int line = Line(call.Name.Start);
            var declaration = declarations.LastOrDefault(function => function.Line <= line);
            string function = declaration?.Name ?? "<init>";
            string anchor = string.Join("\u001f", allTokens.Skip(byOffset[call.Name.Start]).Take(byOffset[call.Close.Start] - byOffset[call.Name.Start] + 1)
                .Select(token => source.Substring(token.Start, token.Length)));
            string occurrenceKey = function + "\u001e" + anchor;
            int occurrence = occurrences.GetValueOrDefault(occurrenceKey) + 1;
            occurrences[occurrenceKey] = occurrence;
            yield return new(Key(file, function, "call:" + anchor + "\u001e" + occurrence), file, function,
                declaration?.Line ?? 0, line, null, true);
        }
    }

    private static string Key(string file, string function, string anchor) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        file.Replace('\\', '/').ToUpperInvariant() + "\u001e" + function + "\u001e" + anchor)));

    private static int[] LineOffsets(string source)
    {
        var offsets = new List<int> { 0 };
        for (int index = 0; index < source.Length; index++)
            if (source[index] == '\n' || source[index] == '\r' && (index + 1 >= source.Length || source[index + 1] != '\n')) offsets.Add(index + 1);
        return offsets.ToArray();
    }

    private static Dictionary<string, int> GraphLines(string source)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        var offsets = LineOffsets(source);
        var tokens = LuaSyntax.Parse(source).Where(token => token.Kind != LuaTokenKind.Comment).ToArray();
        for (int index = 0; index + 2 < tokens.Length; index++)
        {
            if (!source.AsSpan(tokens[index].Start, tokens[index].Length).SequenceEqual("kne_runtime_node") ||
                tokens[index + 2].Kind != LuaTokenKind.String) continue;
            string quoted = source.Substring(tokens[index + 2].Start, tokens[index + 2].Length);
            var id = new StringBuilder();
            for (int cursor = 1; cursor < quoted.Length - 1; cursor++)
            {
                if (quoted[cursor] != '\\') { id.Append(quoted[cursor]); continue; }
                if (++cursor >= quoted.Length - 1) break;
                if (char.IsAsciiDigit(quoted[cursor]) && cursor + 2 < quoted.Length - 1 &&
                    char.IsAsciiDigit(quoted[cursor + 1]) && char.IsAsciiDigit(quoted[cursor + 2]))
                { id.Append((char)int.Parse(quoted.Substring(cursor, 3), System.Globalization.CultureInfo.InvariantCulture)); cursor += 2; }
                else id.Append(quoted[cursor]);
            }
            int found = Array.BinarySearch(offsets, tokens[index].Start);
            result[id.ToString()] = found >= 0 ? found + 1 : ~found;
        }
        return result;
    }
}
