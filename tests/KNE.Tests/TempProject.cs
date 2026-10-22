using System.IO;

namespace KNE.Tests;

internal sealed class TempProject : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "kne-tests-" + Guid.NewGuid().ToString("N"));
    public string ProjectPath { get; }

    public TempProject()
    {
        Directory.CreateDirectory(Root);
        ProjectPath = ProjectCreator.CreateProject("Новелла с пробелами", Root, ProjectAuthoringMode.Code,
            new ProjectCreationOptions { Width = 1280, Height = 720 });
    }

    public string Write(string relative, string content)
    {
        string target = PathSafety.ResolveRelative(ProjectPath, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content);
        return target;
    }

    public Task<ProjectInfo> LoadAsync() => ProjectLoader.LoadProjectAsync(ProjectPath);

    public void Dispose()
    {
        string fullPath = Path.GetFullPath(Root);
        string temp = Path.GetFullPath(Path.GetTempPath());
        if (!fullPath.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(fullPath).StartsWith("kne-tests-", StringComparison.Ordinal))
            throw new InvalidOperationException("Unexpected test directory.");
        Directory.Delete(fullPath, recursive: true);
    }
}
