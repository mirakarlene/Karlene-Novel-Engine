using System.IO;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectLoaderTests
{
    [Fact]
    public async Task EntryPoints_AmbiguousFallback_RequiresExplicitScene()
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", "{}");
        File.Delete(Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl"));
        string first = fixture.Write("game/scenes/first.krl", "function main() end");
        var single = await fixture.LoadAsync();
        Assert.Equal(first, single.MainScenePath);
        fixture.Write("game/scenes/second.krl", "function main() end");
        var ambiguous = await fixture.LoadAsync();
        Assert.Throws<FileNotFoundException>(() => ambiguous.MainScenePath);
    }

    [Fact]
    public async Task LoadProject_MetadataNameAndCustomEntryPoints_AreUsed()
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", """
            {"metadata":{"name":"Display name"},"game_info":{"title":"Game title"},
             "entry_points":{"main_scene":"alternate/scene.krl","init_script":"alternate/init.lua"}}
            """);
        string scene = fixture.Write("alternate/scene.krl", "function main() end");
        string init = fixture.Write("alternate/init.lua", "answer = 42");
        var project = await fixture.LoadAsync();
        Assert.Equal("Display name", project.Name);
        Assert.Equal("Game title", project.Title);
        Assert.Equal(scene, project.MainScenePath);
        Assert.Equal(init, project.InitScriptPath);
    }

    [Fact]
    public async Task LoadProject_LegacySchemaAndWrongInitName_FallBackToGeneratedFile()
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", """
            {"name":"Legacy","entry_points":{"main_scene":"game/scenes/main.krl","init_script":"game/scripts/game_init.lua"}}
            """);
        var project = await fixture.LoadAsync();
        Assert.Equal("Legacy", project.Name);
        Assert.Equal("Legacy", project.Title);
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "scripts", "gamelogic.lua"), project.InitScriptPath);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("null")]
    [InlineData("[]")]
    public async Task LoadProject_InvalidConfiguration_ReportsError(string json)
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", json);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.LoadAsync());
    }

    [Fact]
    public async Task LoadProject_MissingConfiguration_RejectsArbitraryFolder()
    {
        using var fixture = new TempProject();
        File.Delete(Path.Combine(fixture.ProjectPath, "project.json"));
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.LoadAsync());
    }

    [Fact]
    public async Task DeclaredEntryPoint_DoesNotSilentlyUseOtherScene()
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", """{"entry_points":{"main_scene":"missing.krl"}}""");
        var project = await fixture.LoadAsync();
        Assert.Throws<FileNotFoundException>(() => project.MainScenePath);
    }

    [Theory]
    [InlineData("../outside.krl")]
    [InlineData("C:/outside.krl")]
    public async Task DeclaredEntryPoint_OutsideProject_Rejects(string path)
    {
        using var fixture = new TempProject();
        fixture.Write("project.json", "{\"entry_points\":{\"main_scene\":\"" + path + "\"}}");
        var project = await fixture.LoadAsync();
        Assert.Throws<ArgumentException>(() => project.MainScenePath);
    }

    [Fact]
    public async Task LoadProject_Cancelled_DoesNotReturnPartialSnapshot()
    {
        using var fixture = new TempProject();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProjectLoader.LoadProjectAsync(fixture.ProjectPath, cancellation.Token));
    }
}
