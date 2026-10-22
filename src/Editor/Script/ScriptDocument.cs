using System.IO;
using System.Text;

namespace KNE;

/// <summary>A project-confined code or visual document with atomic saves and conflict detection.</summary>
public sealed class ScriptDocument
{
    private string savedContent;
    private readonly string root;

    /// <summary>Absolute path of the source inside the owning project.</summary>
    public string FilePath { get; }

    /// <summary>The editable visual model, or null for a manually authored script.</summary>
    public VisualScript? Graph { get; set; }

    /// <summary>Editable source text for manual documents.</summary>
    public string Code { get; set; }

    /// <summary>Whether this document contains a graph.</summary>
    public bool IsVisual => Graph != null;

    /// <summary>Whether model or source text differ from the last successful save.</summary>
    public bool IsDirty => Content != savedContent;

    private string Content => Graph?.Serialize() ?? Code;

    private ScriptDocument(string root, string path, string content)
    {
        this.root = root;
        FilePath = path;
        savedContent = content;
        Code = content;
        if (IsGraphPath(path))
        {
            Graph = VisualScript.Parse(content);
            if (Graph.IsShader != IsShaderPath(path)) throw new InvalidDataException("Формат графа должен соответствовать расширению .knegraph / .kneshader.");
            savedContent = Graph.Serialize();
        }

        diskContent = content;
    }

    private string diskContent;

    /// <summary>Recognizes the visual source extension without accessing the filesystem.</summary>
    /// <param name="path">A filename or path.</param>
    /// <returns>True for .knegraph, ignoring case.</returns>
    public static bool IsVisualPath(string path) =>
        Path.GetExtension(path).Equals(".knegraph", StringComparison.OrdinalIgnoreCase);
    /// <summary>Recognizes dedicated GPU data-flow graphs.</summary>
    public static bool IsShaderPath(string path) => Path.GetExtension(path).Equals(".kneshader", StringComparison.OrdinalIgnoreCase);
    /// <summary>Recognizes either editable visual document without treating shaders as game scripts.</summary>
    public static bool IsGraphPath(string path) => IsVisualPath(path) || IsShaderPath(path);

    /// <summary>Recognizes manually editable scenario source extensions.</summary>
    /// <param name="path">A filename or path.</param>
    /// <returns>True for .lua or .krl, ignoring case.</returns>
    public static bool IsCodePath(string path) => Path.GetExtension(path).ToLowerInvariant() is ".lua" or ".krl";

    /// <summary>Opens a Lua/KRL or KNE graph file within the specified project.</summary>
    /// <param name="root">Project root directory.</param>
    /// <param name="path">Absolute source path inside the project.</param>
    /// <param name="cancellationToken">Cancellation for file reads.</param>
    /// <returns>The loaded document and its saved baseline.</returns>
    public static async Task<ScriptDocument> OpenAsync(string root, string path,
        CancellationToken cancellationToken = default)
    {
        path = PathSafety.EnsureInside(root, path);
        if (!IsGraphPath(path) && !IsCodePath(path))
            throw new ArgumentException("Редактор поддерживает .lua, .krl, .knegraph и .kneshader.");
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new InvalidDataException("Сценарий больше 8 МБ.");
        string content = await File.ReadAllTextAsync(path, cancellationToken);
        return new ScriptDocument(root, path, content);
    }

    /// <summary>Creates a runnable visual scene without replacing an existing file.</summary>
    /// <param name="root">Project root directory.</param>
    /// <param name="path">Absolute destination with the .knegraph extension.</param>
    /// <param name="cancellationToken">Cancellation for the staging write.</param>
    /// <returns>A saved document ready to edit.</returns>
    public static async Task<ScriptDocument> CreateVisualAsync(string root, string path,
        CancellationToken cancellationToken = default)
    {
        path = PathSafety.EnsureInside(root, path);
        if (!IsGraphPath(path)) throw new ArgumentException("Выберите расширение .knegraph или .kneshader.");
        if (IsShaderPath(path))
        {
            string shaderContent = ShaderGraphCompiler.Create().Serialize();
            await WriteNewAsync(root, path, shaderContent, cancellationToken);
            return new ScriptDocument(root, path, shaderContent);
        }
        var graph = VisualScript.Create();
        graph.Nodes.Single(node => node.Kind == ScriptNodeKind.End).EndAction = ScriptEndAction.Menu;
        string stem = Path.GetFileNameWithoutExtension(path);
        string name = "scene_" + new string(stem.Select(c => char.IsAsciiLetterOrDigit(c) || c == '_' ? c : '_').ToArray());
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in ProjectLoader.EnumerateFiles(root, cancellationToken).Files)
        {
            if (!IsCodePath(file) && !IsVisualPath(file)) continue;
            PathSafety.EnsureInside(root, file);
            if (new FileInfo(file).Length > 8 * 1024 * 1024) continue;
            string source = await File.ReadAllTextAsync(file, cancellationToken);
            if (IsCodePath(file)) used.UnionWith(LuaSource.Functions(source).Select(f => f.Name));
            else used.Add(VisualScript.Parse(source).FunctionName);
        }
        string unique = name;
        for (int suffix = 2; used.Contains(unique); suffix++) unique = name + "_" + suffix;
        graph.FunctionName = unique;
        string content = graph.Serialize();
        await WriteNewAsync(root, path, content, cancellationToken);
        return new ScriptDocument(root, path, content);
    }

    /// <summary>Saves a draft atomically. External changes and deleted sources require reopening.</summary>
    /// <param name="cancellationToken">Cancellation before replacing the source.</param>
    /// <returns>A task that completes once the saved baseline has been updated.</returns>
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        string content = Content;
        if (Encoding.UTF8.GetByteCount(content) > 8 * 1024 * 1024)
            throw new InvalidDataException("Сценарий больше 8 МБ.");
        string target = PathSafety.EnsureInside(root, FilePath);
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staging, content, cancellationToken);
            PathSafety.EnsureInside(root, target);
            if (!File.Exists(target) || await File.ReadAllTextAsync(target, cancellationToken) != diskContent)
                throw new IOException(
                    "Файл изменён или удалён вне редактора. Сохраните копию либо откройте файл заново.");
            File.Move(staging, target, overwrite: true);
            savedContent = diskContent = content;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }

    /// <summary>Exports generated Lua into a new KRL file, leaving the graph editable.</summary>
    /// <param name="path">Absolute .krl destination inside the project.</param>
    /// <param name="cancellationToken">Cancellation before creating the destination.</param>
    /// <returns>A task that completes after the new code file is created.</returns>
    public Task ExportAsync(string path, CancellationToken cancellationToken = default)
    {
        if (Graph == null) throw new InvalidOperationException("Экспорт доступен для блок-схем.");
        if (Graph.IsShader)
        {
            var shader = ShaderGraphCompiler.Compile(Graph, root, FilePath);
            string source = Path.GetExtension(path).ToLowerInvariant() switch { ".frag" or ".glsl" => shader.Glsl, ".hlsl" => shader.ToHlsl(), _ => throw new ArgumentException("Выберите .frag / .glsl / .hlsl.") };
            return WriteNewAsync(root, path, source, cancellationToken);
        }
        if (!Path.GetExtension(path).Equals(".krl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Выберите расширение .krl.");
        return WriteNewAsync(root, path, Graph.Compile(), cancellationToken);
    }

    private static async Task WriteNewAsync(string root, string path, string content,
        CancellationToken cancellationToken)
    {
        string target = PathSafety.EnsureInside(root, path);
        PathSafety.ValidateFileName(Path.GetFileName(target));
        string staging = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(staging, content, cancellationToken);
            PathSafety.EnsureInside(root, target);
            File.Move(staging, target, overwrite: false);
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
