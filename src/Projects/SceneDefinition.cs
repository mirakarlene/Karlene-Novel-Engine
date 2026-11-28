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
    internal static IReadOnlyDictionary<string, (string Effect, string Direction)> Presets { get; } = CreatePresets();

    private static Dictionary<string, (string, string)> CreatePresets()
    {
        var presets = new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            ["pixellate"] = ("pixellate", ""), ["blinds"] = ("blinds", ""), ["squares"] = ("squares", ""),
            ["hpunch"] = ("hpunch", ""), ["vpunch"] = ("vpunch", ""),
            ["irisin"] = ("iris", "in"), ["irisout"] = ("iris", "out"),
            ["move"] = ("slide", "left"), ["ease"] = ("easein", "left"),
            ["zoomin"] = ("zoomin", ""), ["zoomout"] = ("zoomout", ""), ["zoominout"] = ("zoominout", "")
        };
        foreach (string effect in new[] { "wipe", "slide", "slideaway", "push" })
            foreach (string direction in new[] { "left", "right", "up", "down" })
                presets.Add(effect + direction, (effect, direction));
        foreach (var (edge, outward, inward) in new[]
        {
            ("left", "left", "right"), ("right", "right", "left"), ("top", "up", "down"), ("bottom", "down", "up")
        })
        {
            presets.Add("movein" + edge, ("slide", inward));
            presets.Add("moveout" + edge, ("slideaway", outward));
            presets.Add("easein" + edge, ("easein", inward));
            presets.Add("easeout" + edge, ("easeout", outward));
        }
        return presets;
    }

    public string Kind { get; set; } = "none";
    public double Duration { get; set; } = 1;
    public string? Shader { get; set; }
    public void Validate(string? root = null)
    {
        if (Kind is null || Kind is not ("none" or "fade" or "dissolve" or "custom") && !Presets.ContainsKey(Kind) ||
            !double.IsFinite(Duration) || Duration is <= 0 or > 3600)
            throw new InvalidDataException("Выберите допустимый переход длительностью больше 0 и не более 3600 секунд.");
        if (Kind == "custom")
        {
            if (string.IsNullOrWhiteSpace(Shader) || !Shader.EndsWith(".kneshader", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Выберите .kneshader для своего перехода.");
            if (root != null && !File.Exists(PathSafety.ResolveRelative(root, Shader))) throw new InvalidDataException("Шейдер перехода не найден: " + Shader);
        }
    }
}
