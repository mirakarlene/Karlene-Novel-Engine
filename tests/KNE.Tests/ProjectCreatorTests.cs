using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectCreatorTests
{
    [Fact]
    public async Task CreateProject_ProducesLoadableProjectWithExistingEntryPoints()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        Assert.Equal("Новелла с пробелами", project.Name);
        Assert.Equal("Новелла с пробелами", project.Title);
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl"), project.MainScenePath);
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "scripts", "gamelogic.lua"), project.InitScriptPath);
        Assert.Equal(new[] { "game.json", "gamelogic.lua", "main.krl", "project.json", "README.txt", "README.txt", "state.json", "theme.json", "window.json" },
            project.Files.Select(f => f.FileName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        Assert.Contains("function main()", File.ReadAllText(project.MainScenePath));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".kne-*"));
    }

    [Fact]
    public void CreateProject_ExistingTarget_DoesNotOverwrite()
    {
        using var fixture = new TempProject();
        Assert.Throws<IOException>(() => ProjectCreator.CreateProject("Новелла с пробелами", fixture.Root));
        Assert.Contains("function main()", File.ReadAllText(Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl")));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".kne-*"));
    }

    [Fact]
    public void CreateProject_Cancelled_DoesNotPublishPartialProject()
    {
        using var fixture = new TempProject();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectCreator.CreateProject("Cancelled", fixture.Root, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "Cancelled")));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".kne-*"));
    }

    [Fact]
    public void CreateProject_TraversalName_DoesNotCreateOutsideBase()
    {
        using var fixture = new TempProject();
        Assert.Throws<ArgumentException>(() => ProjectCreator.CreateProject("..\\outside", fixture.Root));
        Assert.Single(Directory.GetDirectories(fixture.Root));
    }
}
