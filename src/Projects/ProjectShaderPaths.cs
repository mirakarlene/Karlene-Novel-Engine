using System.IO;

namespace KNE;

/// <summary>Default location for shader authoring, including projects created by older engine versions.</summary>
internal static class ProjectShaderPaths
{
    internal static string Directory(string root)
    {
        string path = PathSafety.ResolveRelative(root, "game/shaders");
        System.IO.Directory.CreateDirectory(path);
        return path;
    }

    internal static string NewFile(string root, string fileName, bool shader)
    {
        string full = Path.IsPathFullyQualified(fileName) ? PathSafety.EnsureInside(root, fileName) : PathSafety.ResolveRelative(root, fileName);
        if (!shader) return full;
        full = Path.ChangeExtension(full, ".kneshader");
        return string.Equals(Path.GetDirectoryName(full), Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Directory(root), Path.GetFileName(full)) : full;
    }
}
