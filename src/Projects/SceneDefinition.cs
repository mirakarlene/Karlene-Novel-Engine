using System.IO;

namespace KNE;

/// <summary>A named scene entry inside a source file; authoring never moves the file on disk.</summary>
internal sealed record SceneDefinition(string Path, string Function, bool Graph)
{
    internal string Key => Path + "::" + Function;
    internal string Label => Path + " · " + Function;
    internal static IReadOnlyList<SceneDefinition> Read(ProjectInfo project)
    {
        var result = new List<SceneDefinition>();
        foreach (var file in project.Files.Where(f => ScenePerformanceDocument.IsScenePath(f.FilePath)))
        {
            string path = System.IO.Path.GetRelativePath(project.Path, file.FilePath).Replace('\\', '/'); string source = ScriptProtection.ReadText(file.FilePath);
            if (ScriptDocument.IsVisualPath(path)) result.Add(new(path, VisualScript.Parse(source).FunctionName, true));
            else result.AddRange(LuaSource.Functions(source).Select(function => new SceneDefinition(path, function.Name, false)));
        }
        return result;
    }
}

/// <summary>Independent entry/exit transition; disabling shake/flash does not disable scene transitions.</summary>
public sealed record SceneTransitionSettings
{
    public string Kind { get; set; } = "none";
    public double Duration { get; set; } = 1;
    public string? Shader { get; set; }
    public void Validate(string? root = null)
    {
        if (Kind is not ("none" or "fade" or "dissolve" or "custom") || !double.IsFinite(Duration) || Duration is <= 0 or > 3600) throw new InvalidDataException("Переход: none/fade/dissolve/custom, длительность больше 0 и не более 3600 секунд.");
        if (Kind == "custom")
        {
            if (string.IsNullOrWhiteSpace(Shader) || !Shader.EndsWith(".kneshader", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Выберите .kneshader для своего перехода.");
            if (root != null && !File.Exists(PathSafety.ResolveRelative(root, Shader))) throw new InvalidDataException("Шейдер перехода не найден: " + Shader);
        }
    }
}
