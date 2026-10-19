using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class PerformanceSettingsTests
{
    [Fact]
    public void Graphics_DefaultsAndNormalizationPreserveIndependentValidPreferences()
    {
        Assert.Equal(new GraphicsSettings(), GraphicsSettings.Normalize(null));
        var source = new GraphicsSettings { Api = "invalid", MaxFps = -1, MsaaSamples = 3, VSync = false };
        var result = GraphicsSettings.Normalize(source);
        Assert.Equal("auto", result.Api); Assert.Equal(60, result.MaxFps); Assert.Equal(0, result.MsaaSamples); Assert.False(result.VSync);
        Assert.Equal("invalid", source.Api); Assert.Equal(-1, source.MaxFps); Assert.Equal(3, source.MsaaSamples);
        var valid = new GraphicsSettings { Api = "vulkan", MaxFps = 144, MsaaSamples = 4, VSync = false };
        Assert.Equal(valid, GraphicsSettings.Normalize(valid));
        Assert.Equal(new GraphicsSettings { Api = "opengl", MaxFps = 60, MsaaSamples = 2 }, GraphicsSettings.Normalize(new() { Api = "opengl", MaxFps = 61, MsaaSamples = 2 }));
    }

    [Fact]
    public void Graphics_EveryAdvertisedApiFrameRateAndMsaaCombinationIsAccepted()
    {
        Assert.Equal(new[] { "auto", "vulkan", "opengl" }, GraphicsSettings.Apis.Select(x => x.Value));
        Assert.Equal(new[] { 30, 60, 144, 0 }, GraphicsSettings.FrameRates.Select(x => x.Value));
        Assert.Equal(new[] { 0, 2, 4 }, GraphicsSettings.Antialiasing.Select(x => x.Value));
        foreach (string api in GraphicsSettings.Apis.Select(x => x.Value))
        foreach (int fps in GraphicsSettings.FrameRates.Select(x => x.Value))
        foreach (int samples in GraphicsSettings.Antialiasing.Select(x => x.Value))
        {
            var value = new GraphicsSettings { Api = api, MaxFps = fps, MsaaSamples = samples };
            value.Validate(); Assert.Equal(value, GraphicsSettings.Normalize(value));
        }
    }

    [Theory]
    [InlineData(null, 60, 0)] [InlineData("", 60, 0)] [InlineData("directx", 60, 0)] [InlineData("Vulkan", 60, 0)]
    [InlineData("auto", -1, 0)] [InlineData("auto", 29, 0)] [InlineData("auto", 31, 0)] [InlineData("auto", 59, 0)]
    [InlineData("auto", 61, 0)] [InlineData("auto", 143, 0)] [InlineData("auto", 145, 0)]
    [InlineData("auto", 60, -1)] [InlineData("auto", 60, 1)] [InlineData("auto", 60, 3)] [InlineData("auto", 60, 5)]
    public void Graphics_RejectsUnsupportedDiscriminatorsAndAdjacentNumericValues(string? api, int fps, int samples) =>
        Assert.Throws<InvalidDataException>(() => new GraphicsSettings { Api = api!, MaxFps = fps, MsaaSamples = samples }.Validate());

    [Fact]
    public void ProjectDefaults_SceneDefaultsAndAllCounterCornersAreValid()
    {
        using var fixture = new TempProject(); var project = new ProjectPerformanceSettings();
        Assert.Equal(new GraphicsSettings(), project.Graphics); Assert.False(project.FpsCounter); Assert.Equal("top_right", project.FpsCorner);
        Assert.False(project.StatisticsOverlay); Assert.True(project.LoadingIndicator); Assert.False(project.DebugMode); Assert.True(project.Preloading);
        Assert.Equal(Path.Combine(fixture.ProjectPath, "saves"), project.ResolveSavesPath(fixture.ProjectPath));
        foreach (string corner in new[] { "top_left", "top_right", "bottom_left", "bottom_right" }) { project.FpsCorner = corner; project.Validate(fixture.ProjectPath); }
        var scene = new ScenePerformanceSettings(); Assert.Equal("high", scene.EffectsQuality); Assert.True(scene.PostProcessing);
        foreach (string quality in new[] { "high", "medium", "low" }) new ScenePerformanceSettings { EffectsQuality = quality, PostProcessing = false }.Validate();
        Assert.Throws<InvalidDataException>(() => new ScenePerformanceSettings { EffectsQuality = "ultra" }.Validate());
        Assert.Throws<InvalidDataException>(() => new ProjectPerformanceSettings { Graphics = null! }.Validate(fixture.ProjectPath));
        Assert.Throws<InvalidDataException>(() => new ProjectPerformanceSettings { FpsCorner = "center" }.Validate(fixture.ProjectPath));
    }

    [Fact]
    public void SavesPath_ConfinesRelativePathsButAllowsAnExplicitExternalDirectoryAndRejectsRoots()
    {
        using var fixture = new TempProject();
        Assert.Equal(Path.Combine(fixture.ProjectPath, "custom", "slots"), new ProjectPerformanceSettings { SavesPath = "custom/slots" }.ResolveSavesPath(fixture.ProjectPath));
        string external = Path.Combine(fixture.Root, "external-saves");
        Assert.Equal(external, new ProjectPerformanceSettings { SavesPath = external }.ResolveSavesPath(fixture.ProjectPath));
        foreach (string invalid in new[] { "", " ", fixture.ProjectPath, Path.GetPathRoot(fixture.ProjectPath)! })
            Assert.Throws<InvalidDataException>(() => new ProjectPerformanceSettings { SavesPath = invalid }.ResolveSavesPath(fixture.ProjectPath));
        Assert.Throws<ArgumentException>(() => new ProjectPerformanceSettings { SavesPath = "../escape" }.ResolveSavesPath(fixture.ProjectPath));
        Assert.Throws<ArgumentException>(() => new ProjectPerformanceSettings { SavesPath = "." }.ResolveSavesPath(fixture.ProjectPath));
    }

    [Fact]
    public async Task Store_RoundTripsEveryProjectSwitchAndIndependentSceneOverrides_AndEngineLoadsThem()
    {
        using var fixture = new TempProject();
        var project = new ProjectPerformanceSettings { Graphics = new() { Api = "opengl", VSync = false, MaxFps = 144, MsaaSamples = 4 },
            FpsCounter = true, FpsCorner = "bottom_left", StatisticsOverlay = true, LoadingIndicator = false, DebugMode = true, Preloading = false, SavesPath = "my-saves" };
        await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, project);
        var scenes = new ScenePerformanceDocument { Scenes = new() { ["game\\scenes\\first.knegraph"] = new() { EffectsQuality = "low", PostProcessing = false }, ["second.knegraph"] = new() { EffectsQuality = "medium" } } };
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, scenes);
        var loaded = PerformanceSettingsStore.LoadProject(fixture.ProjectPath);
        Assert.Equal(JsonSerializer.Serialize(project, EngineSettings.JsonOptions), JsonSerializer.Serialize(loaded, EngineSettings.JsonOptions));
        var loadedScenes = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath);
        Assert.Equal(2, loadedScenes.Scenes.Count); Assert.Equal("low", loadedScenes.For("GAME/SCENES/FIRST.KNEGRAPH").EffectsQuality);
        Assert.False(loadedScenes.For("game\\scenes\\first.knegraph").PostProcessing); Assert.Equal("medium", loadedScenes.For("second.knegraph").EffectsQuality);
        Assert.True(loadedScenes.For("second.knegraph").PostProcessing);
        var absent = loadedScenes.For("absent.knegraph"); absent.EffectsQuality = "low";
        Assert.Equal("high", loadedScenes.For("absent.knegraph").EffectsQuality); Assert.Equal("high", loadedScenes.For(null).EffectsQuality); Assert.Equal(2, loadedScenes.Scenes.Count);
        var engine = (await fixture.LoadAsync()).Engine;
        Assert.True(engine.Performance.DebugMode); Assert.Equal("opengl", engine.Performance.Graphics.Api); Assert.Equal("low", engine.ScenePerformance.For("game/scenes/first.knegraph").EffectsQuality);
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.ProjectPath, "engine"), "*.tmp"));
    }

    [Fact]
    public async Task Templates_CreateBothFiles_AndLegacyAbsenceUsesDefaultsWithoutWritingFiles()
    {
        using var fixture = new TempProject();
        foreach (string file in new[] { "performance.json", "scene-performance.json" }) Assert.True(File.Exists(Path.Combine(fixture.ProjectPath, "engine", file)));
        Assert.Equal("auto", PerformanceSettingsStore.LoadProject(fixture.ProjectPath).Graphics.Api); Assert.Empty(PerformanceSettingsStore.LoadScenes(fixture.ProjectPath).Scenes);
        foreach (string file in new[] { "performance.json", "scene-performance.json" }) File.Delete(Path.Combine(fixture.ProjectPath, "engine", file));
        var engine = (await fixture.LoadAsync()).Engine;
        Assert.Equal(new GraphicsSettings(), engine.Performance.Graphics); Assert.True(engine.Performance.Preloading); Assert.Empty(engine.ScenePerformance.Scenes);
        foreach (string file in new[] { "performance.json", "scene-performance.json" }) Assert.False(File.Exists(Path.Combine(fixture.ProjectPath, "engine", file)));
    }

    [Theory]
    [InlineData("performance.json", "null")] [InlineData("performance.json", "{")] [InlineData("performance.json", "{\"unknown\":true}")]
    [InlineData("performance.json", "{\"graphics\":null}")] [InlineData("performance.json", "{\"graphics\":{\"api\":\"dx12\"}}")]
    [InlineData("performance.json", "{\"saves_path\":\"\"}")] [InlineData("performance.json", "{\"fps_corner\":null}")]
    [InlineData("scene-performance.json", "null")] [InlineData("scene-performance.json", "[]")]
    [InlineData("scene-performance.json", "{\"version\":0}")] [InlineData("scene-performance.json", "{\"version\":2}")]
    [InlineData("scene-performance.json", "{\"scenes\":null}")] [InlineData("scene-performance.json", "{\"scenes\":{\"one.knegraph\":null}}")]
    [InlineData("scene-performance.json", "{\"scenes\":{\"one.krl\":{}}}")]
    [InlineData("scene-performance.json", "{\"scenes\":{\"one.knegraph\":{\"effects_quality\":null}}}")]
    [InlineData("scene-performance.json", "{\"scenes\":{\"one.knegraph\":{\"unknown\":0}}}")]
    [InlineData("scene-performance.json", "{\"scenes\":{\"one.knegraph\":{},\"ONE.knegraph\":{}}}")]
    public async Task Store_RejectsMalformedNullUnknownAndInvalidSettingsBeforeLaunching(string file, string json)
    {
        using var fixture = new TempProject(); fixture.Write("engine/" + file, json);
        await Assert.ThrowsAsync<InvalidDataException>(() => fixture.LoadAsync());
    }

    [Fact]
    public void SceneDocument_RejectsEscapesNormalizedDuplicatesAndCapacityOverflow()
    {
        using var fixture = new TempProject();
        var value = new ScenePerformanceDocument { Scenes = new() { ["../outside.knegraph"] = new() } };
        Assert.Throws<ArgumentException>(() => value.Validate(fixture.ProjectPath));
        value.Scenes = new() { ["one.knegraph"] = new(), ["sub/../one.knegraph"] = new() };
        Assert.Throws<InvalidDataException>(() => value.Validate(fixture.ProjectPath));
        value.Scenes = Enumerable.Range(0, 5000).ToDictionary(i => i + ".knegraph", _ => new ScenePerformanceSettings());
        value.Validate(fixture.ProjectPath); value.Scenes.Add("overflow.knegraph", new());
        Assert.Throws<InvalidDataException>(() => value.Validate(fixture.ProjectPath));
    }

    [Theory]
    [InlineData("performance.json")] [InlineData("scene-performance.json")]
    public void Store_ReadLimitAcceptsOneMegabyteAndRejectsTheNextByte(string file)
    {
        using var fixture = new TempProject();
        void Load() { if (file == "performance.json") _ = PerformanceSettingsStore.LoadProject(fixture.ProjectPath); else _ = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath); }
        fixture.Write("engine/" + file, "{}" + new string(' ', 1024 * 1024 - 2)); Load();
        File.AppendAllText(Path.Combine(fixture.ProjectPath, "engine", file), " ");
        Assert.Throws<InvalidDataException>(Load);
    }

    [Fact]
    public async Task Store_CancelledAndInvalidWritesLeavePreviousFilesIntactAndRemoveTemporaryFiles()
    {
        using var fixture = new TempProject(); string engine = Path.Combine(fixture.ProjectPath, "engine");
        string originalProject = File.ReadAllText(Path.Combine(engine, "performance.json")), originalScenes = File.ReadAllText(Path.Combine(engine, "scene-performance.json"));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { FpsCounter = true }, canceled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Scenes = new() { ["one.knegraph"] = new() } }, canceled.Token));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PerformanceSettingsStore.SaveProjectAsync(fixture.ProjectPath, new() { Graphics = new() { MaxFps = 15 } }));
        await Assert.ThrowsAsync<InvalidDataException>(async () => await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Version = 2 }));
        Assert.Equal(originalProject, File.ReadAllText(Path.Combine(engine, "performance.json"))); Assert.Equal(originalScenes, File.ReadAllText(Path.Combine(engine, "scene-performance.json")));
        Assert.Empty(Directory.GetFiles(engine, "*.tmp"));
    }

    [Fact]
    public async Task UserGraphics_MissingInvalidAndExplicitValuesRecoverOrRoundTripWithoutLosingDesignOrHistory()
    {
        using var fixture = new AccessibilitySettingsTests.SettingsFixture();
        foreach (string json in new[] { "{}", "{\"Graphics\":null}", "{\"Graphics\":{\"Api\":\"dx\",\"MaxFps\":-1,\"MsaaSamples\":8}}" })
        {
            await File.WriteAllTextAsync(fixture.Store.SettingsPath, json); Assert.Equal(new GraphicsSettings(), (await fixture.Store.LoadAsync()).Graphics);
        }
        var graphics = new GraphicsSettings { Api = "vulkan", VSync = false, MaxFps = 30, MsaaSamples = 2 };
        await fixture.Store.SaveAsync(new() { Graphics = graphics, Theme = "ocean", AccessibilityMode = "tritanopia", WelcomeObjects = "stars", UserName = "Тест", RecentProjects = [fixture.Root] });
        var loaded = await fixture.Store.LoadAsync(); Assert.Equal(graphics, loaded.Graphics); Assert.Equal("ocean", loaded.Theme); Assert.Equal("tritanopia", loaded.AccessibilityMode);
        Assert.Equal("stars", loaded.WelcomeObjects); Assert.Equal("Тест", loaded.UserName); Assert.Equal([fixture.Root], loaded.RecentProjects);
    }
}
