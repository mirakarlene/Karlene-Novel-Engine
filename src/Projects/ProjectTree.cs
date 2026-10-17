using System.IO;

namespace KNE;

public sealed class ProjectTreeNode
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public ProjectFile? File { get; init; }
    public List<ProjectTreeNode> Children { get; } = [];
}

public static class ProjectTree
{
    public static IReadOnlyList<ProjectTreeNode> Build(string root, IEnumerable<ProjectFile> files, string query = "")
    {
        var top = new ProjectTreeNode { Name = "", RelativePath = "" };
        var nodes = new Dictionary<string, ProjectTreeNode>(StringComparer.OrdinalIgnoreCase);
        query = query.Replace('/', Path.DirectorySeparatorChar);
        foreach (var file in files.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase))
        {
            string relative = Path.GetRelativePath(root, file.FilePath);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Файл дерева должен принадлежать проекту.");
            if (!relative.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var parts = relative.Split(Path.DirectorySeparatorChar);
            var parent = top;
            string path = "";
            for (int i = 0; i < parts.Length; i++)
            {
                path = path.Length == 0 ? parts[i] : Path.Combine(path, parts[i]);
                if (!nodes.TryGetValue(path, out var node))
                {
                    node = new ProjectTreeNode { Name = parts[i], RelativePath = path, File = i == parts.Length - 1 ? file : null };
                    nodes.Add(path, node);
                    parent.Children.Add(node);
                }
                parent = node;
            }
        }
        Sort(top);
        return top.Children;
    }

    private static void Sort(ProjectTreeNode node)
    {
        node.Children.Sort((a, b) => a.File == null && b.File != null ? -1 : a.File != null && b.File == null ? 1 : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (var child in node.Children) Sort(child);
    }
}
