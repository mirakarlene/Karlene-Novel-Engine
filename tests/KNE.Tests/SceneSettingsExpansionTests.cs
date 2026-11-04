using System.IO;
using System.Text.Json;
using Xunit;

namespace KNE.Tests;

public sealed class SceneSettingsExpansionTests
{
    [Fact]
    public void LegacySceneFilesKeepInheritedDefaultsAndTheirOriginalBytesUntilSaved()
    {
        using var fixture = new TempProject();
        string path = fixture.Write("engine/scene-performance.json", "{\"version\":1,\"scenes\":{\"chapter.krl\":{\"effects_quality\":\"low\",\"post_processing\":false,\"hide_shaders\":true}}}");
        byte[] before = File.ReadAllBytes(path);
        var document = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath); var settings = document.For("chapter.krl", "chapter");
        Assert.Equal("low", settings.EffectsQuality); Assert.False(settings.PostProcessing); Assert.True(settings.HideShaders);
        Assert.Equal("", settings.Name); Assert.Null(settings.WindowWidth); Assert.Null(settings.WindowHeight);
        Assert.Null(settings.Music); Assert.True(settings.LoopMusic); Assert.Null(settings.MusicVolume); Assert.False(settings.StopMusicOnExit);
        Assert.True(settings.AllowSaves); Assert.True(settings.AllowAutosave); Assert.True(settings.AllowTriggers); Assert.False(settings.CheckpointOnEntry);
        Assert.Null(settings.MaxCallDepth); Assert.Equal(JsonValueKind.Object, settings.InitialState.ValueKind); Assert.Empty(settings.InitialState.EnumerateObject());
        Assert.Equal("none", settings.EntryTransition.Kind); Assert.Equal("none", settings.ExitTransition.Kind);
        Assert.Empty(document.Functions); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task FunctionOverridesRoundTripAllOptionsIndependentlyAndFallbackToLegacyFileDefaults()
    {
        using var fixture = new TempProject(); fixture.Write("music/chapter.wav", "resource fixture"); fixture.Write("effects/entry.kneshader", "shader fixture");
        var state = JsonSerializer.SerializeToElement(new { score = 7, hero = new { name = "Герой", flags = new[] { true, false } }, empty = (string?)null });
        var first = new ScenePerformanceSettings { Name = "Пролог", WindowWidth = 1664, WindowHeight = 936, Music = "music/chapter.wav", LoopMusic = false, MusicVolume = 25, StopMusicOnExit = true,
            EntryTransition = new() { Kind = "custom", Shader = "effects/entry.kneshader", Duration = 1.5 }, ExitTransition = new() { Kind = "dissolve", Duration = 2.75 },
            AllowSaves = false, AllowAutosave = false, CheckpointOnEntry = true, InitialState = state, MaxCallDepth = 3, AllowTriggers = false, EffectsQuality = "medium", PostProcessing = false, HideShaders = true };
        var second = new ScenePerformanceSettings { Name = "Финал", MusicVolume = 100, EntryTransition = new() { Kind = "fade", Duration = .5 } };
        var document = new ScenePerformanceDocument { Scenes = new() { ["nested/chapter.krl"] = new() { EffectsQuality = "low" } },
            Functions = new() { ["nested\\chapter.krl"] = new() { ["opening"] = first, ["ending"] = second } } };
        await PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, document);
        var loaded = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath);
        Assert.Single(loaded.Scenes); Assert.Single(loaded.Functions); Assert.Equal(2, loaded.Functions["nested/chapter.krl"].Count);
        var actual = loaded.For("NESTED/CHAPTER.KRL", "opening");
        Assert.Equal("Пролог", actual.Name); Assert.Equal(1664, actual.WindowWidth); Assert.Equal(936, actual.WindowHeight);
        Assert.Equal("music/chapter.wav", actual.Music); Assert.False(actual.LoopMusic); Assert.Equal(25, actual.MusicVolume); Assert.True(actual.StopMusicOnExit);
        Assert.Equal("custom", actual.EntryTransition.Kind); Assert.Equal("effects/entry.kneshader", actual.EntryTransition.Shader); Assert.Equal(1.5, actual.EntryTransition.Duration);
        Assert.Equal("dissolve", actual.ExitTransition.Kind); Assert.Equal(2.75, actual.ExitTransition.Duration);
        Assert.False(actual.AllowSaves); Assert.False(actual.AllowAutosave); Assert.True(actual.CheckpointOnEntry); Assert.False(actual.AllowTriggers); Assert.Equal(3, actual.MaxCallDepth);
        Assert.Equal("medium", actual.EffectsQuality); Assert.False(actual.PostProcessing); Assert.True(actual.HideShaders);
        Assert.Equal(7, actual.InitialState.GetProperty("score").GetInt32()); Assert.Equal("Герой", actual.InitialState.GetProperty("hero").GetProperty("name").GetString());
        Assert.Equal(new[] { true, false }, actual.InitialState.GetProperty("hero").GetProperty("flags").EnumerateArray().Select(v => v.GetBoolean())); Assert.Equal(JsonValueKind.Null, actual.InitialState.GetProperty("empty").ValueKind);
        var end = loaded.For("nested\\chapter.krl", "ending"); Assert.Equal("Финал", end.Name); Assert.Equal(100, end.MusicVolume); Assert.True(end.AllowSaves); Assert.True(end.AllowAutosave); Assert.True(end.PostProcessing);
        Assert.Equal("low", loaded.For("nested/chapter.krl", "OPENING").EffectsQuality); Assert.Equal("low", loaded.For("nested/chapter.krl", "missing").EffectsQuality);
        Assert.Equal("high", loaded.For("missing.krl", "missing").EffectsQuality); Assert.Empty(Directory.GetFiles(Path.Combine(fixture.ProjectPath, "engine"), "*.tmp"));
    }

    [Theory]
    [InlineData(640, 480)] [InlineData(7680, 4320)] [InlineData(1664, 936)]
    public async Task ResolutionOverridesAcceptProjectSupportedBoundariesWithoutRequiringAStandardPreset(int width, int height)
    {
        using var fixture = new TempProject();
        await SaveSingle(fixture, new() { WindowWidth = width, WindowHeight = height });
        var actual = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath).For("chapter.krl", "chapter");
        Assert.Equal(width, actual.WindowWidth); Assert.Equal(height, actual.WindowHeight);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(100, 63)] [InlineData(37, 9)]
    public async Task VolumeAndRelativeCallBudgetAcceptBothEndpointsAndInteriorValues(int volume, int calls)
    {
        using var fixture = new TempProject();
        await SaveSingle(fixture, new() { MusicVolume = volume, MaxCallDepth = calls });
        var actual = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath).For("chapter.krl", "chapter");
        Assert.Equal(volume, actual.MusicVolume); Assert.Equal(calls, actual.MaxCallDepth);
    }

    [Fact]
    public async Task DisplayNameAcceptsTheFullLengthAndDoesNotRequireALuaIdentifier()
    {
        using var fixture = new TempProject();
        await SaveSingle(fixture, new() { Name = "Глава первая · " + new string('x', 128 - "Глава первая · ".Length) });
        string actual = PerformanceSettingsStore.LoadScenes(fixture.ProjectPath).For("chapter.krl", "chapter").Name;
        Assert.Equal(128, actual.Length); Assert.StartsWith("Глава первая · ", actual); Assert.Equal(new string('x', 113), actual[15..]);
    }

    [Theory]
    [InlineData("width", "639")] [InlineData("width", "7681")] [InlineData("height", "479")] [InlineData("height", "4321")]
    [InlineData("half_size", "0")] [InlineData("volume", "-1")] [InlineData("volume", "101")]
    [InlineData("calls", "0")] [InlineData("calls", "64")] [InlineData("name", "control")]
    [InlineData("name", "long")] [InlineData("quality", "unsupported")] [InlineData("initial", "[]")]
    [InlineData("initial", "null")] [InlineData("initial", "7")] [InlineData("initial", "undefined")]
    [InlineData("transition", "entry")] [InlineData("transition", "exit")]
    public async Task InvalidSceneOptionsAreRejectedBeforeReplacingThePreviouslySavedDocument(string field, string input)
    {
        using var fixture = new TempProject(); string path = Path.Combine(fixture.ProjectPath, "engine/scene-performance.json"); byte[] before = File.ReadAllBytes(path);
        var value = new ScenePerformanceSettings();
        switch (field)
        {
            case "width": value.WindowWidth = int.Parse(input); value.WindowHeight = 720; break;
            case "height": value.WindowWidth = 1280; value.WindowHeight = int.Parse(input); break;
            case "half_size": value.WindowWidth = 1280; break;
            case "volume": value.MusicVolume = int.Parse(input); break;
            case "calls": value.MaxCallDepth = int.Parse(input); break;
            case "name": value.Name = input == "long" ? new string('x', 129) : "bad\nname"; break;
            case "quality": value.EffectsQuality = input; break;
            case "initial": value.InitialState = input == "undefined" ? default : JsonDocument.Parse(input).RootElement.Clone(); break;
            case "transition": if (input == "entry") value.EntryTransition = null!; else value.ExitTransition = null!; break;
        }
        var document = new ScenePerformanceDocument { Functions = new() { ["chapter.krl"] = new() { ["chapter"] = value } } };
        await Assert.ThrowsAsync<InvalidDataException>(() => PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, document));
        Assert.Equal(before, File.ReadAllBytes(path)); Assert.Empty(Directory.GetFiles(Path.Combine(fixture.ProjectPath, "engine"), "*.tmp"));
    }

    [Theory]
    [InlineData("{\"functions\":null}")]
    [InlineData("{\"functions\":{\"chapter.krl\":null}}")]
    [InlineData("{\"functions\":{\"chapter.krl\":{\"chapter\":null}}}")]
    [InlineData("{\"functions\":{\"chapter.krl\":{\"chapter\":{\"entry_transition\":null}}}}")]
    [InlineData("{\"functions\":{\"chapter.krl\":{\"chapter\":{\"unknown_option\":true}}}}")]
    public void MalformedFunctionSettingsAreRejectedWithoutRewritingTheInvalidSource(string json)
    {
        using var fixture = new TempProject(); string path = fixture.Write("engine/scene-performance.json", json); byte[] before = File.ReadAllBytes(path);
        Assert.Throws<InvalidDataException>(() => PerformanceSettingsStore.LoadScenes(fixture.ProjectPath)); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void InitialStateLimitCountsUtf8BytesAndAcceptsTheExactObjectBoundary(bool unicode)
    {
        int payloadLength = unicode ? (65536 - 8) / 2 : 65536 - 8;
        string payload = new(unicode ? 'я' : 'x', payloadLength);
        var valid = new ScenePerformanceSettings { InitialState = JsonDocument.Parse("{\"x\":\"" + payload + "\"}").RootElement.Clone() };
        Assert.Equal(65536, System.Text.Encoding.UTF8.GetByteCount(valid.InitialState.GetRawText())); valid.Validate();
        valid.InitialState = JsonDocument.Parse("{\"x\":\"" + payload + "x\"}").RootElement.Clone(); Assert.Throws<InvalidDataException>(() => valid.Validate());
    }

    [Theory]
    [InlineData("{\"x\":1,\"x\":2}")] [InlineData("{\"nested\":{\"x\":1,\"x\":2}}")]
    [InlineData("{\"number\":1e999}")] [InlineData("{\"items\":[null]}")] [InlineData("{\"items\":[1,{\"nested\":[null]}]}")]
    public void InitialStateRejectsDuplicateKeysNonfiniteNumbersAndNullArrayElements(string json)
        => Assert.Throws<InvalidDataException>(() => new ScenePerformanceSettings { InitialState = JsonDocument.Parse(json).RootElement.Clone() }.Validate());

    [Fact]
    public void InitialStateAcceptsDepthSixteenAndRejectsTheNextLevel()
    {
        string json = "1"; for (int depth = 0; depth < 16; depth++) json = "{\"x\":" + json + "}";
        var valid = new ScenePerformanceSettings { InitialState = JsonDocument.Parse(json).RootElement.Clone() }; valid.Validate();
        int count = 0; var current = valid.InitialState; while (current.ValueKind == JsonValueKind.Object) { current = current.GetProperty("x"); count++; } Assert.Equal(16, count); Assert.Equal(1, current.GetInt32());
        valid.InitialState = JsonDocument.Parse("{\"x\":" + json + "}").RootElement.Clone(); Assert.Throws<InvalidDataException>(() => valid.Validate());
    }

    [Theory]
    [InlineData("none", .5)] [InlineData("fade", 1)] [InlineData("dissolve", 1.5)] [InlineData("custom", 2)] [InlineData("fade", 2.75)] [InlineData("dissolve", 3600)]
    public void TransitionChoicesAcceptTheNamedAndCustomDurationsIndependentlyOfPostProcessing(string kind, double duration)
    {
        using var fixture = new TempProject(); fixture.Write("effects/custom.kneshader", "shader fixture");
        var settings = new ScenePerformanceSettings { PostProcessing = false, EntryTransition = new() { Kind = kind, Duration = duration, Shader = "effects/custom.kneshader" }, ExitTransition = new() { Kind = kind, Duration = duration, Shader = "effects/custom.kneshader" } };
        settings.Validate(fixture.ProjectPath); Assert.False(settings.PostProcessing); Assert.Equal(kind, settings.EntryTransition.Kind); Assert.Equal(kind, settings.ExitTransition.Kind);
    }

    [Theory]
    [InlineData("unknown", 1)] [InlineData("fade", 0)] [InlineData("fade", -.001)] [InlineData("fade", 3600.001)]
    [InlineData("fade", double.NaN)] [InlineData("dissolve", double.PositiveInfinity)]
    public void TransitionsRejectInvalidKindsAndNonpositiveNonfiniteOrExcessiveDurations(string kind, double duration)
        => Assert.Throws<InvalidDataException>(() => new SceneTransitionSettings { Kind = kind, Duration = duration }.Validate());

    [Fact]
    public void CustomTransitionsRequireAnExistingProjectShaderAndSceneMusicRequiresAnExistingAudioResource()
    {
        using var fixture = new TempProject();
        Assert.Throws<InvalidDataException>(() => new SceneTransitionSettings { Kind = "custom" }.Validate(fixture.ProjectPath));
        Assert.Throws<InvalidDataException>(() => new SceneTransitionSettings { Kind = "custom", Shader = "missing.kneshader" }.Validate(fixture.ProjectPath));
        Assert.Throws<InvalidDataException>(() => new SceneTransitionSettings { Kind = "custom", Shader = "wrong.png" }.Validate(fixture.ProjectPath));
        Assert.Throws<ArgumentException>(() => new SceneTransitionSettings { Kind = "custom", Shader = "../outside.kneshader" }.Validate(fixture.ProjectPath));
        fixture.Write("music/theme.wav", "audio fixture"); fixture.Write("music/text.txt", "text fixture");
        new ScenePerformanceSettings { Music = "music/theme.wav", MusicVolume = 0, LoopMusic = true }.Validate(fixture.ProjectPath);
        Assert.Throws<FileNotFoundException>(() => new ScenePerformanceSettings { Music = "music/missing.wav" }.Validate(fixture.ProjectPath));
        Assert.Throws<ArgumentException>(() => new ScenePerformanceSettings { Music = "music/text.txt" }.Validate(fixture.ProjectPath));
        Assert.Throws<ArgumentException>(() => new ScenePerformanceSettings { Music = "../theme.wav" }.Validate(fixture.ProjectPath));
    }

    [Fact]
    public async Task FunctionDocumentRejectsNormalizedDuplicateFilesInvalidNamesAndTheCombinedCapacityOverflow()
    {
        using var fixture = new TempProject();
        var duplicate = new ScenePerformanceDocument { Functions = new() { ["chapter.krl"] = new() { ["one"] = new() }, ["sub/../chapter.krl"] = new() { ["two"] = new() } } };
        Assert.Throws<InvalidDataException>(() => duplicate.Validate(fixture.ProjectPath));
        var invalid = new ScenePerformanceDocument { Functions = new() { ["chapter.krl"] = new() { ["not a function"] = new() } } }; Assert.Throws<InvalidDataException>(() => invalid.Validate(fixture.ProjectPath));
        invalid.Functions = new() { ["notes.txt"] = new() { ["chapter"] = new() } }; Assert.Throws<InvalidDataException>(() => invalid.Validate(fixture.ProjectPath));
        invalid.Functions = new() { ["../chapter.krl"] = new() { ["chapter"] = new() } }; Assert.Throws<ArgumentException>(() => invalid.Validate(fixture.ProjectPath));
        var capacity = new ScenePerformanceDocument { Scenes = new() { ["legacy.krl"] = new() }, Functions = new() { ["chapter.krl"] = Enumerable.Range(0, 4999).ToDictionary(i => "scene" + i, _ => new ScenePerformanceSettings()) } };
        capacity.Validate(fixture.ProjectPath); capacity.Functions["chapter.krl"].Add("overflow", new()); Assert.Throws<InvalidDataException>(() => capacity.Validate(fixture.ProjectPath));
        string path = Path.Combine(fixture.ProjectPath, "engine/scene-performance.json"); byte[] before = File.ReadAllBytes(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, capacity)); Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task SceneCatalogSeparatesNamedFunctionsWithinOneKrlAndTheGraphFunctionWithoutExecutingSources()
    {
        using var fixture = new TempProject();
        fixture.Write("chapters.krl", "error('catalog must not execute'); function opening() end; function ending() end; local function helper() end");
        fixture.Write("helper.lua", "function lua_helper() end"); var graph = VisualScript.Create(); graph.FunctionName = "visual"; fixture.Write("chapter.knegraph", graph.Serialize());
        var project = await fixture.LoadAsync(); var before = project.Files.ToDictionary(file => file.FilePath, file => File.ReadAllBytes(file.FilePath)); var entries = SceneDefinition.Read(project);
        Assert.Equal(new[] { "opening", "ending" }, entries.Where(e => e.Path == "chapters.krl").Select(e => e.Function));
        Assert.All(entries.Where(e => e.Path == "chapters.krl"), e => Assert.False(e.Graph));
        var visual = Assert.Single(entries, e => e.Path == "chapter.knegraph"); Assert.True(visual.Graph); Assert.Equal("visual", visual.Function); Assert.Equal("chapter.knegraph::visual", visual.Key);
        Assert.DoesNotContain(entries, e => e.Path == "helper.lua" || e.Function == "helper"); Assert.Equal(entries.Count, entries.Select(e => e.Key).Distinct().Count());
        foreach (var file in before) Assert.Equal(file.Value, File.ReadAllBytes(file.Key));
    }

    static Task SaveSingle(TempProject fixture, ScenePerformanceSettings settings)
        => PerformanceSettingsStore.SaveScenesAsync(fixture.ProjectPath, new() { Functions = new() { ["chapter.krl"] = new() { ["chapter"] = settings } } });
}
