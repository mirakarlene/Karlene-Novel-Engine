using System.IO;
using System.Windows.Media.Imaging;

namespace KNE;

/// <summary>Bounded decoded-image cache. Speculative loads run outside the UI and never advance the story.</summary>
internal sealed class SceneAssetCache
{
    private readonly object sync = new();
    private readonly Dictionary<string, (long Length, DateTime Modified, BitmapImage Image, long Bytes, long Access)> images = new(StringComparer.OrdinalIgnoreCase);
    private long bytes, access, generation;
    private long budget = 64 * 1024 * 1024;
    internal long CachedBytes { get { lock (sync) return bytes; } }
    internal int CachedCount { get { lock (sync) return images.Count; } }
    internal void Configure(GraphicsSettings settings)
    {
        settings.Validate();
        lock (sync) { budget = settings.CacheBudgetBytes; generation++; Trim(0); }
    }
    internal void Clear()
    {
        lock (sync) { generation++; images.Clear(); bytes = access = 0; }
    }
    private void Trim(long incoming)
    {
        while (images.Count > 0 && (bytes > budget - incoming || images.Count >= (incoming == 0 ? 33 : 32)))
        {
            var oldest = images.MinBy(entry => entry.Value.Access);
            bytes -= oldest.Value.Bytes; images.Remove(oldest.Key);
        }
    }
    internal BitmapImage Load(string path, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var file = new FileInfo(path);
        long epoch;
        lock (sync)
        {
            epoch = generation;
            if (images.TryGetValue(path, out var cached) && cached.Length == file.Length && cached.Modified == file.LastWriteTimeUtc)
            { images[path] = cached with { Access = ++access }; return cached.Image; }
        }
        if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("Изображение больше 64 МБ.");
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
        long cost = (long)image.PixelWidth * image.PixelHeight * 4;
        lock (sync)
        {
            token.ThrowIfCancellationRequested();
            if (epoch != generation) return image;
            if (images.Remove(path, out var previous)) bytes -= previous.Bytes;
            if (cost <= budget)
            {
                Trim(cost);
                images[path] = (file.Length, file.LastWriteTimeUtc, image, cost, ++access); bytes += cost;
            }
        }
        return image;
    }
    internal async Task PreloadAsync(ProjectInfo project, string? scenePath, CancellationToken token)
    {
        await Task.Run(() =>
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var graphs = new Dictionary<string, VisualScript>(StringComparer.Ordinal);
            var scripts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var functions = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in project.Files.Where(file => ScriptDocument.IsVisualPath(file.FilePath)).Take(500))
            {
                token.ThrowIfCancellationRequested();
                try { if (new FileInfo(file.FilePath).Length <= 2 * 1024 * 1024) { var graph = VisualScript.Parse(File.ReadAllText(file.FilePath)); graphs.TryAdd(graph.FunctionName, graph); } }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { }
            }
            VisualScript? source = null;
            foreach (var file in project.Files.Where(file => Path.GetExtension(file.FilePath).ToLowerInvariant() is ".lua" or ".krl").Take(500))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    if (new FileInfo(file.FilePath).Length > 2 * 1024 * 1024) continue;
                    string text = File.ReadAllText(file.FilePath); scripts[file.FilePath] = text;
                    foreach (var declaration in LuaSource.Functions(text)) functions.TryAdd(declaration.Name, file.FilePath);
                }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { }
            }
            var targetNames = new HashSet<string>(StringComparer.Ordinal);
            if (scenePath != null)
            {
                string full = PathSafety.ResolveRelative(project.Path, scenePath);
                try { if (File.Exists(full) && ScriptDocument.IsVisualPath(full) && new FileInfo(full).Length <= 2 * 1024 * 1024) source = VisualScript.Parse(File.ReadAllText(full)); }
                catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException) { }
                if (scripts.TryGetValue(full, out string? text))
                {
                    var tokens = LuaSyntax.Parse(text).Where(item => item.Kind != LuaTokenKind.Comment).ToArray();
                    for (int i = 0; i + 2 < tokens.Length; i++)
                        if (text[tokens[i].Start..tokens[i].End] is "goto" or "goto_scene" or "jump" or "call" or "call_scene")
                        { var target = tokens[i + 2]; targetNames.Add(text[target.Start..target.End].Trim('"', '\'')); }
                }
            }
            if (source != null) foreach (var node in source.Nodes.Where(node => node.Kind is ScriptNodeKind.GotoScene or ScriptNodeKind.CallScene)) targetNames.Add(node.Path);
            IEnumerable<VisualScript> candidates = (source == null ? Enumerable.Empty<VisualScript>() : new[] { source }).Concat(targetNames.Select(name => graphs.GetValueOrDefault(name)).OfType<VisualScript>()).Take(4);
            foreach (var graph in candidates)
                foreach (var node in graph.Nodes.Where(node => node.Kind is ScriptNodeKind.Background or ScriptNodeKind.Character).Take(24))
                {
                    token.ThrowIfCancellationRequested();
                    try { if (!string.IsNullOrWhiteSpace(node.Path)) paths.Add(ResourcePaths.Resolve(project.Path, node.Path)); }
                    catch (Exception error) when (error is IOException or ArgumentException) { }
                }
            var codePaths = targetNames.Select(name => functions.GetValueOrDefault(name)).OfType<string>();
            if (scenePath != null) codePaths = new[] { PathSafety.ResolveRelative(project.Path, scenePath) }.Concat(codePaths);
            foreach (string file in codePaths.Distinct(StringComparer.OrdinalIgnoreCase).Take(4))
                if (scripts.TryGetValue(file, out string? text))
                {
                    var tokens = LuaSyntax.Parse(text).Where(item => item.Kind != LuaTokenKind.Comment).ToArray();
                    for (int i = 0; i + 2 < tokens.Length && paths.Count < 24; i++)
                    {
                        if (text[tokens[i].Start..tokens[i].End] is not ("background" or "show_character")) continue;
                        var literal = tokens[i + 1].Kind == LuaTokenKind.String ? tokens[i + 1] : tokens[i + 2];
                        if (literal.Kind != LuaTokenKind.String || literal.Length < 2) continue;
                        string resource = text[(literal.Start + 1)..(literal.End - 1)];
                        // Dynamic expressions and escaped/long strings are left for normal runtime loading.
                        if (resource.Contains('\\')) continue;
                        try { paths.Add(ResourcePaths.Resolve(project.Path, resource)); }
                        catch (Exception error) when (error is IOException or ArgumentException) { }
                    }
                }
            foreach (string path in paths.Take(24))
            {
                token.ThrowIfCancellationRequested();
                try { _ = Load(path, token); } catch (Exception error) when (error is IOException or NotSupportedException or ArgumentException or InvalidDataException or System.IO.FileFormatException) { }
            }
        }, token);
    }
}
