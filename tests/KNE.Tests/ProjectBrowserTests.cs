using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectBrowserTests
{
    [Fact]
    public async Task SnapshotAndTree_IncludeEmptyDirectoriesAndOnlyDirectChildren_WithoutMovingFiles()
    {
        using var fixture = new TempProject();
        string leaf = fixture.Write("chapters/act1/scene.krl", "function scene() end");
        string empty = Path.Combine(fixture.ProjectPath, "chapters", "empty"); Directory.CreateDirectory(empty);
        string ignored = Path.Combine(fixture.ProjectPath, "saves"); Directory.CreateDirectory(ignored);
        string backup = Path.Combine(fixture.ProjectPath, ".kne-backups"); Directory.CreateDirectory(backup);
        var project = await fixture.LoadAsync();
        Assert.Contains(empty, project.Directories); Assert.DoesNotContain(ignored, project.Directories); Assert.DoesNotContain(backup, project.Directories);
        var tree = ProjectTree.Build(project.Path, project.Files, directories: project.Directories);
        var chapters = Assert.Single(tree, node => node.Name == "chapters");
        Assert.Null(chapters.File); Assert.Equal(new[] { "act1", "empty" }, chapters.Children.Select(node => node.Name));
        Assert.Empty(chapters.Children.Single(node => node.Name == "empty").Children);
        Assert.Equal(leaf, Assert.Single(chapters.Children.Single(node => node.Name == "act1").Children).File!.FilePath);
        Assert.DoesNotContain(tree, node => node.Name == "scene.krl");
        Assert.Equal("function scene() end", File.ReadAllText(leaf));
        var filtered = ProjectTree.Build(project.Path, project.Files, "act1/scene", project.Directories);
        Assert.Equal("chapters", Assert.Single(filtered).Name);
        Assert.Equal("act1", Assert.Single(filtered[0].Children).Name);
        Assert.Equal(leaf, Assert.Single(filtered[0].Children[0].Children).File!.FilePath);
        Assert.Throws<ArgumentException>(() => ProjectTree.Build(project.Path, [], directories: [fixture.Root]));
    }

    [Fact]
    public async Task Tree_SearchKeepsFolderHierarchyAndMatchesCaseInsensitivePaths()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/forest.krl", "function main() end");
        var project = await fixture.LoadAsync();
        var tree = ProjectTree.Build(project.Path, project.Files, "SCENES/FOREST");
        var game = Assert.Single(tree);
        Assert.Equal("game", game.Name);
        var scenes = Assert.Single(game.Children);
        Assert.Equal("scenes", scenes.Name);
        var file = Assert.Single(scenes.Children);
        Assert.Equal("forest.krl", file.Name);
        Assert.Equal(Path.Combine(project.Path, "game", "scenes", "forest.krl"), file.File!.FilePath);
        Assert.Empty(ProjectTree.Build(project.Path, project.Files, "no-match"));
    }

    [Fact]
    public void RecentProjects_DeduplicateNormalizeAndKeepEightMostRecent()
    {
        using var fixture = new TempProject();
        var settings = new UserSettings();
        for (int i = 0; i < 10; i++) settings.RememberProject(Path.Combine(fixture.Root, "project-" + i));
        settings.RememberProject(Path.Combine(fixture.Root, "project-3") + Path.DirectorySeparatorChar);
        Assert.Equal(8, settings.RecentProjects.Count);
        Assert.Equal(Path.Combine(fixture.Root, "project-3"), settings.RecentProjects[0]);
        Assert.Single(settings.RecentProjects, p => p.EndsWith("project-3", StringComparison.Ordinal));
        Assert.DoesNotContain(Path.Combine(fixture.Root, "project-0"), settings.RecentProjects);
    }

    [Fact]
    public async Task Monitor_NotifiesAfterExternalFileWrite()
    {
        using var fixture = new TempProject();
        using var monitor = new ProjectChangeMonitor(fixture.ProjectPath);
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        monitor.Changed += (_, _) => changed.TrySetResult();
        fixture.Write("external.txt", "saved by editor");
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var project = await fixture.LoadAsync();
        Assert.Contains(project.Files, f => f.FileName == "external.txt");
        monitor.Dispose();
    }
}
