using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class EngineSettingsTests
{
    [Fact]
    public async Task NewProject_CreatesEditableEngineFilesAndRuntimeDefaults()
    {
        using var fixture = new TempProject();
        var project = await fixture.LoadAsync();
        foreach (string name in new[] { "window.json", "theme.json", "game.json", "state.json", "README.txt" })
            Assert.True(File.Exists(Path.Combine(project.Path, "engine", name)));
        Assert.Equal(new RuntimeWindowSettings(project.Title, 1280, 720, "#FF201134"), project.Engine.RuntimeDefaults(project.Title));
        Assert.True(project.Engine.Game.MainMenu);
        Assert.Equal("#ED21162F", project.Engine.Theme.Dialogue.BackgroundColor);
        Assert.Equal(240, project.Engine.Theme.Dialogue.Height);
        Assert.Contains("engine/*.json", File.ReadAllText(Path.Combine(project.Path, "README.txt")));
        await using var worker = new ProcessGameSession(project, _ => { });
        Assert.Equal(project.Engine.RuntimeDefaults(project.Title), worker.WindowSettings);
    }

    [Fact]
    public async Task LegacyProject_WithoutEngineFiles_RetainsOldWindowAndDirectStart()
    {
        using var fixture = new TempProject();
        foreach (string name in new[] { "window.json", "theme.json", "game.json", "state.json", "screens.json" })
            File.Delete(Path.Combine(fixture.ProjectPath, "engine", name));
        var project = await fixture.LoadAsync();
        Assert.Equal(new RuntimeWindowSettings(project.Title, 800, 600, "#FF2D2D30"), project.Engine.RuntimeDefaults(project.Title));
        Assert.False(project.Engine.Game.MainMenu);
        Assert.False(project.Engine.Theme.Pattern);
        fixture.Write("engine/theme.json", "{}");
        var upgraded = await fixture.LoadAsync();
        Assert.Equal(1280, upgraded.Engine.Window.Width);
        Assert.True(upgraded.Engine.Game.MainMenu);
    }

    [Fact]
    public async Task PartialConfiguration_AppliesOverridesAndMissingFieldsUseDefaults()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/window.json", """{"width":1440,"height":900,"position":"manual","left":120,"top":140,"mode":"maximized","resizable":false}""");
        fixture.Write("engine/theme.json", """{"pattern":false,"background_color":"#FF123456","dialogue":{"font_size":28,"margin":16}}""");
        fixture.Write("engine/game.json", """{"main_menu":false,"auto_save":false,"execution_timeout_seconds":7,"start_label":"Читать"}""");
        var project = await fixture.LoadAsync();
        Assert.Equal(new RuntimeWindowSettings(project.Title, 1440, 900, "#FF123456"), project.Engine.RuntimeDefaults(project.Title));
        Assert.Equal("manual", project.Engine.Window.Position);
        Assert.Equal(120, project.Engine.Window.Left);
        Assert.Equal(140, project.Engine.Window.Top);
        Assert.False(project.Engine.Window.Resizable);
        Assert.Equal(28, project.Engine.Theme.Dialogue.FontSize);
        Assert.Equal(240, project.Engine.Theme.Dialogue.Height);
        await using var worker = new ProcessGameSession(project, _ => { });
        await using var runtime = new LuaRuntime(project, _ => { });
        Assert.Equal(TimeSpan.FromSeconds(7), worker.ExecutionLimit);
        Assert.Equal(TimeSpan.FromSeconds(7), runtime.ExecutionLimit);
        Assert.Equal("Читать", project.Engine.Game.StartLabel);
        Assert.False(project.Engine.Game.AutoSave);
    }

    [Theory]
    [InlineData("window.json", "{\"width\":639}")]
    [InlineData("window.json", "{\"width\":7681}")]
    [InlineData("window.json", "{\"height\":479}")]
    [InlineData("window.json", "{\"height\":4321}")]
    [InlineData("window.json", "{\"position\":\"offscreen\"}")]
    [InlineData("window.json", "{\"mode\":\"borderless\"}")]
    [InlineData("game.json", "{\"execution_timeout_seconds\":0}")]
    [InlineData("game.json", "{\"execution_timeout_seconds\":3601}")]
    [InlineData("game.json", "{\"start_label\":\"\"}")]
    [InlineData("theme.json", "{\"background_color\":\"invalid\"}")]
    [InlineData("theme.json", "{\"background_color\":null}")]
    [InlineData("theme.json", "{\"dialogue\":null}")]
    [InlineData("theme.json", "{\"dialogue\":{\"height\":119}}")]
    [InlineData("theme.json", "{\"dialogue\":{\"font_size\":61}}")]
    [InlineData("theme.json", "{\"dialogue\":{\"margin\":-1}}")]
    [InlineData("theme.json", "{\"dialogue\":{\"height\":120,\"padding\":80}}")]
    [InlineData("theme.json", "{\"menu\":null}")]
    [InlineData("theme.json", "{\"menu\":{\"width\":239}}")]
    [InlineData("theme.json", "{\"menu\":{\"width\":561}}")]
    [InlineData("theme.json", "{\"menu\":{\"alignment\":\"top\"}}")]
    [InlineData("theme.json", "{\"unknown_color\":\"Red\"}")]
    [InlineData("state.json", "[]")]
    [InlineData("state.json", "null")]
    [InlineData("state.json", "{\"x\":null}")]
    [InlineData("state.json", "{\"x\":[null]}")]
    [InlineData("state.json", "{\"x\":1,\"x\":2}")]
    [InlineData("state.json", "{\"x\":1e999}")]
    [InlineData("window.json", "null")]
    [InlineData("game.json", "{")]
    public async Task InvalidSettings_AreRejectedBeforeLaunch(string file, string json)
    {
        using var fixture = new TempProject();
        fixture.Write("engine/" + file, json);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.LoadAsync());
        Assert.Contains("engine", error.Message);
    }

    [Theory]
    [InlineData(640, 480, 1)]
    [InlineData(7680, 4320, 3600)]
    public async Task ExactSizeAndTimeoutBoundaries_AreAccepted(int width, int height, int timeout)
    {
        using var fixture = new TempProject();
        fixture.Write("engine/window.json", JsonSerializer.Serialize(new { width, height }));
        fixture.Write("engine/game.json", JsonSerializer.Serialize(new { execution_timeout_seconds = timeout }));
        var engine = (await fixture.LoadAsync()).Engine;
        Assert.Equal(width, engine.Window.Width);
        Assert.Equal(height, engine.Window.Height);
        Assert.Equal(timeout, engine.Game.ExecutionTimeoutSeconds);
    }

    [Fact]
    public async Task BackgroundPath_RejectsTraversalAndMissingImage()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/theme.json", """{"background_image":"../outside.png"}""");
        await Assert.ThrowsAsync<ArgumentException>(() => fixture.LoadAsync());
        fixture.Write("engine/theme.json", """{"background_image":"game/backgrounds/missing.png"}""");
        await Assert.ThrowsAsync<FileNotFoundException>(() => fixture.LoadAsync());
    }

    [Fact]
    public async Task StateDepthAndFileSize_RejectOversizedConfiguration()
    {
        using var fixture = new TempProject();
        string nested = "true";
        for (int i = 0; i < 17; i++) nested = "{\"child\":" + nested + "}";
        fixture.Write("engine/state.json", nested);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.LoadAsync());
        fixture.Write("engine/state.json", nested[9..^1]);
        var allowed = (await fixture.LoadAsync()).Engine.InitialState;
        for (int i = 0; i < 16; i++) allowed = allowed.GetProperty("child");
        Assert.True(allowed.GetBoolean());
        fixture.Write("engine/state.json", "{}");
        fixture.Write("engine/theme.json", new string(' ', 256 * 1024 + 1));
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => fixture.LoadAsync());
        Assert.Contains("theme.json", error.Message);
        Assert.Contains("256", error.Message);
    }

    [Fact]
    public async Task StateDefaults_LoadBeforeInit_ReplayAndPresentationChangesKeepSavesCompatible()
    {
        using var fixture = new TempProject();
        fixture.Write("engine/state.json", """{"score":41,"profile":{"name":"Алиса\n🌸","active":true}}""");
        fixture.Write("game/scripts/gamelogic.lua", "state.score = state.score + 1");
        fixture.Write("game/scenes/main.krl", "function main() d(state.profile.name, tostring(state.score)); state.score = state.score + 1; d(nil, tostring(state.score)) end");
        var project = await fixture.LoadAsync();
        await using var runtime = new ProcessGameSession(project, _ => { });
        await runtime.StartAsync();
        Assert.Equal("Алиса\n🌸", runtime.Story.Speaker);
        Assert.Equal("42", runtime.Story.Text);
        await runtime.AdvanceAsync();
        var checkpoint = await runtime.GetCheckpointAsync();
        Assert.Equal(43, checkpoint.State.GetProperty("score").GetInt32());
        Assert.True(checkpoint.State.GetProperty("profile").GetProperty("active").GetBoolean());
        fixture.Write("engine/theme.json", """{"background_color":"#FF123456"}""");
        fixture.Write("engine/window.json", """{"width":1440,"height":900}""");
        await using var restored = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await restored.RestoreAsync(checkpoint);
        Assert.Equal("43", restored.Story.Text);
        Assert.Equal(1440, restored.WindowSettings.Width);
        Assert.Equal("#FF123456", restored.WindowSettings.Background);
        fixture.Write("engine/state.json", """{"score":99,"profile":{"name":"Алиса\n🌸","active":true}}""");
        await using var incompatible = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await Assert.ThrowsAsync<InvalidDataException>(() => incompatible.RestoreAsync(checkpoint));
    }

    [Fact]
    public async Task LuaWindowCommands_OverrideEngineDefaults()
    {
        using var fixture = new TempProject();
        fixture.Write("game/scenes/main.krl", "function main() set_window_title('Scene'); set_window_size(1000,700); set_bg_color('#FF123456'); d(nil,'ready') end");
        await using var runtime = new ProcessGameSession(await fixture.LoadAsync(), _ => { });
        await runtime.StartAsync();
        Assert.Equal(new RuntimeWindowSettings("Scene", 1000, 700, "#FF123456"), runtime.WindowSettings);
        Assert.Equal("ready", runtime.Story.Text);
    }
}
