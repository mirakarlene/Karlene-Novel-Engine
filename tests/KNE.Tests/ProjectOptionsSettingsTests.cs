using System.IO;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace KNE.Tests;

public sealed class ProjectOptionsSettingsTests
{
    [Fact]
    public async Task MissingNewSettingsKeepCompatibleDefaultsWithoutCreatingFiles()
    {
        using var fixture = new TempProject();
        string securityPath = Path.Combine(fixture.ProjectPath, "engine/security.json"); File.Delete(securityPath);
        fixture.Write("engine/game.json", "{\"auto_save\":false,\"subtitle\":\"Legacy\"}");
        fixture.Write("engine/performance.json", "{\"preloading\":false}");
        var before = Directory.EnumerateFiles(fixture.ProjectPath, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);
        var loaded = await fixture.LoadAsync();
        Assert.False(loaded.Engine.Game.AutoSave); Assert.Equal("Legacy", loaded.Engine.Game.Subtitle);
        Assert.Equal(100, loaded.Engine.Game.RollbackDepth); Assert.Equal("dialogue", loaded.Engine.Game.AutoSaveInterval);
        Assert.Equal(10, loaded.Engine.Game.AutoSaveActions); Assert.Null(loaded.Engine.Game.MenuMusic);
        Assert.False(loaded.Engine.Performance.Preloading); Assert.Equal(3, loaded.Engine.Performance.PreloadScenes);
        Assert.False(loaded.Engine.Security.EncryptScripts); Assert.False(loaded.Engine.Security.ReleaseDebugConsole);
        Assert.True(loaded.Engine.Security.CrashReports); Assert.True(loaded.Engine.Security.AllowPlugins); Assert.True(loaded.Engine.Security.AllowNetwork);
        Assert.Equal("full", loaded.Engine.Security.FileSystem); Assert.False(File.Exists(securityPath));
        Assert.Equal(before.Keys.Order(), Directory.EnumerateFiles(fixture.ProjectPath, "*", SearchOption.AllDirectories).Order());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    [Theory]
    [InlineData(0, 1, "dialogue")] [InlineData(1, 1000, "choice")] [InlineData(1000, 10, "actions")]
    public async Task RollbackAndAutosaveBoundariesAreAcceptedAndPersistWithoutClamping(int depth, int actions, string interval)
    {
        using var fixture = new TempProject();
        var game = new GameDefaults { RollbackDepth = depth, AutoSaveActions = actions, AutoSaveInterval = interval };
        game.Validate(fixture.ProjectPath);
        fixture.Write("engine/game.json", JsonSerializer.Serialize(game, EngineSettings.JsonOptions));
        var loaded = (await fixture.LoadAsync()).Engine.Game;
        Assert.Equal(depth, loaded.RollbackDepth); Assert.Equal(actions, loaded.AutoSaveActions); Assert.Equal(interval, loaded.AutoSaveInterval);
    }

    [Theory]
    [InlineData(-1, 1, "dialogue")] [InlineData(1001, 1, "dialogue")]
    [InlineData(0, 0, "dialogue")] [InlineData(0, 1001, "dialogue")]
    [InlineData(0, 1, "")] [InlineData(0, 1, "Dialogue")] [InlineData(0, 1, null)]
    public void InvalidRollbackOrAutosaveConfigurationFailsBeforeRuntime(int depth, int actions, string? interval)
    {
        using var fixture = new TempProject();
        var game = new GameDefaults { RollbackDepth = depth, AutoSaveActions = actions, AutoSaveInterval = interval! };
        Assert.Throws<InvalidDataException>(() => game.Validate(fixture.ProjectPath));
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public async Task EachPreloadCountRoundTripsIndependentlyOfTheEnableSwitch(int count)
    {
        using var fixture = new TempProject();
        foreach (bool enabled in new[] { false, true })
        {
            await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Preloading = enabled, PreloadScenes = count });
            var loaded = (await fixture.LoadAsync()).Engine.Performance;
            Assert.Equal(enabled, loaded.Preloading); Assert.Equal(count, loaded.PreloadScenes);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(4)]
    public void PreloadCountRejectsAdjacentUnsupportedValues(int count)
    {
        using var fixture = new TempProject();
        Assert.Throws<InvalidDataException>(() => new ProjectPerformanceSettings { PreloadScenes = count }.Validate(fixture.ProjectPath));
    }

    [Theory]
    [InlineData(1, false)] [InlineData(2, false)] [InlineData(3, false)]
    [InlineData(1, true)] [InlineData(2, true)] [InlineData(3, true)]
    public async Task PreloadCountActuallyLimitsCachedDirectSceneTargetsForCodeAndGraphs(int count, bool code)
    {
        using var fixture = new TempProject();
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { PreloadScenes = count });
        for (int index = 0; index < 4; index++)
        {
            string relative = "images/target" + index + ".png"; WritePng(fixture, relative);
            if (code) fixture.Write("target" + index + ".krl", "function target" + index + "() error('preload must not execute'); background('" + relative + "') end");
            else fixture.Write("target" + index + ".knegraph", new VisualScript { FunctionName = "target" + index, Nodes = new() { new ScriptNode { Id = "bg", Kind = ScriptNodeKind.Background, Path = relative } } }.Serialize());
        }
        string entry;
        if (code)
        {
            entry = "entry.krl"; fixture.Write(entry, "function entry() " + string.Join(";", Enumerable.Range(0, 4).Select(index => "call(target" + index + ")")) + " end");
        }
        else
        {
            entry = "entry.knegraph";
            fixture.Write(entry, new VisualScript { FunctionName = "entry", Nodes = Enumerable.Range(0, 4).Select(index => new ScriptNode { Id = "call" + index, Kind = ScriptNodeKind.CallScene, Path = "target" + index }).ToList() }.Serialize());
        }
        var project = await fixture.LoadAsync(); var cache = new SceneAssetCache();
        string stateBefore = project.Engine.InitialState.GetRawText();
        await cache.PreloadAsync(project, entry, CancellationToken.None);
        Assert.Equal(count, cache.CachedCount); Assert.Equal(count * 4, cache.CachedBytes);
        Assert.Equal(stateBefore, project.Engine.InitialState.GetRawText());
        // The selected prefix is in the cache; the first excluded target requires a new decode.
        for (int index = 0; index < count; index++) _ = cache.Load(Path.Combine(fixture.ProjectPath, "images/target" + index + ".png"));
        Assert.Equal(count, cache.CachedCount);
        _ = cache.Load(Path.Combine(fixture.ProjectPath, "images/target" + count + ".png")); Assert.Equal(count + 1, cache.CachedCount);
    }

    [Theory]
    [InlineData("project")] [InlineData("full")]
    public async Task SecurityPolicyRoundTripsEveryFlagAndFilesystemMode(string mode)
    {
        using var fixture = new TempProject();
        var value = new ProjectSecuritySettings { EncryptScripts = true, ReleaseDebugConsole = true, CrashReports = false, AllowPlugins = false, AllowNetwork = false, FileSystem = mode };
        value.Validate(); fixture.Write("engine/security.json", JsonSerializer.Serialize(value, EngineSettings.JsonOptions));
        var loaded = (await fixture.LoadAsync()).Engine.Security;
        Assert.True(loaded.EncryptScripts); Assert.True(loaded.ReleaseDebugConsole); Assert.False(loaded.CrashReports);
        Assert.False(loaded.AllowPlugins); Assert.False(loaded.AllowNetwork); Assert.Equal(mode, loaded.FileSystem);
    }

    [Theory]
    [InlineData("")] [InlineData("Project")] [InlineData("elsewhere")] [InlineData(null)]
    public void InvalidFilesystemPolicyFailsClosed(string? mode) => Assert.Throws<InvalidDataException>(() => new ProjectSecuritySettings { FileSystem = mode! }.Validate());

    public static IEnumerable<object[]> GamePaletteIds => EditorTheme.BuiltInThemes.Select(theme => new object[] { theme.Id });

    [Fact]
    public void SeventeenNamedGamePalettesHaveDistinctColorFamilies()
    {
        Assert.Equal(17, EditorTheme.BuiltInThemes.Count);
        var backgrounds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var option in EditorTheme.BuiltInThemes)
        {
            var theme = new GameTheme(); GamePalette.Apply(theme, ScreenLayouts.CreateDefaults(theme), option.Id);
            Assert.True(backgrounds.Add(theme.BackgroundColor), "Palette shares another background: " + option.Id);
        }
        Assert.Equal(17, backgrounds.Count);
    }

    [Theory]
    [MemberData(nameof(GamePaletteIds))]
    public void AllSeventeenGamePalettesUpdateGlobalColorsWhilePreservingAuthoredLayoutAndArtwork(string palette)
    {
        var theme = new GameTheme { BackgroundImage = "art/background.png", CharacterWidth = 777, FontFamily = "Arial" };
        theme.Dialogue.Height = 333; theme.Dialogue.FontSize = 29; theme.Menu.Width = 399;
        var screens = ScreenLayouts.CreateDefaults(theme); screens.Screens[0].Elements[0].X = 123;
        var custom = screens.AddScreen("Custom authored"); custom.Elements[0].Image = "art/custom.png"; custom.Elements[0].MotionY = 77;
        string layoutBefore = JsonSerializer.Serialize(screens.Screens, EngineSettings.JsonOptions);
        string oldBackground = theme.BackgroundColor, oldText = theme.TextColor;
        GamePalette.Apply(theme, screens, palette);
        Assert.Equal(palette, theme.Palette); Assert.Same(theme, screens.Theme);
        Assert.NotEqual(oldBackground, theme.BackgroundColor); Assert.NotEqual(oldText, theme.TextColor);
        Assert.Equal(theme.TextColor, theme.Dialogue.TextColor); Assert.Equal(theme.AccentColor, theme.Dialogue.SpeakerColor);
        Assert.Equal("art/background.png", theme.BackgroundImage); Assert.Equal(777, theme.CharacterWidth); Assert.Equal("Arial", theme.FontFamily);
        Assert.Equal(333, theme.Dialogue.Height); Assert.Equal(29, theme.Dialogue.FontSize); Assert.Equal(399, theme.Menu.Width);
        Assert.Equal(layoutBefore, JsonSerializer.Serialize(screens.Screens, EngineSettings.JsonOptions));
        screens.Validate();
    }

    private static void WritePng(TempProject fixture, string relative)
    {
        string path = PathSafety.ResolveRelative(fixture.ProjectPath, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var bitmap = BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[] { 10, 20, 30, 255 }, 4);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var file = File.Create(path); encoder.Save(file);
    }
}
