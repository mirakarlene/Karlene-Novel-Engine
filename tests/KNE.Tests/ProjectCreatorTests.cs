using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectCreatorTests
{
    [Theory]
    [InlineData(ProjectAuthoringMode.Code, "game/scenes/main.krl")]
    [InlineData(ProjectAuthoringMode.Visual, "game/scenes/main.knegraph")]
    public async Task CreateProject_ChosenMode_PersistsPreferenceAndCreatesExactlyOneRunnableMain(ProjectAuthoringMode mode, string mainPath)
    {
        using var fixture = new TempProject();
        string path = await ProjectCreator.CreateProjectAsync("Выбранный способ", fixture.Root, mode);
        var project = await ProjectLoader.LoadProjectAsync(path);
        Assert.Equal(mode, project.Configuration.AuthoringMode);
        Assert.Equal(Path.Combine(path, mainPath.Replace('/', Path.DirectorySeparatorChar)), project.MainScenePath);
        Assert.Single(Directory.GetFiles(Path.Combine(path, "game", "scenes")));
        var scripts = GameScripts.Load(project);
        Assert.Single(scripts.Functions, f => f.Name == "main");
        Assert.Equal(mainPath, scripts.Main.Path);
        Assert.Contains(mainPath, File.ReadAllText(Path.Combine(path, "README.txt")));
        using var json = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "project.json")));
        Assert.Equal(mode.ToString(), json.RootElement.GetProperty("authoring_mode").GetString());

        await using var runtime = new LuaRuntime(project, _ => { }, interactive: true);
        await runtime.StartAsync();
        Assert.Equal("Добро пожаловать! Это базовая сцена.", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.Equal("Игрок", runtime.Story.Speaker);
        Assert.Equal("Нажмите «Далее» или пробел, чтобы продолжить.", runtime.Story.Text);
        await runtime.AdvanceAsync();
        Assert.True(runtime.Story.Completed);
    }

    [Fact]
    public async Task CreateProject_VisualMain_OpensAsEditableConnectedGraph()
    {
        using var fixture = new TempProject();
        string path = ProjectCreator.CreateProject("Блоки", fixture.Root, ProjectAuthoringMode.Visual);
        var document = await ScriptDocument.OpenAsync(path, Path.Combine(path, "game", "scenes", "main.knegraph"));
        Assert.True(document.IsVisual);
        Assert.Equal("main", document.Graph!.FunctionName);
        var nodes = document.Graph.Nodes;
        Assert.Equal("dialogue", nodes.Single(n => n.Kind == ScriptNodeKind.Start).Next);
        Assert.Equal("continue", nodes.Single(n => n.Id == "dialogue").Next);
        Assert.Equal("end", nodes.Single(n => n.Id == "continue").Next);
        Assert.Single(nodes, n => n.Kind == ScriptNodeKind.End);
        nodes.Single(n => n.Id == "dialogue").Text = "Первая правка без преобразования кода";
        await document.SaveAsync();
        Assert.Equal("Первая правка без преобразования кода", VisualScript.Parse(File.ReadAllText(document.FilePath)).Nodes.Single(n => n.Id == "dialogue").Text);
        Assert.False(File.Exists(Path.Combine(path, "game", "scenes", "main.krl")));
    }

    [Fact]
    public void Configuration_MissingAuthoringPreference_DefaultsToCodeAndKeepsUnknownFields()
    {
        var configuration = JsonSerializer.Deserialize<ProjectConfiguration>("{\"custom\": {\"enabled\": true}}", ProjectConfiguration.JsonOptions)!;
        Assert.Equal(ProjectAuthoringMode.Code, configuration.AuthoringMode);
        configuration.AuthoringMode = ProjectAuthoringMode.Visual;
        using var saved = JsonDocument.Parse(JsonSerializer.Serialize(configuration, ProjectConfiguration.JsonOptions));
        Assert.True(saved.RootElement.GetProperty("custom").GetProperty("enabled").GetBoolean());
        Assert.Equal("Visual", saved.RootElement.GetProperty("authoring_mode").GetString());
    }

    [Fact]
    public void CreateProject_InvalidAuthoringMode_DoesNotCreateProjectOrStaging()
    {
        using var fixture = new TempProject();
        Assert.Throws<ArgumentOutOfRangeException>(() => ProjectCreator.CreateProject("Недопустимый", fixture.Root, (ProjectAuthoringMode)99));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "Недопустимый")));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".kne-*"));
    }

    [Fact]
    public void CreateProject_CancelledVisualMode_DoesNotPublishPartialProject()
    {
        using var fixture = new TempProject();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() => ProjectCreator.CreateProject("Отмена блоков", fixture.Root, ProjectAuthoringMode.Visual, cancellation.Token));
        Assert.False(Directory.Exists(Path.Combine(fixture.Root, "Отмена блоков")));
        Assert.Empty(Directory.GetDirectories(fixture.Root, ".kne-*"));
    }

    [Fact]
    public async Task CreateProject_ProducesLoadableProjectWithExistingEntryPoints()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        Assert.Equal("Новелла с пробелами", project.Name);
        Assert.Equal("Новелла с пробелами", project.Title);
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "scenes", "main.krl"), project.MainScenePath);
        Assert.Equal(Path.Combine(fixture.ProjectPath, "game", "scripts", "gamelogic.lua"), project.InitScriptPath);
        Assert.Equal(new[] { "game.json", "gamelogic.lua", "main.krl", "project.json", "README.txt", "README.txt", "screens.json", "state.json", "theme.json", "window.json" },
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
