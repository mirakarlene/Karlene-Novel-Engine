using System.IO;
using System.Windows.Media;

namespace KNE;

/// <summary>A folder or file in the current directory of the embedded project browser.</summary>
internal sealed record ProjectBrowserEntry(string Name, string RelativePath, ProjectFile? File)
{
    internal bool IsDirectory => File == null;
    public string DisplayName => Name;
    public string PathLabel => RelativePath;
    public string Kind => File?.FileType ?? "Папка";
    public ProjectFile? ProjectFile => File;
    public ImageSource? Icon => WindowsFileIcons.Get(Path.GetExtension(Name), IsDirectory);
}
