using System.IO;
using System.ComponentModel;

namespace KNE;

public sealed class ProjectTreeNode : INotifyPropertyChanged
{
    public required string Name { get; init; }
    public required string RelativePath { get; init; }
    public ProjectFile? File { get; init; }
    /// <summary>Folder expansion retained when refreshing the file tree.</summary>
    public bool IsExpanded { get => expanded; set { if (expanded == value) return; expanded = value; PropertyChanged?.Invoke(this, new(nameof(IsExpanded))); } }
    /// <summary>Selection retained by relative path.</summary>
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; PropertyChanged?.Invoke(this, new(nameof(IsSelected))); } }
    private bool expanded, selected;
    /// <summary>Updates the inspector when navigation changes folder selection or ancestor expansion.</summary>
    public event PropertyChangedEventHandler? PropertyChanged;
    public List<ProjectTreeNode> Children { get; } = [];
}

public static class ProjectTree
{
    public static IReadOnlyList<ProjectTreeNode> Build(string root, IEnumerable<ProjectFile> files, string query = "", IEnumerable<string>? directories = null)
    {
        var top = new ProjectTreeNode { Name = "", RelativePath = "" };
        var nodes = new Dictionary<string, ProjectTreeNode>(StringComparer.OrdinalIgnoreCase);
        query = query.Replace('/', Path.DirectorySeparatorChar);
        foreach (string directory in directories ?? []) Add(directory, null);
        foreach (var file in files.OrderBy(f => f.FilePath, StringComparer.OrdinalIgnoreCase))
            Add(file.FilePath, file);
        Sort(top);
        return top.Children;

        void Add(string fullPath, ProjectFile? file)
        {
            string relative = Path.GetRelativePath(root, fullPath);
            if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new ArgumentException("Файл дерева должен принадлежать проекту.");
            if (relative == "." || !relative.Contains(query, StringComparison.OrdinalIgnoreCase)) return;
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
    }

    private static void Sort(ProjectTreeNode node)
    {
        node.Children.Sort((a, b) => a.File == null && b.File != null ? -1 : a.File != null && b.File == null ? 1 : StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));
        foreach (var child in node.Children) Sort(child);
    }
}
