using System.IO;

namespace KNE;

/// <summary>Creates or validates nested graph sources inside the current project.</summary>
public static class SubsystemFiles
{
    public static async Task<string> EnsureAsync(string root, string path, CancellationToken token = default)
    {
        path = PathSafety.EnsureInside(root, path);
        if (!ScriptDocument.IsGraphPath(path)) throw new ArgumentException("Подсистема должна иметь расширение .knegraph или .kneshader.");
        if (File.Exists(path)) { _ = await ScriptDocument.OpenAsync(root, path, token); return path; }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        PathSafety.EnsureInside(root, path);
        var document = await ScriptDocument.CreateVisualAsync(root, path, token);
        foreach (var end in document.Graph!.Nodes.Where(node => node.Kind == ScriptNodeKind.End)) end.EndAction = ScriptEndAction.Return;
        await document.SaveAsync(token);
        return path;
    }
}
