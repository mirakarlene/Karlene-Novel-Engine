namespace KNE;

/// <summary>An unsaved document executed only by an isolated editor preview session.</summary>
public sealed record EditorPreviewSource(string FilePath, string Source, string? EntryFunction = null,
    bool StepCommands = true, bool SceneTest = false, int? Seed = null)
{
    internal string RelativePath(string projectPath)
    {
        string full = System.IO.Path.IsPathRooted(FilePath) ? PathSafety.EnsureInside(projectPath, FilePath) : PathSafety.ResolveRelative(projectPath, FilePath);
        return System.IO.Path.GetRelativePath(projectPath, full).Replace('\\', '/');
    }
}

public sealed record PreviewLocation(string File, int Line, string? NodeId, int? FunctionLine = null);
