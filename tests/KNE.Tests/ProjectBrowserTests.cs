using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectBrowserTests
{
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
